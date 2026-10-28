using System.Runtime.Versioning;
using System.Text;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// Windows only: the Mac counterpart is Tests/ConnectorControlStateTests/TestSupport/SocketRig.swift.
///
/// A <see cref="ControlRig"/> served on a real named pipe, under a name of the test's own, so it never
/// meets the app a developer may be running.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PipeRig : IDisposable
{
    private readonly TimeSpan writeTimeout;

    public PipeRig(bool integration = true, bool confirm = true, TimeSpan? writeTimeout = null)
    {
        this.writeTimeout = writeTimeout ?? ControlPipeServer.WriteTimeout;
        Rig = new ControlRig(integration, confirm);
        Name = "ConnectorControl-test-" + Guid.NewGuid().ToString("N");
        Server = new ControlServer(Rig.Tools);
        Endpoint = ControlPipeServer.Start(Name, Server, Rig.H.Host, timeout: this.writeTimeout);
    }

    public ControlRig Rig { get; }
    public string Name { get; }
    public ControlServer Server { get; }
    public ControlPipeServer? Endpoint { get; private set; }

    /// <summary>The app quitting: the pipe goes, and every connection with it.</summary>
    public void Stop()
    {
        Endpoint?.Dispose();
        Endpoint = null;
        Server.CloseAll();
    }

    /// <summary>The app starting again, with the same state and the same write timeout.</summary>
    public void Restart() => Endpoint = StartAgain();

    /// <summary>The app's pipe served again over the same server, for the caller to keep and dispose: what
    /// <see cref="Restart"/> keeps itself, and what a launch the relay asks for brings up. Windows may take a
    /// moment to let the old pipe's name go after its last handle closes, so the first instance is retried
    /// until it is free. Safe off the test's thread: it reads only what never changes.</summary>
    public ControlPipeServer StartAgain()
    {
        var deadline = DateTime.UtcNow + Wait.Eventually;
        while (true)
        {
            try
            {
                return ControlPipeServer.Start(Name, Server, Rig.H.Host, timeout: writeTimeout);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Runs what the endpoint marshalled until <paramref name="condition"/> holds; false after <see cref="Wait.Eventually"/>.</summary>
    public bool Until(Func<bool> condition) => Rig.H.Ui.PumpUntil(condition, Wait.Eventually);

    public PipeClient Connect() =>
        new(ControlPipe.TryConnect(Name, TimeSpan.FromSeconds(5)) ?? throw new InvalidOperationException("The test pipe did not answer."));

    public void Dispose()
    {
        Stop();
        Rig.Dispose();
    }
}

/// <summary>A client of the pipe: it writes lines, and reads the server's on a task of its own.</summary>
internal sealed class PipeClient
{
    private readonly Lock gate = new();
    private readonly List<string> received = [];
    private bool ended;

    public PipeClient(IControlStream stream)
    {
        Stream = stream;
        // A thread of its own, as the Mac's: this read blocks, and a pool thread per client would starve the pool.
        new Thread(() =>
        {
            var framer = new McpLineFramer(64 * 1_048_576);
            while (true)
            {
                var chunk = stream.Read(65_536);
                if (chunk.Length == 0)
                {
                    break;
                }
                foreach (var frame in framer.Append(chunk))
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
            lock (gate)
            {
                ended = true;
            }
        })
        { IsBackground = true }.Start();
    }

    public IControlStream Stream { get; }

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

    /// <summary>Writes each line and its newline; a refused write shows in what comes back.</summary>
    public void Say(params string[] lines)
    {
        foreach (var line in lines)
        {
            Stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
        }
    }

    public void Send(byte[] bytes) => Stream.Write(bytes);

    public void Hello(string client = ControlHello.CliClient, string version = "1.2.2") => Say(ControlHello.Line(client, version));

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
}
