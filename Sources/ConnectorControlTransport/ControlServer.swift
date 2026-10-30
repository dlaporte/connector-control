import Foundation
import ConnectorControlCore
import ConnectorControlState

/// Mirror: windows/src/ConnectorControl.Core/Transport/ControlServer.cs (IControlConnection)
///
/// One client of the app's endpoint, as the server sees it. Called on the main actor; `send` must
/// not wait for the client to read - a client that stopped reading would stall the app - so each
/// endpoint queues the line and writes it on a thread of the connection's own.
public protocol ControlConnection: AnyObject, Sendable {
    /// Queues one line for the client; the newline is the connection's to add.
    func send(_ line: String)
    /// Ends the connection. Safe to call more than once.
    func close()
}

/// Mirror: windows/src/ConnectorControl.Core/Transport/ControlServer.cs
///
/// The app's end of the control layer, apart from any socket or pipe. Each connection's first line
/// says who it is (`ControlHello`); the connection then gets a `ControlToolHost` and an `McpSession`
/// of its own over the app's one `ControlTools`, which every connection shares - the write queue
/// that runs changes one at a time is that one instance's. A handful of connections are served at
/// once: Claude Desktop opens two relays, beside any command line. Main-actor only: the endpoint
/// hands each frame over on the main thread, where the tools run.
@MainActor
public final class ControlServer {
    public static let maxConnections = 8

    public let tools: ControlTools
    /// Oldest first.
    private var links: [Link] = []

    public init(tools: ControlTools) {
        self.tools = tools
    }

    public var connectionCount: Int { links.count }

    /// The hosts of the connections that have said hello, oldest first.
    public var hosts: [ControlToolHost] { links.compactMap(\.host) }

    /// Takes a new connection. One past `maxConnections` is closed unanswered.
    @discardableResult
    public func open(_ connection: ControlConnection) -> Bool {
        guard links.count < ControlServer.maxConnections else {
            connection.close()
            return false
        }
        links.append(Link(connection))
        return true
    }

    /// One frame read from `connection`: its hello first, then its messages.
    public func receive(_ frame: McpFrame, from connection: ControlConnection) {
        guard let link = links.first(where: { $0.connection === connection }) else { return }
        guard let session = link.session else {
            greet(link, frame)
            return
        }
        switch frame {
        case .line(let bytes): session.receive(bytes)
        case .tooLarge: session.receiveTooLarge()
        }
    }

    /// The connection has ended, or is to end. Its host stops counting as open, so a write it left
    /// waiting for its turn is dropped.
    public func close(_ connection: ControlConnection) {
        guard let index = links.firstIndex(where: { $0.connection === connection }) else { return }
        let link = links.remove(at: index)
        link.end()
        connection.close()
    }

    /// Every connection, as the app quits.
    public func closeAll() {
        // Taken and cleared first: a connection whose `close` calls back into `close` must not change
        // the list this walks, nor be closed a second time.
        let closing = links
        links.removeAll()
        for link in closing {
            link.end()
            link.connection.close()
        }
    }

    /// The hello decides the caller, and is consumed here: it never reaches the session.
    private func greet(_ link: Link, _ frame: McpFrame) {
        let hello: ControlHello
        if case .line(let bytes) = frame { hello = ControlHello.parse(bytes) } else { hello = .unknown }
        let host = ControlToolHost(tools: tools, isCLI: hello.isCLI)
        host.clientVersion = hello.version
        // A call that was waiting when its connection closed is still answered, once the user
        // responds, and that answer goes to the session of a connection that is gone: nothing is
        // written to it. Here, releasing the link at close already makes the weak capture nil; the
        // closed flag is what stops it on Windows, where the closure holds the link.
        let session = McpSession(host: host, version: host.version, send: { [weak link] line in
            guard let link, !link.isClosed else { return }
            link.connection.send(line)
        })
        host.session = session
        link.host = host
        link.session = session
    }

    /// One connection and, once it has said hello, its host and session. The link holds the
    /// session; the host's reference to it is weak, and the session's send holds the link weakly.
    @MainActor private final class Link {
        let connection: ControlConnection
        var host: ControlToolHost?
        var session: McpSession?
        private(set) var isClosed = false

        init(_ connection: ControlConnection) {
            self.connection = connection
        }

        /// The connection is going: its host stops counting as open and its session stops writing.
        func end() {
            isClosed = true
            host?.isOpen = false
        }
    }
}
