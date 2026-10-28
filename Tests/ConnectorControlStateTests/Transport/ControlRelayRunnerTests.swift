import XCTest
import ConnectorControlCore
@testable import ConnectorControlState
@testable import ConnectorControlTransport

/// Mac only: the Windows counterpart is windows/tests/ConnectorControl.Core.Tests/Transport/ControlRelayRunnerTests.cs.
///
/// The relay between a stand-in for Claude — two pipes — and the app's real socket.
@MainActor
final class ControlRelayRunnerTests: XCTestCase {
    /// Claude's end: what it says goes to the relay's standard input; what the relay writes to its
    /// standard output is read here on a thread of its own.
    final class Claude: @unchecked Sendable {
        let toRelay = Pipe()
        let fromRelay = Pipe()
        private let lock = NSLock()
        private var received: [String] = []
        private var closed = false

        init() {
            let reading = fromRelay.fileHandleForReading
            Thread.detachNewThread { [self] in
                var framer = McpLineFramer(limit: 64 * 1_048_576)
                while true {
                    let chunk = reading.availableData
                    if chunk.isEmpty { break }
                    for case .line(let bytes) in framer.append(chunk) {
                        let line = String(decoding: bytes, as: UTF8.self)
                        lock.withLock { received.append(line) }
                    }
                }
            }
        }

        var lines: [String] { lock.withLock { received } }

        func say(_ line: String) { toRelay.fileHandleForWriting.write(Data((line + "\n").utf8)) }

        /// Claude closes its side: the relay's standard input ends. Safe to call more than once.
        func close() {
            let first = lock.withLock {
                defer { closed = true }
                return !closed
            }
            if first { try? toRelay.fileHandleForWriting.close() }
        }

        /// The relay has ended, so nothing writes to its standard output any more: closing it ends the
        /// thread reading it. Never sooner — the relay writes to the bare descriptor, which by then could
        /// be something else's.
        func relayEnded() { try? fromRelay.fileHandleForWriting.close() }

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

    private let initialize = ControlServerTests.initializeMessage
    private let initialized = ControlServerTests.initializedMessage

    private func call(_ id: Int, _ name: String = "get_status", _ arguments: String = "{}") -> String {
        #"{"jsonrpc":"2.0","id":\#(id),"method":"tools/call","params":{"name":"\#(name)","arguments":\#(arguments)}}"#
    }

    private func run(_ claude: Claude, _ client: ControlClient) -> (ControlRelayRunner, Box<Int32>) {
        let runner = ControlRelayRunner(client: client, version: "1.2.2",
                                        input: claude.toRelay.fileHandleForReading.fileDescriptor,
                                        output: claude.fromRelay.fileHandleForWriting.fileDescriptor)
        let exit = Box<Int32>()
        Thread.detachNewThread { exit.set(runner.run()) }
        return (runner, exit)
    }

    /// Every test ends here, whatever it asserted: Claude closes its side, the relay is waited for, and
    /// only once it has ended is its standard output closed, so no thread the test started is left behind.
    private func end(_ claude: Claude, _ exit: Box<Int32>, _ socket: SocketRig) {
        claude.close()
        guard socket.until({ exit.value != nil }) else { return XCTFail("the relay did not end once Claude closed its side") }
        claude.relayEnded()
    }

    /// The closures run on the relay's queue, not the main actor: @Sendable, so none is isolated to it.
    private func client(_ socket: SocketRig, launch: (@Sendable () -> Bool)? = nil) -> ControlClient {
        let path = socket.path
        return ControlClient(connectOnce: { try? ControlSocketStream.connect(to: path) }, launch: launch, announce: nil)
    }

    func testTheRelayCarriesClaudeThroughAnAppRestart() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let claude = Claude()
        let (runner, exit) = run(claude, client(socket))
        defer { end(claude, exit, socket) }
        claude.say(initialize)
        claude.say(initialized)
        claude.say(call(2))
        XCTAssertTrue(socket.until { claude.answers()[.int(2)] != nil })
        socket.stop()
        XCTAssertTrue(socket.until { !runner.isConnected }, "the relay notices the app has gone")
        try socket.restart()
        claude.say(call(3))
        XCTAssertTrue(socket.until { claude.answers()[.int(3)] != nil })
        XCTAssertEqual(claude.answers()[.int(1)]?.count, 1, "Claude's own handshake was answered once")
        XCTAssertFalse(claude.lines.contains { $0.contains(ControlRelay.replayIDPrefix) }, "the replay's answer never reached Claude")
        XCTAssertTrue(claude.answers()[.int(3)]?.first?.contains(#""isError":false"#) == true)
        XCTAssertEqual(socket.server.hosts.last?.session?.clientName, "claude-ai", "the new connection knows who is asking")
        claude.close()
        XCTAssertTrue(socket.until { exit.value != nil })
        XCTAssertEqual(exit.value, ControlOutput.doneExit)
    }

    func testARequestInFlightWhenTheAppGoesIsToldToTryAgain() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let claude = Claude()
        let (_, exit) = run(claude, client(socket))
        defer { end(claude, exit, socket) }
        claude.say(initialize)
        claude.say(initialized)
        claude.say(call(2, "delete_connectors", #"{"names":["aws-mcp"]}"#))
        XCTAssertTrue(socket.until { socket.rig.confirmer.waitingCount == 1 })
        socket.stop()
        XCTAssertTrue(socket.until { claude.answers()[.int(2)] != nil })
        XCTAssertTrue(claude.answers()[.int(2)]?.first?.contains(ControlRelay.restartedError) == true)
        socket.rig.confirmer.answer(false)
    }

    func testTheRelayStartsTheAppWhenNothingAnswers() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        socket.stop()
        let started = Box<ControlSocketServer>()
        let path = socket.path, server = socket.server, host = socket.rig.h.host
        defer { started.value?.stop() }
        let claude = Claude()
        let (_, exit) = run(claude, client(socket, launch: {
            guard let endpoint = try? ControlSocketServer.start(path: path, server: server, host: host) else { return false }
            started.set(endpoint)
            return true
        }))
        defer { end(claude, exit, socket) }
        claude.say(initialize)
        XCTAssertTrue(socket.until { claude.answers()[.int(1)] != nil })
        XCTAssertNotNil(started.value, "the app was started")
        XCTAssertTrue(claude.answers()[.int(1)]?.first?.contains(#""serverInfo""#) == true)
    }

    func testAFailedFirstHandshakeEndsTheRelay() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        socket.stop()
        let claude = Claude()
        let (_, exit) = run(claude, client(socket))
        defer { end(claude, exit, socket) }
        claude.say(initialize)
        // The answer is written before the relay ends, but read here on a thread of its own: both are waited for.
        XCTAssertTrue(socket.until { exit.value != nil && claude.answers()[.int(1)] != nil })
        XCTAssertEqual(exit.value, ControlOutput.unreachableExit)
        XCTAssertEqual(claude.answers()[.int(1)],
                       [McpSession.errorLine(code: McpSession.internalErrorCode, message: ControlClient.notRunningError, id: .int(1))])
    }
}
