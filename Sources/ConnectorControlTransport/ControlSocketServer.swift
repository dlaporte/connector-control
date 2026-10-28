import Foundation
import ConnectorControlCore
import ConnectorControlState

/// Mac only: the Windows counterpart is windows/src/ConnectorControl.Core/Transport/ControlPipeServer.cs.
///
/// The app's socket, serving `ControlServer`. Each connection is read on a thread of its own and
/// written on a queue of its own; what it reads reaches the server on the main actor, through the
/// app's `AppHost`, so the server and the tools never leave it.
public final class ControlSocketServer: @unchecked Sendable {
    /// A client that has read nothing for this long has its connection closed: its lines would
    /// otherwise pile up in the app.
    public static let writeTimeout = 30

    public let path: String
    private let listener: ControlSocketListener
    private let acceptQueue = DispatchQueue(label: "com.dlaporte.connector-control.accept")

    private init(path: String, listener: ControlSocketListener) {
        self.path = path
        self.listener = listener
    }

    /// Listens at `path` and hands every connection from `allowedUID` — this user, but for a test —
    /// to `server`. Anyone else's is closed before a byte is read.
    public static func start(path: String, server: ControlServer, host: AppHost,
                             allowedUID: uid_t = getuid()) throws -> ControlSocketServer {
        let listener = try ControlSocketListener.listen(at: path)
        let endpoint = ControlSocketServer(path: path, listener: listener)
        listener.start(queue: endpoint.acceptQueue) { stream in
            guard stream.peerUID == allowedUID else {
                stream.close()
                return
            }
            stream.setWriteTimeout(seconds: writeTimeout)
            let connection = SocketConnection(stream: stream)
            host.marshal { _ = server.open(connection) }
            Thread.detachNewThread { connection.readAll(server: server, host: host) }
        }
        return endpoint
    }

    /// The app is quitting: no new connections, and no socket file left behind.
    public func stop() {
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
