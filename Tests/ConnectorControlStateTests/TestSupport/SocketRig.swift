import Foundation
import XCTest
import ConnectorControlCore
@testable import ConnectorControlState
@testable import ConnectorControlTransport

/// Mac only: the Windows counterpart is windows/tests/ConnectorControl.Core.Tests/TestSupport/PipeRig.cs.
///
/// A `ControlRig` served on a real Unix socket. The socket sits in a short folder under /tmp: a socket
/// path holds at most 103 bytes, and a `TempDir` under the user's temporary folder can pass that.
@MainActor
final class SocketRig {
    let rig: ControlRig
    let folder: String
    let path: String
    let server: ControlServer
    private(set) var endpoint: ControlSocketServer?
    private let allowedUID: uid_t
    private let writeTimeout: Double

    init(integration: Bool = true, confirm: Bool = true, allowedUID: uid_t = getuid(),
         writeTimeout: Double = Double(ControlSocketServer.writeTimeout)) throws {
        self.allowedUID = allowedUID
        self.writeTimeout = writeTimeout
        rig = ControlRig(integration: integration, confirm: confirm)
        var template = Array("/tmp/cc-XXXXXX".utf8CString)
        guard mkdtemp(&template) != nil else { throw ControlSocketError.system("mkdtemp", errno) }
        folder = String(cString: template)
        // mkdtemp makes the folder 0700 already; opened up first, so the tests see the listener close it.
        guard chmod(folder, 0o755) == 0 else { throw ControlSocketError.system("chmod", errno) }
        path = folder + "/" + ControlEndpoint.socketName
        server = ControlServer(tools: rig.tools)
        endpoint = try ControlSocketServer.start(path: path, server: server, host: rig.h.host, allowedUID: allowedUID,
                                                timeout: writeTimeout)
    }

    /// The app quitting: the socket goes, and every connection with it.
    func stop() {
        endpoint?.stop()
        endpoint = nil
        server.closeAll()
    }

    /// The app starting again, with the same state.
    func restart() throws {
        endpoint = try ControlSocketServer.start(path: path, server: server, host: rig.h.host, allowedUID: allowedUID,
                                                timeout: writeTimeout)
    }

    func dispose() {
        stop()
        try? FileManager.default.removeItem(atPath: folder)
        rig.dispose()
    }

    /// Runs what the endpoint marshalled until `condition` holds; false after `Wait.eventually`.
    func until(_ condition: () -> Bool) -> Bool {
        rig.h.ui.pumpUntil(condition, timeout: Wait.eventually)
    }

    func connect() throws -> SocketClient {
        SocketClient(try ControlSocketStream.connect(to: path))
    }
}

/// A client of the socket: it writes lines, and reads the server's on a thread of its own.
final class SocketClient: @unchecked Sendable {
    let stream: ControlStream
    private let lock = NSLock()
    private var received: [String] = []
    private var ended = false

    init(_ stream: ControlStream) {
        self.stream = stream
        Thread.detachNewThread { [self] in
            var framer = McpLineFramer(limit: 64 * 1_048_576)
            while true {
                let chunk = stream.read(maximum: 65_536)
                if chunk.isEmpty { break }
                for case .line(let bytes) in framer.append(chunk) {
                    let line = String(decoding: bytes, as: UTF8.self)
                    lock.withLock { received.append(line) }
                }
            }
            lock.withLock { ended = true }
        }
    }

    var lines: [String] { lock.withLock { received } }
    var isEnded: Bool { lock.withLock { ended } }

    /// Writes each line and its newline. A write the server refuses is the test's to notice by what
    /// comes back, so the result is not asserted here.
    func say(_ lines: String...) {
        for line in lines { _ = stream.write(Data((line + "\n").utf8)) }
    }

    func send(_ bytes: Data) { _ = stream.write(bytes) }

    func hello(_ client: String = ControlHello.cliClient, version: String = "1.2.2") {
        say(ControlHello.line(client: client, version: version))
    }

    /// The answers so far, by request id, each request's in the order they came: a request is
    /// checked against its own answers, not against where they fell among the others'.
    func answers() -> [JSONValue: [String]] {
        var grouped: [JSONValue: [String]] = [:]
        for line in lines {
            guard case .object(let fields)? = try? JSONValue.parse(Data(line.utf8)), fields["method"] == nil,
                  let id = fields["id"] else { continue }
            grouped[id, default: []].append(line)
        }
        return grouped
    }
}
