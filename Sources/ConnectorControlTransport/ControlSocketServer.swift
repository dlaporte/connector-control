import Foundation
import ConnectorControlCore
import ConnectorControlState

/// Mac only: the Windows counterpart is windows/src/ConnectorControl.Core/Transport/ControlPipeServer.cs.
///
/// The app's socket, serving `ControlServer`. Each connection is read on a thread of its own and
/// written on a queue of its own; what it reads reaches the server on the main actor, through the
/// app's `AppHost`, so the server and the tools never leave it.
///
/// Two things the Windows pipe server has that this one does not. Its `stopped` callback reports an accept
/// loop that ends on its own, whether the pipe can take no more instances or the loop failed; here the
/// listener is a dispatch source with no loop that can die unseen, and a listener that cannot start throws
/// from `start`. Its remote-client check closes a client from another computer, which the pipe's access
/// list alone would admit; a Unix socket cannot be reached from another machine, and `peerUID` already
/// decides who is served.
public final class ControlSocketServer: @unchecked Sendable {
    /// A write that takes longer than this, in all, closes its connection: a client that stops reading,
    /// or reads too slowly, would otherwise hold its lines in the app.
    public static let writeTimeout = 30

    public let path: String
    private let listener: ControlSocketListener
    private let acceptQueue = DispatchQueue(label: "com.dlaporte.connector-control.accept")
    private let server: ControlServer
    private let host: AppHost
    private let allowedUID: uid_t
    private let timeout: Double
    private let lock = NSLock()
    private var stopped = false

    private init(path: String, listener: ControlSocketListener, server: ControlServer, host: AppHost,
                 allowedUID: uid_t, timeout: Double) {
        self.path = path
        self.listener = listener
        self.server = server
        self.host = host
        self.allowedUID = allowedUID
        self.timeout = timeout
    }

    /// Listens at `path` and hands every connection from `allowedUID` — this user, but for a test —
    /// to `server`. Anyone else's is closed before a byte is read. `timeout` is a test's way to a
    /// short write timeout.
    public static func start(path: String, server: ControlServer, host: AppHost, allowedUID: uid_t = getuid(),
                             timeout: Double = Double(writeTimeout)) throws -> ControlSocketServer {
        let listener = try ControlSocketListener.listen(at: path)
        let endpoint = ControlSocketServer(path: path, listener: listener, server: server, host: host,
                                           allowedUID: allowedUID, timeout: timeout)
        listener.start(queue: endpoint.acceptQueue) { [endpoint] stream in endpoint.accept(stream) }
        return endpoint
    }

    /// One new connection, on the accept queue.
    func accept(_ stream: ControlSocketStream) {
        guard stream.peerUID == allowedUID, !isStopped else {
            stream.close()
            return
        }
        stream.setWriteTimeout(seconds: timeout)
        let connection = SocketConnection(stream: stream)
        let (server, host) = (self.server, self.host)
        // Checked again where the server is: an accept already running when the app stopped posts its
        // connection after `closeAll`, which would otherwise be served by an app that has quit.
        host.marshal { [self] in
            guard !isStopped else {
                connection.close()
                return
            }
            _ = server.open(connection)
        }
        Thread.detachNewThread { connection.readAll(server: server, host: host) }
    }

    private var isStopped: Bool { lock.withLock { stopped } }

    /// The app is quitting: no new connections, and no socket file left behind. Called before the
    /// server closes every connection, so none opens after that.
    public func stop() {
        lock.withLock { stopped = true }
        listener.close()
    }
}

/// One client, as the server sees it.
final class SocketConnection: ControlConnection, @unchecked Sendable {
    let stream: ControlSocketStream
    private let writes = DispatchQueue(label: "com.dlaporte.connector-control.connection")

    init(stream: ControlSocketStream) {
        self.stream = stream
    }

    func send(_ line: String) {
        let data = Data((line + "\n").utf8)
        writes.async { [stream] in
            if !stream.write(data) { stream.close() }
        }
    }

    func close() {
        stream.close()
    }

    /// Reads until the client goes; each frame, and then the end, reaches the server in order.
    func readAll(server: ControlServer, host: AppHost) {
        var framer = McpLineFramer()
        while true {
            let chunk = stream.read(maximum: 65_536)
            if chunk.isEmpty { break }
            for frame in framer.append(chunk) {
                host.marshal { server.receive(frame, from: self) }
            }
        }
        host.marshal { server.close(self) }
    }
}
