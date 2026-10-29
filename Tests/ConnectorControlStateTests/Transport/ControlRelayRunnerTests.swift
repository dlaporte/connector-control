import XCTest
import ConnectorControlCore
@testable import ConnectorControlState
@testable import ConnectorControlTransport

/// Mac only: the Windows counterpart is windows/tests/ConnectorControl.Core.Tests/Transport/ControlRelayRunnerTests.cs.
///
/// The relay between a stand-in for Claude — two pipes — and the app's real socket, or, where a test
/// must decide what the app has sent when, a scripted stand-in for the app.
@MainActor
final class ControlRelayRunnerTests: XCTestCase {
    /// Claude's end: what it says goes to the relay's standard input; what the relay writes to its
    /// standard output is read here on a thread of its own, unless the test reads nothing.
    final class Claude: @unchecked Sendable {
        let toRelay = Pipe()
        let fromRelay = Pipe()
        private let lock = NSLock()
        private var received: [String] = []
        private var closed = false
        private var ended = false

        init(reading: Bool = true) {
            guard reading else {
                ended = true
                return
            }
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
                lock.withLock { ended = true }
            }
        }

        var lines: [String] { lock.withLock { received } }

        /// Everything the relay wrote has been read: its standard output has closed.
        var isEnded: Bool { lock.withLock { ended } }

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

        /// Claude goes away without a word: the read end of the relay's standard output closes. Only for a
        /// Claude that reads nothing, so no thread of the test's is reading it.
        func stopReading() { try? fromRelay.fileHandleForReading.close() }

        /// The bytes the relay has written that Claude has not read: a pipe's size, as the Mac reports it.
        var unread: Int {
            var info = stat()
            return fstat(fromRelay.fileHandleForReading.fileDescriptor, &info) == 0 ? Int(info.st_size) : 0
        }

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

    /// The app's end of one connection, scripted: it keeps what the relay writes, hands the relay what the
    /// test sends, and refuses every write once the test says the app has gone.
    final class ScriptedApp: ControlStream, @unchecked Sendable {
        private let condition = NSCondition()
        private var written: [String] = []
        private var refused: [String] = []
        private var toRead: [Data] = []
        private var finished = false
        private var closed = false
        private var gone = false
        private var writeClosed = false

        func read(maximum: Int) -> Data {
            condition.lock()
            defer { condition.unlock() }
            while toRead.isEmpty, !finished, !closed { condition.wait() }
            return closed || toRead.isEmpty ? Data() : toRead.removeFirst()
        }

        func write(_ data: Data) -> Bool {
            condition.withLock {
                let lines = String(decoding: data, as: UTF8.self).split(separator: "\n").map(String.init)
                if gone || writeClosed || closed {
                    refused += lines
                    return false
                }
                written += lines
                return true
            }
        }

        func closeWrite() { condition.withLock { writeClosed = true } }

        func close() {
            condition.withLock {
                closed = true
                condition.broadcast()
            }
        }

        var lines: [String] { condition.withLock { written } }
        var refusedLines: [String] { condition.withLock { refused } }
        var isWriteClosed: Bool { condition.withLock { writeClosed } }

        /// The app sends a line, which waits for the relay to read it.
        func send(_ line: String) {
            condition.withLock {
                toRead.append(Data((line + "\n").utf8))
                condition.broadcast()
            }
        }

        /// The app goes: every write from now on fails, while what it sent is still there to be read.
        func goes() { condition.withLock { gone = true } }

        /// The stream ends once what the app sent has been read.
        func finish() {
            condition.withLock {
                finished = true
                condition.broadcast()
            }
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
    /// Then, with all the relay wrote read, each request it answered was answered once.
    private func end(_ claude: Claude, _ exit: Box<Int32>, _ socket: SocketRig?) {
        claude.close()
        guard until(socket, { exit.value != nil }) else { return XCTFail("the relay did not end once Claude closed its side") }
        claude.relayEnded()
        guard until(socket, { claude.isEnded }) else { return XCTFail("the relay's standard output did not close") }
        for (id, answers) in claude.answers() where answers.count != 1 {
            XCTFail("\(id) was answered \(answers.count) times: \(answers)")
        }
    }

    /// Waits for `condition`, running what the socket's endpoint marshalled when there is one.
    private func until(_ socket: SocketRig?, _ condition: () -> Bool) -> Bool {
        if let socket { return socket.until(condition) }
        let deadline = Date().addingTimeInterval(Wait.eventually)
        while !condition() {
            guard Date() < deadline else { return false }
            Thread.sleep(forTimeInterval: 0.01)
        }
        return true
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

    /// A relay Claude ran elevated starts nothing, or every other relay of the person's could not reach
    /// the app it started, and tells Claude why in place of its handshake's answer.
    func testAnElevatedRelayStartsNothingAndTellsClaudeWhy() {
        let launched = Box<Bool>()
        // A clock of the test's own, so a guard that let the app start would fail at once, not after the wait.
        let seen = ControlClientTests.Seen()
        let claude = Claude()
        let (_, exit) = run(claude, ControlClient(connectOnce: { nil }, launch: { launched.set(true); return true },
                                                  announce: nil, sleep: { seen.clock += $0 }, now: { seen.clock },
                                                  elevated: true))
        defer { end(claude, exit, nil) }
        claude.say(initialize)
        XCTAssertTrue(until(nil) { exit.value != nil && claude.answers()[.int(1)] != nil })
        XCTAssertEqual(exit.value, ControlOutput.unreachableExit)
        XCTAssertEqual(claude.answers()[.int(1)],
                       [McpSession.errorLine(code: McpSession.internalErrorCode, message: ControlClient.elevatedError, id: .int(1))])
        XCTAssertNil(launched.value, "nothing was started")
        XCTAssertEqual(seen.clock, 0, "no wait for an app that was never started")
    }

    /// The app answers a call and goes; the relay, not yet having read that answer, fails to write the
    /// next call. The answer still reaches Claude as the app gave it — a call that went through is never
    /// told to try again — and only the call that never arrived is.
    func testAnAnswerTheAppSentBeforeItWentReachesClaudeAfterAFailedWrite() {
        let app = ScriptedApp()
        let claude = Claude()
        let (_, exit) = run(claude, ControlClient(connectOnce: { app }, launch: nil, announce: nil))
        defer { end(claude, exit, nil) }
        claude.say(initialize)
        XCTAssertTrue(until(nil) { app.lines.contains(self.initialize) })
        app.send(#"{"id":1,"jsonrpc":"2.0","result":{}}"#)
        XCTAssertTrue(until(nil) { claude.answers()[.int(1)] != nil })
        claude.say(call(2))
        XCTAssertTrue(until(nil) { app.lines.contains(self.call(2)) })
        app.goes()
        claude.say(call(3))
        XCTAssertTrue(until(nil) { app.refusedLines.contains(self.call(3)) })
        let answered = #"{"id":2,"jsonrpc":"2.0","result":{"content":[],"isError":false,"structuredContent":{}}}"#
        app.send(answered)
        app.finish()
        XCTAssertTrue(until(nil) { claude.answers()[.int(2)] != nil && claude.answers()[.int(3)] != nil })
        XCTAssertEqual(claude.answers()[.int(2)], [answered], "the app's own answer, not restarted")
        XCTAssertTrue(claude.answers()[.int(3)]?.first?.contains(ControlRelay.restartedError) == true)
        XCTAssertTrue(app.isWriteClosed, "only the writing half was ended")
    }

    /// Claude going away while the relay writes to it ends the relay as any failed write to Claude does:
    /// not with SIGPIPE, which would end the process — and here the whole test run. Claude reads nothing,
    /// so the relay fills the pipe with answers and waits to write more when Claude closes its end.
    func testTheRelayEndsWhenClaudeGoesWhileItWrites() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let claude = Claude(reading: false)
        let (_, exit) = run(claude, client(socket))
        defer { end(claude, exit, socket) }
        let pings = (1...3_000).map { #"{"jsonrpc":"2.0","id":\#($0),"method":"ping"}"# }
        claude.say(([initialize, initialized] + pings).joined(separator: "\n"))
        // A pipe holds 16 KB until a writer makes it grow: by then the relay is writing more than it holds.
        XCTAssertTrue(socket.until { claude.unread >= 16_384 })
        claude.stopReading()
        XCTAssertTrue(socket.until { exit.value != nil })
        XCTAssertEqual(exit.value, ControlOutput.doneExit)
    }
}
