using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Transport;

/// <summary>
/// Windows only: the Mac counterpart is Sources/ConnectorControlTransport/ControlSocketServer.swift.
///
/// The app's pipe, serving <see cref="ControlServer"/>. Each connection is read and written on tasks of
/// its own; what it reads reaches the server on the UI thread, through the app's <see cref="AppHost"/>,
/// so the server and the tools never leave it. A new instance always exists before the last is handed
/// on, so while the app runs the name is never free for anything else to take.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ControlPipeServer : IDisposable
{
    /// <summary>A write that takes longer than this, in all, closes its connection: a client that stops
    /// reading, or reads too slowly, would otherwise hold its lines in the app.</summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

    private readonly ControlServer server;
    private readonly AppHost host;
    private readonly TimeSpan timeout;
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    /// <summary>The instance waiting for the next client, held from the moment it is made until a client
    /// connects to it: <see cref="Dispose"/> disposes it at once. Once connected an instance is no longer
    /// held here, and is disposed under the same lock when it is not served, or kept by its connection
    /// when handed on; so after <see cref="Dispose"/> only a served connection can still hold the name.</summary>
    private NamedPipeServerStream? waiting;
    private Action? stopped;

    private ControlPipeServer(string name, ControlServer server, AppHost host, TimeSpan timeout)
    {
        Name = name;
        this.server = server;
        this.host = host;
        this.timeout = timeout;
    }

    public string Name { get; }

    /// <summary>Creates the pipe's first instance - which throws if anything already holds the name - and
    /// serves it. <paramref name="stopped"/> runs on the UI thread if serving ever stops on its own: the pipe
    /// would take no more instances, or the accept loop failed for any other reason. The Mac's socket has no
    /// such case, so its server takes no such action. A name something else holds surfaces as
    /// <see cref="UnauthorizedAccessException"/> (ERROR_ACCESS_DENIED under FIRST_PIPE_INSTANCE) or
    /// <see cref="IOException"/>, for the app to catch and report. <paramref name="timeout"/> is a test's way
    /// to a short write timeout.</summary>
    public static ControlPipeServer Start(string name, ControlServer server, AppHost host, Action? stopped = null,
        TimeSpan? timeout = null)
    {
        var first = ControlPipe.CreateServer(name, first: true);
        // Held from the start, so a Dispose that comes before the accept loop has begun still lets the name go.
        var endpoint = new ControlPipeServer(name, server, host, timeout ?? WriteTimeout) { stopped = stopped, waiting = first };
        _ = Task.Run(() => endpoint.AcceptAsync(first));
        return endpoint;
    }

    /// <summary>The app is quitting: no new connections, and no instance still waiting for one keeps the name.
    /// A connection already handed on keeps its instance, and so the name, until the server closes it:
    /// the app calls this and then <see cref="ControlServer.CloseAll"/>, both on the UI thread, and
    /// <see cref="Hand"/>'s second check relies on that order, as the Mac's stop() does.</summary>
    public void Dispose()
    {
        NamedPipeServerStream? last;
        lock (gate)
        {
            stopping.Cancel();
            last = waiting;
            waiting = null;
        }
        last?.Dispose();
    }

    /// <summary>The accept loop, and the one place it can die unseen: anything but the app stopping that ends
    /// it tells the app, which would otherwise be left not serving without knowing.</summary>
    private async Task AcceptAsync(NamedPipeServerStream first)
    {
        try
        {
            await ServeAsync(first).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The instance still waiting, if any, is let go; one handed on is its connection's, and Hand
            // disposes its own when it fails.
            NamedPipeServerStream? last;
            lock (gate)
            {
                last = waiting;
                waiting = null;
            }
            last?.Dispose();
            if (!stopping.IsCancellationRequested)
            {
                host.Marshal(() => stopped?.Invoke());
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream first)
    {
        var current = first;
        while (true)
        {
            lock (gate)
            {
                if (stopping.IsCancellationRequested)
                {
                    current.Dispose();
                    return;
                }
                waiting = current;
            }
            bool connected;
            try
            {
                await current.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                // A client on another computer is closed unanswered, as one that left before it was taken.
                connected = IsLocal(current);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                current.Dispose();   // stopped: Dispose may already have done this, and twice is harmless
                return;
            }
            catch (IOException)
            {
                connected = false;   // a client that left before it was taken
            }
            NamedPipeServerStream? next = null;
            lock (gate)
            {
                waiting = null;
                if (stopping.IsCancellationRequested)
                {
                    // Dispose has run: nothing more is made, so the name is let go as it promised.
                    current.Dispose();
                    return;
                }
                try
                {
                    // Made before this one is handed on or let go, so the name is never free for anything else.
                    next = ControlPipe.CreateServer(Name, first: false);
                    waiting = next;   // held at once, so a Dispose from here on frees it too
                }
                catch (Exception ex) when (FileSystemErrors.IsTransient(ex))
                {
                    next = null;
                }
                finally
                {
                    if (!connected)
                    {
                        // Let go under the lock, so a Dispose cannot return while it still holds the name.
                        current.Dispose();
                    }
                }
            }
            if (connected)
            {
                Hand(current);
            }
            if (next is null)
            {
                // The pipe takes no more instances: this one was the last served, and the app says so.
                host.Marshal(() => stopped?.Invoke());
                return;
            }
            current = next;
        }
    }

    private static bool IsLocal(NamedPipeServerStream pipe)
    {
        try
        {
            return ControlPipe.IsLocalClient(pipe);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>One new connection, from the accept loop.</summary>
    internal void Hand(NamedPipeServerStream pipe)
    {
        var connection = new PipeConnection(pipe, server, host, timeout);
        try
        {
            host.Marshal(() =>
            {
                // Checked again where the server is: a connection accepted just as the app stopped is posted
                // after CloseAll, which would otherwise be served by an app that has quit.
                if (stopping.IsCancellationRequested)
                {
                    connection.Close();
                    return;
                }
                server.Open(connection);
            });
            connection.Start();
        }
        catch (Exception)
        {
            // Never handed on: the client sees the end of its stream, and the instance lets the name go.
            connection.Close();
            throw;
        }
    }

    /// <summary>Writes all of <paramref name="bytes"/> within <paramref name="timeout"/> of starting, counted
    /// as a whole: a reader that takes a little at a time does not start the wait over. False when it
    /// cannot, because the client is too slow or has gone.</summary>
    internal static async Task<bool> TryWriteAsync(PipeStream pipe, byte[] bytes, TimeSpan timeout)
    {
        try
        {
            using var deadline = new CancellationTokenSource(timeout);
            await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>One client, as the server sees it.</summary>
    private sealed class PipeConnection(NamedPipeServerStream pipe, ControlServer server, AppHost host, TimeSpan timeout) : IControlConnection
    {
        private readonly Channel<byte[]> outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private int closed;

        public void Send(string line) => outgoing.Writer.TryWrite(Encoding.UTF8.GetBytes(line + "\n"));

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0)
            {
                outgoing.Writer.TryComplete();
                pipe.Dispose();
            }
        }

        public void Start()
        {
            _ = Task.Run(ReadAsync);
            _ = Task.Run(WriteAsync);
        }

        /// <summary>Reads until the client goes; each frame, and then the end, reaches the server in order.</summary>
        private async Task ReadAsync()
        {
            var framer = new McpLineFramer();
            var buffer = new byte[65_536];
            try
            {
                while (true)
                {
                    var count = await pipe.ReadAsync(buffer).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }
                    foreach (var frame in framer.Append(new ReadOnlySpan<byte>(buffer, 0, count)))
                    {
                        host.Marshal(() => server.Receive(frame, this));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                // the client went, or the app closed the connection
            }
            host.Marshal(() => server.Close(this));
        }

        private async Task WriteAsync()
        {
            await foreach (var bytes in outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (!await TryWriteAsync(pipe, bytes, timeout).ConfigureAwait(false))
                {
                    Close();
                    return;
                }
            }
        }
    }
}
