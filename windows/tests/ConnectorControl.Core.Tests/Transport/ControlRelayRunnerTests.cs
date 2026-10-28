using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using ConnectorControl.Core.Tests.TestSupport;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.Transport;

/// <summary>
/// Windows only: the Mac counterpart is Tests/ConnectorControlStateTests/Transport/ControlRelayRunnerTests.swift.
///
/// The relay between a stand-in for Claude — two anonymous pipes — and the app's real named pipe.
/// </summary>
[SupportedOSPlatform("windows")]
public class ControlRelayRunnerTests
{
    /// <summary>Claude's end: what it says goes to the relay's standard input; what the relay writes to its
    /// standard output is read here on a thread of its own.</summary>
    private sealed class Claude
    {
        private readonly AnonymousPipeServerStream toRelay = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream fromRelay = new(PipeDirection.In);
        private readonly Lock gate = new();
        private readonly List<string> received = [];
        private int closed;

        public Claude()
        {
            Input = new AnonymousPipeClientStream(PipeDirection.In, toRelay.ClientSafePipeHandle);
            Output = new AnonymousPipeClientStream(PipeDirection.Out, fromRelay.ClientSafePipeHandle);
            // A thread of its own, as the Mac's: this read blocks until the relay's output is closed.
            new Thread(() =>
            {
                var framer = new McpLineFramer(64 * 1_048_576);
                var buffer = new byte[65_536];
                try
                {
                    int count;
                    while ((count = fromRelay.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        foreach (var frame in framer.Append(new ReadOnlySpan<byte>(buffer, 0, count)))
                        {
                            if (frame is McpFrame.Line line)
                            {
                                lock (gate)
                                {
                                    received.Add(Encoding.UTF8.GetString(line.Bytes));
                                }
                            }
                        }
                    }
                }
                catch (IOException)
                {
                    // the relay's output has gone
                }
                finally
                {
                    fromRelay.Dispose();
                }
            })
            { IsBackground = true }.Start();
        }

        /// <summary>The relay's standard input.</summary>
        public Stream Input { get; }

        /// <summary>The relay's standard output.</summary>
        public Stream Output { get; }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (gate)
                {
                    return [.. received];
                }
            }
        }

        public void Say(string line)
        {
            toRelay.Write(Encoding.UTF8.GetBytes(line + "\n"));
            toRelay.Flush();
        }

        /// <summary>Claude closes its side: the relay's standard input ends. Safe to call more than once.</summary>
        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0)
            {
                toRelay.Dispose();
            }
        }

        /// <summary>The relay has ended, so nothing writes to its standard output any more: closing it ends the
        /// thread reading it. Never sooner — the relay could still be writing to it.</summary>
        public void RelayEnded()
        {
            Output.Dispose();
            Input.Dispose();
        }

        /// <summary>The answers so far, by request id, each request's in the order they came.</summary>
        public Dictionary<JsonValue, List<string>> Answers()
        {
            var grouped = new Dictionary<JsonValue, List<string>>();
            foreach (var line in Lines)
            {
                JsonValue message;
                try
                {
                    message = JsonValue.Parse(line);
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }
                if (message.Kind != JsonKind.Object || message["method"] is not null || message["id"] is not { } id)
                {
                    continue;
                }
                if (!grouped.TryGetValue(id, out var answers))
                {
                    grouped[id] = answers = [];
                }
                answers.Add(line);
            }
            return grouped;
        }

        public List<string>? AnswersTo(int id) => Answers().GetValueOrDefault(JsonValue.Int(id));
    }

    private const string Initialize = ControlServerTests.InitializeMessage;
    private const string Initialized = ControlServerTests.InitializedMessage;

    private static string Call(int id, string name = "get_status", string arguments = "{}") =>
        $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{name}}}","arguments":{{{arguments}}}}}""";

    /// <summary>The relay's exit code once it has ended: set on the relay's thread, read on the fact's.</summary>
    private sealed class ExitCode
    {
        private readonly Lock gate = new();
        private int? code;

        public int? Value
        {
            get
            {
                lock (gate)
                {
                    return code;
                }
            }
        }

        public void Set(int value)
        {
            lock (gate)
            {
                code = value;
            }
        }
    }

    /// <summary>The relay runs on a thread of its own, as the real tool's main thread would, and as the Mac's
    /// test runs it.</summary>
    private static (ControlRelayRunner Runner, ExitCode Exit) Run(Claude claude, ControlClient client)
    {
        var runner = new ControlRelayRunner(client, "1.2.2", claude.Input, claude.Output);
        var exit = new ExitCode();
        new Thread(() => exit.Set(runner.Run())) { IsBackground = true }.Start();
        return (runner, exit);
    }

    /// <summary>Every fact ends here, in a <c>finally</c>, whatever it asserted: Claude closes its side, the relay
    /// is waited for, and only once it has ended are its standard input and output closed, so no thread the
    /// fact started is left behind. That the relay ended is asserted only when the fact got that far: thrown
    /// from a <c>finally</c>, it would hide the failure that brought it here. Platform idiom: the Mac records
    /// the failure without throwing.</summary>
    private static void End(Claude claude, ExitCode exit, PipeRig pipe, bool asserted)
    {
        claude.Close();
        var ended = pipe.Until(() => exit.Value is not null);
        if (ended)
        {
            claude.RelayEnded();
        }
        Assert.True(ended || !asserted, "the relay did not end once Claude closed its side");
    }

    private static ControlClient Client(PipeRig pipe, Func<bool>? launch = null)
    {
        var name = pipe.Name;
        return new ControlClient(() => ControlPipe.TryConnect(name, TimeSpan.FromMilliseconds(200)), launch, announce: null);
    }

    [Fact]
    public void TheRelayCarriesClaudeThroughAnAppRestart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        var claude = new Claude();
        var (runner, exit) = Run(claude, Client(pipe));
        var asserted = false;
        try
        {
            claude.Say(Initialize);
            claude.Say(Initialized);
            claude.Say(Call(2));
            Assert.True(pipe.Until(() => claude.AnswersTo(2) is not null));
            pipe.Stop();
            Assert.True(pipe.Until(() => !runner.IsConnected), "the relay notices the app has gone");
            pipe.Restart();
            claude.Say(Call(3));
            Assert.True(pipe.Until(() => claude.AnswersTo(3) is not null));
            Assert.Single(claude.AnswersTo(1)!);   // Claude's own handshake was answered once
            Assert.DoesNotContain(claude.Lines, line => line.Contains(ControlRelay.ReplayIdPrefix, StringComparison.Ordinal));   // the replay's answer never reached Claude
            Assert.Contains("\"isError\":false", claude.AnswersTo(3)![0], StringComparison.Ordinal);
            Assert.Equal("claude-ai", pipe.Server.Hosts[^1].Session?.ClientName);   // the new connection knows who is asking
            claude.Close();
            Assert.True(pipe.Until(() => exit.Value is not null));
            Assert.Equal(ControlOutput.DoneExit, exit.Value);
            asserted = true;
        }
        finally
        {
            End(claude, exit, pipe, asserted);
        }
    }

    [Fact]
    public void ARequestInFlightWhenTheAppGoesIsToldToTryAgain()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        var claude = new Claude();
        var (_, exit) = Run(claude, Client(pipe));
        var asserted = false;
        try
        {
            claude.Say(Initialize);
            claude.Say(Initialized);
            claude.Say(Call(2, "delete_connectors", """{"names":["aws-mcp"]}"""));
            Assert.True(pipe.Until(() => pipe.Rig.Confirmer.WaitingCount == 1));
            pipe.Stop();
            Assert.True(pipe.Until(() => claude.AnswersTo(2) is not null));
            Assert.Contains(ControlRelay.RestartedError, claude.AnswersTo(2)![0], StringComparison.Ordinal);
            pipe.Rig.Confirmer.Answer(false);
            asserted = true;
        }
        finally
        {
            End(claude, exit, pipe, asserted);
        }
    }

    [Fact]
    public void TheRelayStartsTheAppWhenNothingAnswers()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        pipe.Stop();
        ControlPipeServer? started = null;
        var claude = new Claude();
        var (_, exit) = Run(claude, Client(pipe, launch: () =>
        {
            // On the relay's thread: the rig's retry waits for Windows to let the stopped pipe's name go.
            Volatile.Write(ref started, pipe.StartAgain());
            return true;
        }));
        var asserted = false;
        try
        {
            claude.Say(Initialize);
            Assert.True(pipe.Until(() => claude.AnswersTo(1) is not null));
            Assert.NotNull(Volatile.Read(ref started));   // the app was started
            Assert.Contains("\"serverInfo\"", claude.AnswersTo(1)![0], StringComparison.Ordinal);
            asserted = true;
        }
        finally
        {
            End(claude, exit, pipe, asserted);
            Volatile.Read(ref started)?.Dispose();
        }
    }

    [Fact]
    public void AFailedFirstHandshakeEndsTheRelay()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        pipe.Stop();
        var claude = new Claude();
        var (_, exit) = Run(claude, Client(pipe));
        var asserted = false;
        try
        {
            claude.Say(Initialize);
            // The answer is written before the relay ends, but read here on a thread of its own: both are waited for.
            Assert.True(pipe.Until(() => exit.Value is not null && claude.AnswersTo(1) is not null));
            Assert.Equal(ControlOutput.UnreachableExit, exit.Value);
            Assert.Equal([McpSession.ErrorLine(McpSession.InternalErrorCode, ControlClient.NotRunningError, JsonValue.Int(1))],
                claude.AnswersTo(1));
            asserted = true;
        }
        finally
        {
            End(claude, exit, pipe, asserted);
        }
    }
}
