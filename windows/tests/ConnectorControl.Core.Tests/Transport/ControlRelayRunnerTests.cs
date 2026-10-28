using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using ConnectorControl.Core.Tests.TestSupport;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.Transport;

/// <summary>
/// Windows only: the Mac counterpart is Tests/ConnectorControlStateTests/Transport/ControlRelayRunnerTests.swift.
///
/// The relay between a stand-in for Claude — two anonymous pipes — and the app's real named pipe, or, where a
/// fact must decide what the app has sent when, a scripted stand-in for the app. The facts over the real pipe
/// are Windows only; the scripted one runs everywhere.
/// </summary>
[SupportedOSPlatform("windows")]
public class ControlRelayRunnerTests
{
    /// <summary>Claude's end: what it says goes to the relay's standard input; what the relay writes to its
    /// standard output is read here on a thread of its own, unless the fact reads nothing.</summary>
    private sealed class Claude
    {
        private readonly AnonymousPipeServerStream toRelay = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream fromRelay = new(PipeDirection.In);
        private readonly Lock gate = new();
        private readonly List<string> received = [];
        private int closed;
        private bool ended;

        public Claude(bool reading = true)
        {
            Input = new AnonymousPipeClientStream(PipeDirection.In, toRelay.ClientSafePipeHandle);
            Output = new AnonymousPipeClientStream(PipeDirection.Out, fromRelay.ClientSafePipeHandle);
            if (!reading)
            {
                ended = true;
                return;
            }
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
                    lock (gate)
                    {
                        ended = true;
                    }
                }
            })
            { IsBackground = true }.Start();
        }

        /// <summary>The relay's standard input.</summary>
        public Stream Input { get; }

        /// <summary>The relay's standard output.</summary>
        public Stream Output { get; }

        /// <summary>Everything the relay wrote has been read: its standard output has closed.</summary>
        public bool IsEnded
        {
            get
            {
                lock (gate)
                {
                    return ended;
                }
            }
        }

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

        /// <summary>Claude goes away without a word: the read end of the relay's standard output closes. Only for
        /// a Claude that reads nothing, so no thread of the fact's is reading it.</summary>
        public void StopReading() => fromRelay.Dispose();

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

    /// <summary>The app's end of one connection, scripted: it keeps what the relay writes, hands the relay what the
    /// fact sends, and refuses every write once the fact says the app has gone.</summary>
    private sealed class ScriptedApp : IControlStream
    {
        private readonly object gate = new();
        private readonly List<string> written = [];
        private readonly List<string> refused = [];
        private readonly Queue<byte[]> toRead = new();
        private bool finished;
        private bool closed;
        private bool gone;
        private bool writeClosed;

        public byte[] Read(int maximum)
        {
            lock (gate)
            {
                while (toRead.Count == 0 && !finished && !closed)
                {
                    Monitor.Wait(gate);
                }
                return closed || toRead.Count == 0 ? [] : toRead.Dequeue();
            }
        }

        public bool Write(byte[] data)
        {
            lock (gate)
            {
                var lines = Encoding.UTF8.GetString(data).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (gone || writeClosed || closed)
                {
                    refused.AddRange(lines);
                    return false;
                }
                written.AddRange(lines);
                return true;
            }
        }

        public void CloseWrite()
        {
            lock (gate)
            {
                writeClosed = true;
            }
        }

        public void Close()
        {
            lock (gate)
            {
                closed = true;
                Monitor.PulseAll(gate);
            }
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (gate)
                {
                    return [.. written];
                }
            }
        }

        public IReadOnlyList<string> RefusedLines
        {
            get
            {
                lock (gate)
                {
                    return [.. refused];
                }
            }
        }

        public bool IsWriteClosed
        {
            get
            {
                lock (gate)
                {
                    return writeClosed;
                }
            }
        }

        /// <summary>The app sends a line, which waits for the relay to read it.</summary>
        public void Send(string line)
        {
            lock (gate)
            {
                toRead.Enqueue(Encoding.UTF8.GetBytes(line + "\n"));
                Monitor.PulseAll(gate);
            }
        }

        /// <summary>The app goes: every write from now on fails, while what it sent is still there to be read.</summary>
        public void Goes()
        {
            lock (gate)
            {
                gone = true;
            }
        }

        /// <summary>The stream ends once what the app sent has been read.</summary>
        public void Finish()
        {
            lock (gate)
            {
                finished = true;
                Monitor.PulseAll(gate);
            }
        }
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
    /// fact started is left behind. Then, with all the relay wrote read, each request it answered was answered
    /// once. All this is asserted only when the fact got that far: thrown from a <c>finally</c>, it would hide
    /// the failure that brought it here. Platform idiom: the Mac records the failure without throwing.</summary>
    private static void End(Claude claude, ExitCode exit, PipeRig? pipe, bool asserted)
    {
        claude.Close();
        var ended = Until(pipe, () => exit.Value is not null);
        if (ended)
        {
            claude.RelayEnded();
        }
        if (!asserted)
        {
            return;
        }
        Assert.True(ended, "the relay did not end once Claude closed its side");
        Assert.True(Until(pipe, () => claude.IsEnded), "the relay's standard output did not close");
        Assert.All(claude.Answers(), answers => Assert.Single(answers.Value));
    }

    /// <summary>Waits for <paramref name="condition"/>, running what the pipe's endpoint marshalled when there is one.</summary>
    private static bool Until(PipeRig? pipe, Func<bool> condition) =>
        pipe?.Until(condition) ?? Wait.Until(condition, Wait.Eventually);

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

    /// <summary>The app answers a call and goes; the relay, not yet having read that answer, fails to write the
    /// next call. The answer still reaches Claude as the app gave it — a call that went through is never told
    /// to try again — and only the call that never arrived is. Scripted, so it runs on every platform.</summary>
    [Fact]
    public void AnAnswerTheAppSentBeforeItWentReachesClaudeAfterAFailedWrite()
    {
        var app = new ScriptedApp();
        var claude = new Claude();
        var (_, exit) = Run(claude, new ControlClient(() => app, launch: null, announce: null));
        var asserted = false;
        try
        {
            claude.Say(Initialize);
            Assert.True(Until(null, () => app.Lines.Contains(Initialize)));
            app.Send("""{"id":1,"jsonrpc":"2.0","result":{}}""");
            Assert.True(Until(null, () => claude.AnswersTo(1) is not null));
            claude.Say(Call(2));
            Assert.True(Until(null, () => app.Lines.Contains(Call(2))));
            app.Goes();
            claude.Say(Call(3));
            Assert.True(Until(null, () => app.RefusedLines.Contains(Call(3))));
            const string answered = """{"id":2,"jsonrpc":"2.0","result":{"content":[],"isError":false,"structuredContent":{}}}""";
            app.Send(answered);
            app.Finish();
            Assert.True(Until(null, () => claude.AnswersTo(2) is not null && claude.AnswersTo(3) is not null));
            Assert.Equal([answered], claude.AnswersTo(2));   // the app's own answer, not restarted
            Assert.Contains(ControlRelay.RestartedError, claude.AnswersTo(3)![0], StringComparison.Ordinal);
            Assert.True(app.IsWriteClosed, "only the writing half was ended");
            asserted = true;
        }
        finally
        {
            End(claude, exit, null, asserted);
        }
    }

    /// <summary>Claude going away while the relay writes to it ends the relay as any failed write to Claude does.
    /// Windows reports the broken pipe as an <see cref="IOException"/>; the Mac's counterpart also proves the
    /// relay is not ended by SIGPIPE instead.</summary>
    [Fact]
    public void TheRelayEndsWhenClaudeGoesWhileItWrites()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var pipe = new PipeRig();
        var claude = new Claude(reading: false);
        var (_, exit) = Run(claude, Client(pipe));
        var asserted = false;
        try
        {
            claude.StopReading();
            claude.Say(Initialize);
            Assert.True(pipe.Until(() => exit.Value is not null));
            Assert.Equal(ControlOutput.DoneExit, exit.Value);
            asserted = true;
        }
        finally
        {
            End(claude, exit, pipe, asserted);
        }
    }
}
