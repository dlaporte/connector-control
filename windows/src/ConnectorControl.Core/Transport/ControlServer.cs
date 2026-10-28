using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Transport;

/// <summary>
/// Mirror: Sources/ConnectorControlTransport/ControlServer.swift (ControlConnection)
///
/// One client of the app's endpoint, as the server sees it. Called on the UI thread; <c>Send</c> must
/// not wait for the client to read — a client that stopped reading would stall the app — so each
/// endpoint queues the line and writes it on a thread of the connection's own.
/// </summary>
public interface IControlConnection
{
    /// <summary>Queues one line for the client; the newline is the connection's to add.</summary>
    void Send(string line);

    /// <summary>Ends the connection. Safe to call more than once.</summary>
    void Close();
}

/// <summary>
/// Mirror: Sources/ConnectorControlTransport/ControlServer.swift
///
/// The app's end of the control layer, apart from any socket or pipe. Each connection's first line
/// says who it is (<see cref="ControlHello"/>); the connection then gets a <see cref="ControlToolHost"/>
/// and an <see cref="McpSession"/> of its own over the app's one <see cref="ControlTools"/>, which every
/// connection shares — the write queue that runs changes one at a time is that one instance's. A
/// handful of connections are served at once: Claude Desktop opens two relays, beside any command line.
/// </summary>
public sealed class ControlServer(ControlTools tools)
{
    public const int MaxConnections = 8;

    public ControlTools Tools { get; } = tools;
    /// <summary>Oldest first.</summary>
    private readonly List<Link> links = [];

    public int ConnectionCount => links.Count;

    /// <summary>The hosts of the connections that have said hello, oldest first.</summary>
    public IReadOnlyList<ControlToolHost> Hosts =>
        links.Where(link => link.Host is not null).Select(link => link.Host!).ToList();

    /// <summary>Takes a new connection. One past <c>MaxConnections</c> is closed unanswered.</summary>
    public bool Open(IControlConnection connection)
    {
        if (links.Count >= MaxConnections)
        {
            connection.Close();
            return false;
        }
        links.Add(new Link(connection));
        return true;
    }

    /// <summary>One frame read from <paramref name="connection"/>: its hello first, then its messages.</summary>
    public void Receive(McpFrame frame, IControlConnection connection)
    {
        var link = links.FirstOrDefault(link => ReferenceEquals(link.Connection, connection));
        if (link is null)
        {
            return;
        }
        if (link.Session is not { } session)
        {
            Greet(link, frame);
            return;
        }
        switch (frame)
        {
            case McpFrame.Line line:
                session.Receive(line.Bytes);
                break;
            case McpFrame.TooLarge:
                session.ReceiveTooLarge();
                break;
        }
    }

    /// <summary>The connection has ended, or is to end. Its host stops counting as open, so a write it
    /// left waiting for its turn is dropped.</summary>
    public void Close(IControlConnection connection)
    {
        var index = links.FindIndex(link => ReferenceEquals(link.Connection, connection));
        if (index < 0)
        {
            return;
        }
        var link = links[index];
        links.RemoveAt(index);
        link.End();
        connection.Close();
    }

    /// <summary>Every connection, as the app quits.</summary>
    public void CloseAll()
    {
        foreach (var link in links)
        {
            link.End();
            link.Connection.Close();
        }
        links.Clear();
    }

    /// <summary>The hello decides the caller, and is consumed here: it never reaches the session.</summary>
    private void Greet(Link link, McpFrame frame)
    {
        var hello = frame is McpFrame.Line line ? ControlHello.Parse(line.Bytes) : ControlHello.Unknown;
        var host = new ControlToolHost(Tools, hello.IsCli) { ClientVersion = hello.Version };
        // A call that was waiting when its connection closed is still answered, once the user
        // responds, and that answer goes to the session of a connection that is gone: nothing is
        // written to it.
        var session = new McpSession(host, host.Version, text =>
        {
            if (!link.IsClosed)
            {
                link.Connection.Send(text);
            }
        });
        host.Session = session;
        link.Host = host;
        link.Session = session;
    }

    /// <summary>One connection and, once it has said hello, its host and session. The GC has no cycle
    /// to break, where the Mac's host holds its session weakly and its session's send holds the link
    /// weakly.</summary>
    private sealed class Link(IControlConnection connection)
    {
        public IControlConnection Connection { get; } = connection;
        public ControlToolHost? Host { get; set; }
        public McpSession? Session { get; set; }
        public bool IsClosed { get; private set; }

        /// <summary>The connection is going: its host stops counting as open and its session stops writing.</summary>
        public void End()
        {
            IsClosed = true;
            if (Host is not null)
            {
                Host.IsOpen = false;
            }
        }
    }
}
