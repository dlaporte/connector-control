using System.Collections.Concurrent;
using System.Text;

namespace ConnectorControl.Core.Transport;

/// <summary>
/// Mirror: Sources/ConnectorControlTransport/ControlRelayRunner.swift
///
/// Runs <see cref="ControlRelay"/> between Claude - the relay's standard input and output - and the app.
/// A task reads each side; everything the relay decides happens on the thread that called
/// <see cref="Run"/>, one event at a time in the order it arrived, connecting included, so what Claude
/// sends while the app starts waits its turn. Platform idiom: tasks and a blocking collection here;
/// threads and a dispatch queue on the Mac. A reader here can throw, so each ends in a <c>finally</c>
/// that gives the relay its last word however it failed; the Mac's readers cannot throw, and read a
/// failure as the end of their streams.
/// </summary>
public sealed class ControlRelayRunner(ControlClient client, string version, Stream input, Stream output)
{
    private readonly BlockingCollection<Action> events = [];
    private readonly ControlRelay relay = new();
    private IControlStream? stream;
    /// <summary>Which connection a reader belongs to: what a reader of a connection already given up reads is ignored.</summary>
    private int generation;
    private int? exitCode;
    private volatile bool connected;

    /// <summary>Whether the relay holds a connection to the app now. Platform idiom: a copy the relay's thread
    /// keeps after each event, so asking never waits; the Mac asks on the relay's queue, and waits while a
    /// connection is being made.</summary>
    public bool IsConnected => connected;

    /// <summary>Relays until Claude closes its side or the relay must end, and returns the exit code.</summary>
    public int Run()
    {
        Read(ReadClaude);
        foreach (var work in events.GetConsumingEnumerable())
        {
            work();
            connected = relay.IsConnected;
            if (exitCode is { } code)
            {
                return code;
            }
        }
        return ControlOutput.DoneExit;
    }

    /// <summary>A reader blocks for as long as its side stays open, so it runs as a long-running task - a
    /// thread of its own, as the Mac's readers are - and never holds a pool thread that long.</summary>
    private static void Read(Action reading) =>
        _ = Task.Factory.StartNew(reading, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void ReadClaude()
    {
        var framer = new McpLineFramer();
        var buffer = new byte[65_536];
        try
        {
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                foreach (var frame in framer.Append(new ReadOnlySpan<byte>(buffer, 0, count)))
                {
                    events.Add(() => FromClaude(frame));
                }
            }
            foreach (var frame in framer.Finish())
            {
                events.Add(() => FromClaude(frame));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            // Claude's side has gone
        }
        finally
        {
            // However the reading ended, the relay hears that Claude closed its side; without this, Run
            // would wait for good.
            events.Add(() => Perform(relay.ClaudeClosed()));
        }
    }

    /// <summary>What Claude sent that the app would refuse unread is refused here in the app's words: the relay
    /// cannot forward a line it did not keep.</summary>
    private void FromClaude(McpFrame frame)
    {
        if (exitCode is not null)
        {
            return;
        }
        if (frame is McpFrame.Line line)
        {
            if (McpSession.StrictUtf8(line.Bytes) is not { } text)
            {
                ToClaude(McpSession.ErrorLine(McpSession.ParseErrorCode, McpSession.ParseErrorMessage, JsonValue.Null));
                return;
            }
            Perform(relay.FromClaude(text));
        }
        else
        {
            ToClaude(McpSession.ErrorLine(McpSession.InvalidRequestCode, McpSession.TooLargeMessage, JsonValue.Null));
        }
    }

    private void Perform(IReadOnlyList<ControlRelay.Action> actions)
    {
        foreach (var action in actions)
        {
            if (exitCode is not null)
            {
                return;
            }
            switch (action)
            {
                case ControlRelay.Action.ToApp toApp:
                    // A write that fails ends the writing half, as far as CloseWrite can. What the app sent
                    // before it went is still read and passed on, and the end of the stream after it gives the
                    // connection up: a call that went through is never told to try again, and one this write or
                    // a later one carried is told so once.
                    if (stream is { } current && !current.Write(Encoding.UTF8.GetBytes(toApp.Line + "\n")))
                    {
                        current.CloseWrite();
                    }
                    break;
                case ControlRelay.Action.ToClaude toClaude:
                    ToClaude(toClaude.Line);
                    break;
                case ControlRelay.Action.Connect:
                    IControlStream connection;
                    try
                    {
                        connection = client.Connect();
                    }
                    catch (Exception ex)
                    {
                        // Any other failure leaves the app unreachable too.
                        Perform(relay.ConnectFailed(ex is ControlClient.UnreachableException unreachable
                            ? ControlClient.Message(unreachable.Reason) : ControlClient.UnreachableError));
                        break;
                    }
                    Perform(Attach(connection) ? relay.ConnectSucceeded() : relay.ConnectFailed(ControlClient.UnreachableError));
                    break;
                case ControlRelay.Action.Exit exit:
                    Finish(exit.Code);
                    break;
            }
        }
    }

    /// <summary>Starts reading a new connection once its hello has gone out; false, with the connection closed, when
    /// the hello could not be written. Nothing else may go on such a connection: the app reads its first line as
    /// the hello, and Claude's own first line, read so, could pass for the command line.</summary>
    private bool Attach(IControlStream connection)
    {
        if (!connection.Write(Encoding.UTF8.GetBytes(ControlHello.Line(ControlHello.RelayClient, version) + "\n")))
        {
            connection.Close();
            return false;
        }
        var mine = ++generation;
        stream = connection;
        Read(() =>
        {
            // The app's answers may be far longer than what Claude may send: a whole configuration, say.
            var framer = new McpLineFramer(ControlClient.MaxAnswerBytes);
            try
            {
                while (true)
                {
                    var chunk = connection.Read(65_536);
                    if (chunk.Length == 0)
                    {
                        break;
                    }
                    foreach (var frame in framer.Append(chunk))
                    {
                        events.Add(() =>
                        {
                            if (mine != generation || exitCode is not null)
                            {
                                return;
                            }
                            if (frame is McpFrame.Line line)
                            {
                                Perform(relay.FromApp(Encoding.UTF8.GetString(line.Bytes)));
                            }
                            else
                            {
                                Lose();
                            }
                        });
                    }
                }
            }
            finally
            {
                // However the reading ended, the relay hears that the connection has gone; without this, a
                // request it forwarded would wait for good.
                events.Add(() =>
                {
                    if (mine == generation)
                    {
                        Lose();
                    }
                });
            }
        });
        return true;
    }

    /// <summary>The connection has gone, or is given up: the relay answers what it was waiting for.</summary>
    private void Lose()
    {
        stream?.Close();
        stream = null;
        generation++;
        Perform(relay.ConnectionLost());
    }

    /// <summary>A line for Claude. When Claude's side has gone there is nobody left to relay for. Platform idiom: a
    /// Claude that has gone is a broken pipe, which .NET reports as an <see cref="IOException"/>; the Mac asks
    /// for EPIPE in place of SIGPIPE.</summary>
    private void ToClaude(string line)
    {
        try
        {
            output.Write(Encoding.UTF8.GetBytes(line + "\n"));
            output.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException or InvalidOperationException)
        {
            Finish(ControlOutput.DoneExit);
        }
    }

    private void Finish(int code)
    {
        if (exitCode is not null)
        {
            return;
        }
        exitCode = code;
        stream?.Close();
        stream = null;
    }
}
