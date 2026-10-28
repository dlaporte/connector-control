using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.Transport;

/// <summary>
/// Windows only: the Mac counterpart is Tests/ConnectorControlStateTests/Transport/ControlSocketTests.swift.
///
/// The control layer on a real pipe: what reaches the server, what it answers, and who it lets in.
///
/// Four of the Mac's facts have no pipe to test, and the file says so here rather than leaving them out
/// unremarked. A pipe leaves no file behind, so there is no stale socket to replace, no file that is not a
/// socket to leave alone, no socket whose connect fails for another reason, and no listener that is started
/// twice or after it is closed (testALiveSocketIsLeftAloneAndAStaleOneIsReplaced,
/// testAFileThatIsNotASocketIsLeftAlone, testASocketThatFailsForAnotherReasonIsLeftAlone,
/// testStartingAClosedOrStartedListenerDoesNothing); the live-name half of the first is the first-instance
/// fact below. A pipe has no folder, so none can be a link or a file or too open
/// (testAFolderThatIsALinkIsRefused, testAFileWhereTheFolderShouldBeIsRefused, and the folder half of the
/// first fact), and its name has no length limit like a socket path's (testAPathTooLongForASocketIsRefusedBeforeAnythingIsMade).
/// Who may connect is the access list, not a peer-id check the server makes per connection, so the
/// other-user facts (testAnotherUsersConnectionIsClosedBeforeAnythingIsRead, testTheToolRefusesAServerAnotherUserRuns,
/// testARestartedServerStillRefusesAnotherUser) are the access-list fact below; the one rule the server
/// does apply per connection, that a client is on this computer, is its own fact.
/// </summary>
[SupportedOSPlatform("windows")]
public class ControlPipeTests
{
    private const string InitializeMessage = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"claude-ai","version":"1"}}}""";
    private const string InitializedMessage = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
    private const string ParseError = """{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}""";
    private const string DeleteAws = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"delete_connectors","arguments":{"names":["aws-mcp"]}}}""";

    /// <summary>The short write timeout the tests give the server, and how long a write past it may take
    /// to be cut: generous, since only a write that is never cut matters.</summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CutBound = 3 * ShortTimeout + TimeSpan.FromSeconds(1);

    /// <summary>The pipe's access list names this user and nobody else, and a second first instance under
    /// the same name is refused, so nothing can take the name from the app or stand in for it.</summary>
    [Fact]
    public void ThePipeIsThisUsersAloneAndTheNameCannotBeTaken()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var name = "ConnectorControl-test-" + Guid.NewGuid().ToString("N");
        using var first = ControlPipe.CreateServer(name, first: true);
        // CurrentUserOnly keys on the token's owner, not its user: .NET grants the pipe to
        // WindowsIdentity.Owner and makes it the pipe's owner, and the client compares the pipe's owner
        // with its own token's. An elevated token's owner is BUILTIN\Administrators — which is why an
        // elevated Claude and an app that is not reach different owners. The name still carries .User.
        var owner = WindowsIdentity.GetCurrent().Owner!;
        var security = first.GetAccessControl();
        Assert.Equal(owner, security.GetOwner(typeof(SecurityIdentifier)));
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.Equal(owner, rule.IdentityReference));
        var taken = Record.Exception(() => ControlPipe.CreateServer(name, first: true));
        Assert.True(taken is IOException or UnauthorizedAccessException, taken?.GetType().Name ?? "no exception");
        Assert.Null(ControlPipe.TryConnect("ConnectorControl-test-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(100)));
    }

    /// <summary>The first check of how Windows answers for a local client: if it fails, every other pipe
    /// fact fails with it, and the evidence is in the CI log.</summary>
    [Fact]
    public void AClientOnThisComputerIsLocal()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var name = "ConnectorControl-test-" + Guid.NewGuid().ToString("N");
        using var server = ControlPipe.CreateServer(name, first: true);
        var connecting = Accepting(server);
        var client = ControlPipe.TryConnect(name, TimeSpan.FromSeconds(5));
        Assert.NotNull(client);
        Assert.True(Done(connecting, Wait.Eventually));
        Assert.True(ControlPipe.IsLocalClient(server));
        client.Close();
    }

    [Fact]
    public void RequestsAreAnsweredEachUnderItsOwnId()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        var client = pipe.Connect();
        client.Hello();
        client.Say(InitializeMessage, InitializedMessage,
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"get_status","arguments":{}}}""");
        Assert.True(pipe.Until(() => client.Answers().Count == 4));
        var answers = client.Answers();
        Assert.Equal(4, answers.Count);
        Assert.All([1, 2, 3, 4], id => Assert.True(answers.ContainsKey(JsonValue.Int(id)), $"id {id}"));
        Assert.All(answers.Values, lines => Assert.Single(lines));
        Assert.Equal(["""{"id":2,"jsonrpc":"2.0","result":{}}"""], answers[JsonValue.Int(2)]);
        Assert.Contains("\"name\":\"get_status\"", answers[JsonValue.Int(3)][0]);
        Assert.Contains("\"isError\":false", answers[JsonValue.Int(4)][0]);
    }

    [Fact]
    public void AMessageOverOneMegabyteIsRefusedAndTheConnectionCarriesOn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        var client = pipe.Connect();
        client.Hello();
        client.Send([.. Enumerable.Repeat((byte)0x20, McpSession.MaxMessageBytes + 1), (byte)'\n']);
        client.Say("""{"jsonrpc":"2.0","id":5,"method":"ping"}""");
        Assert.True(pipe.Until(() => client.Lines.Count == 2));
        Assert.Equal(["""{"error":{"code":-32600,"message":"Message too large"},"id":null,"jsonrpc":"2.0"}""",
                      """{"id":5,"jsonrpc":"2.0","result":{}}"""], client.Lines);
    }

    /// <summary>Bytes that are not UTF-8 are refused as they arrive, so both platforms answer the same bytes
    /// the same way whatever their decoders would have made of them.</summary>
    [Fact]
    public void MalformedJsonAndBytesThatAreNotUtf8AreParseErrors()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        var client = pipe.Connect();
        client.Hello();
        client.Say("""{"jsonrpc":""");
        byte[][] bads = [[0xC3, 0x28], [0xC0, 0xAF], [0xED, 0xA0, 0x80], [0xFF]];
        foreach (var bad in bads)
        {
            // A regular string, since a raw literal cannot end in a quote.
            client.Send([.. Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"x\":\""), .. bad, .. "\"}}\n"u8]);
        }
        client.Say("""{"jsonrpc":"2.0","id":6,"method":"ping"}""");
        Assert.True(pipe.Until(() => client.Lines.Count == 6));
        Assert.Equal([.. Enumerable.Repeat(ParseError, 5), """{"id":6,"jsonrpc":"2.0","result":{}}"""], client.Lines);
    }

    /// <summary>A string that ends in an escaped backslash is closed by the quote after it, so the brackets
    /// that follow count: 62 more levels make 64, the limit; 63 make 65.</summary>
    [Fact]
    public void TheNestingScannerSeesPastAnEscapedBackslash()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        var client = pipe.Connect();
        client.Hello();
        static string Ping(int id, int depth) =>
            $$"""{"jsonrpc":"2.0","id":{{id}},"method":"ping","params":{"s":"\\","d":""" + new string('[', depth) + new string(']', depth) + "}}";
        client.Say(Ping(1, 62), Ping(2, 63));
        Assert.True(pipe.Until(() => client.Lines.Count == 2));
        Assert.Equal(["""{"id":1,"jsonrpc":"2.0","result":{}}""", ParseError], client.Lines);
    }

    /// <summary>Anything but a well-formed command-line hello is gated: each of these is asked about, and
    /// declining refuses the change.</summary>
    [Fact]
    public void TheHelloFailsClosed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        string[] hellos = ["""{"client":"cli"}""", """{"client":"CLI","version":"1.2.2"}""", "hello", InitializeMessage];
        for (var index = 0; index < hellos.Length; index++)
        {
            var hello = hellos[index];
            var client = pipe.Connect();
            client.Say(hello, InitializeMessage, InitializedMessage, DeleteAws);
            Assert.True(pipe.Until(() => pipe.Rig.Confirmer.Requests.Count == index + 1), hello);
            pipe.Rig.Confirmer.Answer(false);
            Assert.True(pipe.Until(() => client.Answers().ContainsKey(JsonValue.Int(2))), hello);
            Assert.Contains(ControlTools.DeclinedError, client.Answers()[JsonValue.Int(2)][0]);
        }
        Assert.True(pipe.Rig.State.Store.Mcps.ContainsKey("aws-mcp"));
        var cli = pipe.Connect();
        cli.Hello();
        cli.Say(InitializeMessage, InitializedMessage, DeleteAws);
        Assert.True(pipe.Until(() => cli.Answers().ContainsKey(JsonValue.Int(2))));
        Assert.Equal(hellos.Length, pipe.Rig.Confirmer.Requests.Count);   // the command line is not asked
        Assert.False(pipe.Rig.State.Store.Mcps.ContainsKey("aws-mcp"));
    }

    [Fact]
    public void OneControlToolsServesEveryConnection()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        var clients = Enumerable.Range(0, 3).Select(_ => pipe.Connect()).ToList();
        clients[0].Hello();
        clients[1].Hello(ControlHello.RelayClient);
        clients[2].Hello(ControlHello.RelayClient);
        Assert.True(pipe.Until(() => pipe.Server.Hosts.Count == 3));
        Assert.All(pipe.Server.Hosts, host => Assert.Same(pipe.Rig.Tools, host.Tools));
        GC.KeepAlive(clients);
    }

    [Fact]
    public void AConnectionPastTheLimitIsClosedUnanswered()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        var clients = Enumerable.Range(0, ControlServer.MaxConnections).Select(_ => pipe.Connect()).ToList();
        Assert.True(pipe.Until(() => pipe.Server.ConnectionCount == ControlServer.MaxConnections));
        var extra = pipe.Connect();
        extra.Hello();
        extra.Say("""{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        Assert.True(pipe.Until(() => extra.IsEnded));
        Assert.Empty(extra.Lines);
        // The ones already in are still served, and none was dropped to make room.
        clients[0].Hello();
        clients[0].Say(InitializeMessage, InitializedMessage, """{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        Assert.True(pipe.Until(() => clients[0].Answers().ContainsKey(JsonValue.Int(2))));
        Assert.All(clients, client => Assert.False(client.IsEnded));
        Assert.Equal(ControlServer.MaxConnections, pipe.Server.ConnectionCount);
        GC.KeepAlive(clients);
    }

    [Fact]
    public void ANewerToolAskingForAToolThisAppLacksIsToldToRestartIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        var client = pipe.Connect();
        client.Hello(version: "99.0.0");
        client.Say(InitializeMessage, InitializedMessage, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"future_tool","arguments":{}}}""");
        Assert.True(pipe.Until(() => client.Answers().ContainsKey(JsonValue.Int(2))));
        Assert.Equal([McpSession.ErrorLine(McpSession.InvalidParamsCode, ControlTools.NeedsNewerAppError("99.0.0"), JsonValue.Int(2))],
            client.Answers()[JsonValue.Int(2)]);
    }

    /// <summary>A command line that goes away while its write waits behind a dialog takes the write with it.</summary>
    [Fact]
    public void ClosingTheConnectionDropsItsWaitingWrite()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        var relay = pipe.Connect();
        relay.Hello(ControlHello.RelayClient);
        relay.Say(InitializeMessage, InitializedMessage, DeleteAws);
        Assert.True(pipe.Until(() => pipe.Rig.Confirmer.WaitingCount == 1));
        var waiting = pipe.Rig.H.Delays.Pending.Count;
        var cli = pipe.Connect();
        cli.Hello();
        cli.Say(InitializeMessage, InitializedMessage,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"_meta":{"progressToken":"p"},"name":"create_collection","arguments":{"name":"Later"}}}""");
        // Its write reached the server and is queued behind the relay's, so closing drops a write
        // that was waiting, not one that never arrived.
        // Progress reaches a call only once it waits its turn, so hearing it proves the write is queued.
        Assert.True(pipe.Until(() => pipe.Rig.H.Delays.Pending.Count > waiting));
        foreach (var _ in pipe.Rig.H.Delays.Pending.ToList())
        {
            pipe.Rig.H.Delays.RunNext();
        }
        Assert.True(pipe.Until(() => cli.Lines.Any(line => line.Contains("\"progressToken\":\"p\""))));
        cli.Stream.Close();
        Assert.True(pipe.Until(() => pipe.Server.ConnectionCount == 1));
        pipe.Rig.Confirmer.Answer(false);
        Assert.True(pipe.Until(() => relay.Answers().ContainsKey(JsonValue.Int(2))));
        Assert.False(pipe.Rig.State.Store.Collections.ContainsKey("Later"), "the command line's write never ran");
    }

    /// <summary>The server closes a client that never reads, within a few times the timeout, and the app goes on.</summary>
    [Fact]
    public void AClientThatNeverReadsIsClosedWithinTheTimeout()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig(writeTimeout: ShortTimeout);
        AssertAClientThatNeverReadsIsClosed(pipe);
    }

    /// <summary>The timeout survives the app starting again: the rig's settings are the rig's, not the
    /// first server's.</summary>
    [Fact]
    public void ARestartedServerKeepsItsWriteTimeout()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig(writeTimeout: ShortTimeout);
        pipe.Stop();
        pipe.Restart();
        AssertAClientThatNeverReadsIsClosed(pipe);
    }

    private static void AssertAClientThatNeverReadsIsClosed(PipeRig pipe)
    {
        var stream = ControlPipe.TryConnect(pipe.Name, TimeSpan.FromSeconds(5));
        Assert.NotNull(stream);
        var burst = new List<byte>();
        burst.AddRange(Encoding.UTF8.GetBytes(ControlHello.Line(ControlHello.CliClient, "1.2.2") + "\n"));
        burst.AddRange(Encoding.UTF8.GetBytes(InitializeMessage + "\n" + InitializedMessage + "\n"));
        for (var id = 2; id < 4_000; id++)
        {
            burst.AddRange(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"ping\"}\n"));
        }
        // The burst is written on a task of its own: its write may itself wait, since nothing here reads.
        _ = Background(() => stream.Write([.. burst]));
        Assert.True(pipe.Until(() => pipe.Server.ConnectionCount == 1));
        var began = Stopwatch.StartNew();
        Assert.True(pipe.Until(() => pipe.Server.ConnectionCount == 0));
        Assert.True(began.Elapsed < 3 * ShortTimeout + TimeSpan.FromSeconds(3), $"closed after {began.Elapsed}");
        stream.Close();
    }

    /// <summary>A connection accepted before the app stops, but opened after it has closed them all, is
    /// closed, not opened: the app that quit serves nobody.</summary>
    [Fact]
    public void AConnectionAcceptedBeforeStopButOpenedAfterIsClosedNotOpened()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        var endpoint = pipe.Endpoint!;
        var name = "ConnectorControl-test-" + Guid.NewGuid().ToString("N");
        var served = ControlPipe.CreateServer(name, first: true);
        var connecting = Accepting(served);
        var client = ControlPipe.TryConnect(name, TimeSpan.FromSeconds(5));
        Assert.NotNull(client);
        Assert.True(Done(connecting, Wait.Eventually));
        endpoint.Hand(served);
        pipe.Stop();
        pipe.Rig.H.Ui.Pump();
        Assert.Equal(0, pipe.Server.ConnectionCount);
        var read = Background(() => client.Read(1));
        Assert.True(Done(read, Wait.Eventually), "it is not closed");
        Assert.Empty(Outcome(read));   // it is closed
    }

    /// <summary>A write is bounded as a whole: a peer that reads nothing, and a line bigger than what is
    /// free in the pipe's buffer, end it at the deadline and not at some multiple of it.</summary>
    [Fact]
    public void AWriteToAPeerThatReadsNothingFailsAtTheDeadline()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var (server, client) = Pair();
        using (server)
        using (client)
        {
            var write = ControlPipeServer.TryWriteAsync(server, new byte[1 << 20], ShortTimeout);
            Assert.True(Done(write, CutBound), "the write is not cut");
            Assert.False(Outcome(write));
        }
    }

    /// <summary>A peer that reads a little at a time, each read within the timeout, does not keep a write
    /// going past the deadline: the wait does not start over when some of it is read.</summary>
    [Fact]
    public void AWriteToAPeerThatReadsSlowlyFailsAtTheDeadline()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var (server, client) = Pair();
        using (server)
        using (client)
        {
            _ = Background(() =>
            {
                var buffer = new byte[1_024];
                try
                {
                    while (client.Read(buffer, 0, buffer.Length) > 0)
                    {
                        Thread.Sleep(50);   // the pace of a slow reader, not an assertion
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // the test closed it
                }
            });
            var write = ControlPipeServer.TryWriteAsync(server, new byte[1 << 20], ShortTimeout);
            Assert.True(Done(write, CutBound), "the write is not cut");
            Assert.False(Outcome(write));
        }
    }

    private static Task Accepting(NamedPipeServerStream server) => server.WaitForConnectionAsync();

    private static Task<T> Background<T>(Func<T> work) => Task.Run(work);

    private static Task Background(Action work) => Task.Run(work);

    /// <summary>What a task that is <see cref="Done"/> came to.</summary>
    private static T Outcome<T>(Task<T> task) => task.Result;

    private static bool Done(Task task, TimeSpan limit) => task.Wait(limit);

    /// <summary>A connected pair of the server's kind of pipe and a client of the tool's.</summary>
    private static (NamedPipeServerStream Server, NamedPipeClientStream Client) Pair()
    {
        var name = "ConnectorControl-test-" + Guid.NewGuid().ToString("N");
        var server = ControlPipe.CreateServer(name, first: true);
        var connecting = Accepting(server);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        client.Connect((int)Wait.Eventually.TotalMilliseconds);
        Assert.True(Done(connecting, Wait.Eventually));
        return (server, client);
    }
}
