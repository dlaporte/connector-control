import XCTest
import ConnectorControlCore
@testable import ConnectorControlState
@testable import ConnectorControlTransport

/// Mac only: the Windows counterpart is windows/tests/ConnectorControl.Core.Tests/Transport/ControlPipeTests.cs.
///
/// The control layer on a real socket: what reaches the server, what it answers, and who it lets in.
///
/// The Windows file also has a fact for the pipe server's `stopped` callback
/// (AnAcceptLoopThatFailsSaysSoAndClosesTheConnectionItWasHandingOn) and one that a client on this computer
/// is accepted (AClientOnThisComputerIsLocal); its refusal of a client on another computer is a pure rule,
/// tested in ControlEndpointTests. The Mac has none of these to test: its listener has no accept loop that
/// can die unseen, and a Unix socket cannot be reached from another machine.
@MainActor
final class ControlSocketTests: XCTestCase {
    private let initialize = ControlServerTests.initializeMessage
    private let initialized = ControlServerTests.initializedMessage
    private let parseError = #"{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}"#
    private let deleteAws = #"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"delete_connectors","arguments":{"names":["aws-mcp"]}}}"#

    func testTheSocketIsOwnerOnlyInAnOwnerOnlyFolder() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        var info = stat()
        XCTAssertEqual(lstat(socket.path, &info), 0)
        XCTAssertEqual(info.st_mode & S_IFMT, S_IFSOCK)
        XCTAssertEqual(info.st_mode & 0o777, 0o600)
        XCTAssertEqual(stat(socket.folder, &info), 0)
        XCTAssertEqual(info.st_mode & 0o777, 0o700)
    }

    func testRequestsAreAnsweredEachUnderItsOwnID() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let client = try socket.connect()
        client.hello()
        client.say(initialize, initialized,
                   #"{"jsonrpc":"2.0","id":2,"method":"ping"}"#,
                   #"{"jsonrpc":"2.0","id":3,"method":"tools/list"}"#,
                   #"{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"get_status","arguments":{}}}"#)
        XCTAssertTrue(socket.until { client.answers().count == 4 })
        let answers = client.answers()
        XCTAssertEqual(Set(answers.keys), [.int(1), .int(2), .int(3), .int(4)])
        XCTAssertTrue(answers.values.allSatisfy { $0.count == 1 }, "each request is answered once")
        XCTAssertEqual(answers[.int(2)], [#"{"id":2,"jsonrpc":"2.0","result":{}}"#])
        XCTAssertTrue(answers[.int(3)]?.first?.contains(#""name":"get_status""#) == true)
        XCTAssertTrue(answers[.int(4)]?.first?.contains(#""isError":false"#) == true)
    }

    func testAMessageOverOneMegabyteIsRefusedAndTheConnectionCarriesOn() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let client = try socket.connect()
        client.hello()
        client.send(Data(repeating: 0x20, count: McpSession.maxMessageBytes + 1) + Data("\n".utf8))
        client.say(#"{"jsonrpc":"2.0","id":5,"method":"ping"}"#)
        XCTAssertTrue(socket.until { client.lines.count == 2 })
        XCTAssertEqual(client.lines, [#"{"error":{"code":-32600,"message":"Message too large"},"id":null,"jsonrpc":"2.0"}"#,
                                      #"{"id":5,"jsonrpc":"2.0","result":{}}"#])
    }

    /// Bytes that are not UTF-8 are refused as they arrive, so both platforms answer the same bytes
    /// the same way whatever their decoders would have made of them.
    func testMalformedJSONAndBytesThatAreNotUTF8AreParseErrors() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let client = try socket.connect()
        client.hello()
        client.say(#"{"jsonrpc":"#)
        for bad in [[0xC3, 0x28], [0xC0, 0xAF], [0xED, 0xA0, 0x80], [0xFF]] as [[UInt8]] {
            client.send(Data(#"{"jsonrpc":"2.0","id":1,"method":"ping","params":{"x":""#.utf8) + Data(bad) + Data("\"}}\n".utf8))
        }
        client.say(#"{"jsonrpc":"2.0","id":6,"method":"ping"}"#)
        XCTAssertTrue(socket.until { client.lines.count == 6 })
        XCTAssertEqual(client.lines, Array(repeating: parseError, count: 5) + [#"{"id":6,"jsonrpc":"2.0","result":{}}"#])
    }

    /// A string that ends in an escaped backslash is closed by the quote after it, so the brackets
    /// that follow count: 62 more levels make 64, the limit; 63 make 65.
    func testTheNestingScannerSeesPastAnEscapedBackslash() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let client = try socket.connect()
        client.hello()
        func ping(_ id: Int, depth: Int) -> String {
            #"{"jsonrpc":"2.0","id":\#(id),"method":"ping","params":{"s":"\\","d":"#
                + String(repeating: "[", count: depth) + String(repeating: "]", count: depth) + "}}"
        }
        client.say(ping(1, depth: 62), ping(2, depth: 63))
        XCTAssertTrue(socket.until { client.lines.count == 2 })
        XCTAssertEqual(client.lines, [#"{"id":1,"jsonrpc":"2.0","result":{}}"#, parseError])
    }

    /// Anything but a well-formed command-line hello is gated: each of these is asked about, and
    /// declining refuses the change.
    func testTheHelloFailsClosed() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let hellos = [#"{"client":"cli"}"#, #"{"client":"CLI","version":"1.2.2"}"#, "hello", initialize]
        for (index, hello) in hellos.enumerated() {
            let client = try socket.connect()
            client.say(hello, initialize, initialized, deleteAws)
            XCTAssertTrue(socket.until { socket.rig.confirmer.requests.count == index + 1 }, hello)
            socket.rig.confirmer.answer(false)
            XCTAssertTrue(socket.until { client.answers()[.int(2)] != nil }, hello)
            XCTAssertTrue(client.answers()[.int(2)]?.first?.contains(ControlTools.declinedError) == true, hello)
        }
        XCTAssertNotNil(socket.rig.state.store.mcps["aws-mcp"])
        let cli = try socket.connect()
        cli.hello()
        cli.say(initialize, initialized, deleteAws)
        XCTAssertTrue(socket.until { cli.answers()[.int(2)] != nil })
        XCTAssertEqual(socket.rig.confirmer.requests.count, hellos.count, "the command line is not asked")
        XCTAssertNil(socket.rig.state.store.mcps["aws-mcp"])
    }

    func testOneControlToolsServesEveryConnection() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let clients = try (0..<3).map { _ in try socket.connect() }
        clients[0].hello()
        clients[1].hello(ControlHello.relayClient)
        clients[2].hello(ControlHello.relayClient)
        XCTAssertTrue(socket.until { socket.server.hosts.count == 3 })
        XCTAssertTrue(socket.server.hosts.allSatisfy { $0.tools === socket.rig.tools })
    }

    func testAConnectionPastTheLimitIsClosedUnanswered() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let clients = try (0..<ControlServer.maxConnections).map { _ in try socket.connect() }
        XCTAssertTrue(socket.until { socket.server.connectionCount == ControlServer.maxConnections })
        let extra = try socket.connect()
        extra.hello()
        extra.say(#"{"jsonrpc":"2.0","id":1,"method":"ping"}"#)
        XCTAssertTrue(socket.until { extra.isEnded })
        XCTAssertEqual(extra.lines, [])
        // The ones already in are still served, and none was dropped to make room.
        clients[0].hello()
        clients[0].say(initialize, initialized, #"{"jsonrpc":"2.0","id":2,"method":"ping"}"#)
        XCTAssertTrue(socket.until { clients[0].answers()[.int(2)] != nil })
        XCTAssertTrue(clients.allSatisfy { !$0.isEnded })
        XCTAssertEqual(socket.server.connectionCount, ControlServer.maxConnections)
        withExtendedLifetime(clients) {}
    }

    func testAnotherUsersConnectionIsClosedBeforeAnythingIsRead() throws {
        let socket = try SocketRig(allowedUID: getuid() &+ 1); defer { socket.dispose() }
        let client = try socket.connect()
        client.hello()
        client.say(#"{"jsonrpc":"2.0","id":1,"method":"ping"}"#)
        XCTAssertTrue(socket.until { client.isEnded })
        XCTAssertEqual(client.lines, [])
        XCTAssertEqual(socket.server.connectionCount, 0, "it never reached the server")
    }

    func testTheToolRefusesAServerAnotherUserRuns() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        XCTAssertThrowsError(try ControlSocketStream.connect(to: socket.path, owner: getuid() &+ 1)) {
            XCTAssertEqual($0 as? ControlSocketError, .notOwner(socket.path))
        }
    }

    func testALiveSocketIsLeftAloneAndAStaleOneIsReplaced() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        XCTAssertThrowsError(try ControlSocketListener.listen(at: socket.path)) {
            XCTAssertEqual($0 as? ControlSocketError, .inUse(socket.path), "another copy of the app is serving")
        }
        // What a crashed app leaves: a socket file nothing listens on.
        let stale = socket.folder + "/stale.sock"
        var address = try ControlSocket.address(stale)
        let fd = Darwin.socket(AF_UNIX, SOCK_STREAM, 0)
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        XCTAssertEqual(bound, 0)
        Darwin.close(fd)
        XCTAssertTrue(FileManager.default.fileExists(atPath: stale))
        let listener = try ControlSocketListener.listen(at: stale)
        listener.close()
        XCTAssertFalse(FileManager.default.fileExists(atPath: stale), "closing takes the socket file with it")
    }

    func testAFileThatIsNotASocketIsLeftAlone() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let other = socket.folder + "/other.sock"
        try Data("mine".utf8).write(to: URL(fileURLWithPath: other))
        XCTAssertThrowsError(try ControlSocketListener.listen(at: other)) {
            XCTAssertEqual($0 as? ControlSocketError, .notASocket(other))
        }
        XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: other)), Data("mine".utf8))
    }

    /// Only a refusal, tried a few times, or nothing there at all makes a socket file stale: one that
    /// refuses for any other reason — here, no permission — is left alone and starting fails.
    func testASocketThatFailsForAnotherReasonIsLeftAlone() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let odd = socket.folder + "/odd.sock"
        var address = try ControlSocket.address(odd)
        let fd = Darwin.socket(AF_UNIX, SOCK_STREAM, 0)
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        XCTAssertEqual(bound, 0)
        Darwin.close(fd)
        XCTAssertEqual(chmod(odd, 0), 0)
        XCTAssertThrowsError(try ControlSocketListener.listen(at: odd)) {
            XCTAssertEqual($0 as? ControlSocketError, .system("connect", EACCES))
        }
        XCTAssertTrue(FileManager.default.fileExists(atPath: odd))
    }

    /// A socket in a folder reached through a link could be reached through whatever the link's owner
    /// points it at: the folder must be a real one of this user's.
    func testAFolderThatIsALinkIsRefused() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let real = socket.folder + "/real"
        let link = socket.folder + "/link"
        try FileManager.default.createDirectory(atPath: real, withIntermediateDirectories: false)
        try FileManager.default.createSymbolicLink(atPath: link, withDestinationPath: real)
        XCTAssertThrowsError(try ControlSocketListener.listen(at: link + "/control.sock")) {
            XCTAssertEqual($0 as? ControlSocketError, .folderNotOwned(link))
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: real + "/control.sock"))
    }

    func testAPathTooLongForASocketIsRefusedBeforeAnythingIsMade() {
        let path = "/tmp/" + String(repeating: "x", count: 120) + "/control.sock"
        XCTAssertThrowsError(try ControlSocketListener.listen(at: path)) {
            XCTAssertEqual($0 as? ControlSocketError, .pathTooLong(path))
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: (path as NSString).deletingLastPathComponent))
    }

    func testANewerToolAskingForAToolThisAppLacksIsToldToRestartIt() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let client = try socket.connect()
        client.hello(version: "99.0.0")
        client.say(initialize, initialized, #"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"future_tool","arguments":{}}}"#)
        XCTAssertTrue(socket.until { client.answers()[.int(2)] != nil })
        XCTAssertEqual(client.answers()[.int(2)], [McpSession.errorLine(code: McpSession.invalidParamsCode,
                                                                      message: ControlTools.needsNewerAppError("99.0.0"), id: .int(2))])
    }

    /// A command line that goes away while its write waits behind a dialog takes the write with it.
    func testClosingTheConnectionDropsItsWaitingWrite() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let relay = try socket.connect()
        relay.hello(ControlHello.relayClient)
        relay.say(initialize, initialized, deleteAws)
        XCTAssertTrue(socket.until { socket.rig.confirmer.waitingCount == 1 })
        let waiting = socket.rig.h.delays.pending.count
        let cli = try socket.connect()
        cli.hello()
        cli.say(initialize, initialized,
                #"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"_meta":{"progressToken":"p"},"name":"create_collection","arguments":{"name":"Later"}}}"#)
        // Its write reached the server and is queued behind the relay's, so closing drops a write
        // that was waiting, not one that never arrived.
        // Progress reaches a call only once it waits its turn, so hearing it proves the write is queued.
        XCTAssertTrue(socket.until { socket.rig.h.delays.pending.count > waiting })
        for _ in 0..<socket.rig.h.delays.pending.count { socket.rig.h.delays.runNext() }
        XCTAssertTrue(socket.until { cli.lines.contains { $0.contains(#""progressToken":"p""#) } })
        cli.stream.close()
        XCTAssertTrue(socket.until { socket.server.connectionCount == 1 })
        socket.rig.confirmer.answer(false)
        XCTAssertTrue(socket.until { relay.answers()[.int(2)] != nil })
        XCTAssertNil(socket.rig.state.store.collections["Later"], "the command line's write never ran")
    }

    /// A regular file where the folder should be is a refusal, not a Foundation error.
    func testAFileWhereTheFolderShouldBeIsRefused() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let file = socket.folder + "/file"
        try Data("mine".utf8).write(to: URL(fileURLWithPath: file))
        XCTAssertThrowsError(try ControlSocketListener.listen(at: file + "/control.sock")) {
            XCTAssertEqual($0 as? ControlSocketError, .folderNotOwned(file))
        }
        XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: file)), Data("mine".utf8))
    }

    /// Starting a listener that is closed, or already started, does nothing.
    func testStartingAClosedOrStartedListenerDoesNothing() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let path = socket.folder + "/again.sock"
        let queue = DispatchQueue(label: "test.accept")
        let count = Locked(0)
        let listener = try ControlSocketListener.listen(at: path)
        listener.start(queue: queue) { stream in count.add(); stream.close() }
        listener.start(queue: queue) { stream in count.add(); stream.close() }
        let first = try ControlSocketStream.connect(to: path)
        XCTAssertTrue(pollUntil { count.value == 1 })
        XCTAssertEqual(boundedRead(first), Data())
        XCTAssertTrue(listener.isServing)
        listener.close()
        XCTAssertFalse(listener.isServing)
        listener.start(queue: queue) { _ in count.add() }
        XCTAssertFalse(listener.isServing, "a closed listener is not started again")
        XCTAssertEqual(count.value, 1)
    }

    /// The server closes a client that never reads, within a few times the timeout, and the app goes on.
    func testAClientThatNeverReadsIsClosedWithinTheTimeout() throws {
        let socket = try SocketRig(writeTimeout: WriteDeadline.short); defer { socket.dispose() }
        let stream = try ControlSocketStream.connect(to: socket.path)
        var burst = Data((ControlHello.line(client: ControlHello.cliClient, version: "1.2.2") + "\n").utf8)
        burst.append(Data((initialize + "\n" + initialized + "\n").utf8))
        for id in 2..<4_000 { burst.append(Data(#"{"jsonrpc":"2.0","id":\#(id),"method":"ping"}"#.utf8) + Data("\n".utf8)) }
        XCTAssertTrue(stream.write(burst))
        XCTAssertTrue(socket.until { socket.server.connectionCount == 1 })
        let began = Date()
        XCTAssertTrue(socket.until { socket.server.connectionCount == 0 })
        XCTAssertLessThan(Date().timeIntervalSince(began), 3 * WriteDeadline.short + 3)
        withExtendedLifetime(stream) {}
    }

    /// An accept already running when the app stops posts its connection after the app has closed them
    /// all; the connection is closed, not opened.
    func testAConnectionAcceptedBeforeStopButOpenedAfterIsClosedNotOpened() throws {
        let socket = try SocketRig(); defer { socket.dispose() }
        let endpoint = try XCTUnwrap(socket.endpoint)
        let (served, client) = try socketPair()
        endpoint.accept(served)
        socket.stop()
        socket.rig.h.ui.pump()
        XCTAssertEqual(socket.server.connectionCount, 0)
        XCTAssertEqual(boundedRead(client), Data(), "it is closed")
    }

    func testARestartedServerStillRefusesAnotherUser() throws {
        let socket = try SocketRig(allowedUID: getuid() &+ 1); defer { socket.dispose() }
        socket.stop()
        try socket.restart()
        let client = try socket.connect()
        client.hello()
        XCTAssertTrue(socket.until { client.isEnded })
        XCTAssertEqual(socket.server.connectionCount, 0)
    }

    private func socketPair() throws -> (ControlSocketStream, ControlSocketStream) {
        var fds: [Int32] = [0, 0]
        guard socketpair(AF_UNIX, SOCK_STREAM, 0, &fds) == 0 else { throw ControlSocketError.system("socketpair", errno) }
        ControlSocket.noSIGPIPE(fds[0])
        ControlSocket.noSIGPIPE(fds[1])
        let streams = (ControlSocketStream(fd: fds[0]), ControlSocketStream(fd: fds[1]))
        streams.0.makeNonBlocking()
        streams.1.makeNonBlocking()
        return streams
    }

    /// A write is bounded as a whole: a peer that reads nothing, and a line bigger than what is free
    /// in the socket's buffer, end it at the deadline and not at some multiple of it.
    func testAWriteToAPeerThatReadsNothingFailsAtTheDeadline() throws {
        let (server, client) = try socketPair()
        server.setWriteTimeout(seconds: WriteDeadline.short)
        let began = Date()
        XCTAssertFalse(server.write(Data(repeating: 0x61, count: 262_144)))
        // Cut at the deadline, not after a partial write and a second wait of the same length.
        XCTAssertLessThan(Date().timeIntervalSince(began), 1.8)
        withExtendedLifetime(client) {}
    }

    /// A peer that reads a little at a time, each read within the timeout, does not keep a write going
    /// past the deadline: the wait does not start over when some of it is read.
    func testAWriteToAPeerThatReadsSlowlyFailsAtTheDeadline() throws {
        let (server, client) = try socketPair()
        server.setWriteTimeout(seconds: WriteDeadline.short)
        Thread.detachNewThread {
            while !client.read(maximum: 1_024).isEmpty { usleep(50_000) }
        }
        let began = Date()
        XCTAssertFalse(server.write(Data(repeating: 0x61, count: 262_144)))
        XCTAssertLessThan(Date().timeIntervalSince(began), WriteDeadline.bound)
        client.close()
    }
}

/// The short write timeout the tests give the server, and how long a write past it may take to be
/// cut: generous, since only a write that is never cut matters.
enum WriteDeadline {
    static let short = 1.0
    static var bound: TimeInterval { 3 * short + 1 }
}

/// A counter two threads may touch.
final class Locked: @unchecked Sendable {
    private let lock = NSLock()
    private var number: Int
    init(_ number: Int) { self.number = number }
    func add() { lock.withLock { number += 1 } }
    var value: Int { lock.withLock { number } }
}

/// Waits on real background work, for at most `Wait.eventually`.
func pollUntil(_ condition: () -> Bool) -> Bool {
    let end = Date().addingTimeInterval(Wait.eventually)
    while !condition() {
        if Date() > end { return false }
        usleep(5_000)
    }
    return true
}

/// One byte, or the end, from a stream: nothing after 10 s fails the test and does not hang it.
func boundedRead(_ stream: ControlSocketStream) -> Data? {
    var pending = pollfd(fd: stream.fd, events: Int16(POLLIN), revents: 0)
    guard poll(&pending, 1, 10_000) > 0 else { return nil }
    return stream.read(maximum: 1)
}
