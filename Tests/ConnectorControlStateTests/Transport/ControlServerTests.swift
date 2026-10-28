import XCTest
import ConnectorControlCore
@testable import ConnectorControlState
@testable import ConnectorControlTransport

/// Mirror: windows/tests/ConnectorControl.Core.Tests/Transport/ControlServerTests.cs
///
/// The server apart from any socket: connections the test opens, feeds and closes.
@MainActor
final class ControlServerTests: XCTestCase {
    final class FakeConnection: ControlConnection, @unchecked Sendable {
        var sent: [String] = []
        var closed = false
        func send(_ line: String) { sent.append(line) }
        func close() { closed = true }
    }

    static let initializeMessage = #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"claude-ai","version":"1"}}}"#
    static let initializedMessage = #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#

    private func line(_ text: String) -> McpFrame { .line(Data(text.utf8)) }

    private func say(_ server: ControlServer, _ connection: FakeConnection, _ lines: String...) {
        for text in lines { server.receive(line(text), from: connection) }
    }

    func testEveryConnectionGetsItsOwnHostAndSessionOverTheOneControlTools() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        let server = ControlServer(tools: rig.tools)
        let cli = FakeConnection(), relay = FakeConnection()
        XCTAssertTrue(server.open(cli))
        XCTAssertTrue(server.open(relay))
        say(server, cli, ControlHello.line(client: ControlHello.cliClient, version: "1.2.2"))
        say(server, relay, ControlHello.line(client: ControlHello.relayClient, version: "1.2.2"))
        XCTAssertEqual(server.hosts.count, 2)
        XCTAssertTrue(server.hosts.allSatisfy { $0.tools === rig.tools }, "one ControlTools, shared by every connection")
        XCTAssertEqual(server.hosts.map(\.isCLI), [true, false])
        XCTAssertEqual(server.hosts.map(\.clientVersion), ["1.2.2", "1.2.2"])
        XCTAssertNotIdentical(try XCTUnwrap(server.hosts[0].session), try XCTUnwrap(server.hosts[1].session))
    }

    func testTheHelloIsConsumedAndNeverReachesTheSession() {
        let rig = ControlRig(); defer { rig.dispose() }
        let server = ControlServer(tools: rig.tools)
        let connection = FakeConnection()
        server.open(connection)
        say(server, connection, ControlServerTests.initializeMessage)
        XCTAssertEqual(connection.sent, [], "a first line is the hello, and a hello is never answered")
        XCTAssertEqual(server.hosts.map(\.isCLI), [false], "an MCP message is no command-line hello")
        say(server, connection, #"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#)
        XCTAssertEqual(connection.sent, [#"{"error":{"code":-32002,"message":"Server not initialized"},"id":2,"jsonrpc":"2.0"}"#])
    }

    func testAnOversizedHelloIsTheRelayAndTheConnectionCarriesOn() {
        let rig = ControlRig(); defer { rig.dispose() }
        let server = ControlServer(tools: rig.tools)
        let connection = FakeConnection()
        server.open(connection)
        server.receive(.tooLarge, from: connection)
        XCTAssertEqual(server.hosts.map(\.isCLI), [false])
        XCTAssertNil(server.hosts.first?.clientVersion)
        say(server, connection, ControlServerTests.initializeMessage)
        XCTAssertEqual(connection.sent.count, 1)
        XCTAssertTrue(connection.sent[0].contains(#""serverInfo""#))
    }

    func testAHelloThatIsNotUTF8IsTheRelay() {
        let rig = ControlRig(); defer { rig.dispose() }
        let server = ControlServer(tools: rig.tools)
        let connection = FakeConnection()
        server.open(connection)
        var bytes = Data(#"{"client":"cli","version":"1.2.2","x":""#.utf8)
        bytes.append(0xFF)
        bytes.append(contentsOf: Data(#""}"#.utf8))
        server.receive(.line(bytes), from: connection)
        XCTAssertEqual(server.hosts.map(\.isCLI), [false])
        XCTAssertNil(server.hosts.first?.clientVersion)
        XCTAssertEqual(connection.sent, [])
    }

    func testAHelloWithALoneSurrogateIsTheRelayAndTheNextLineIsNotTakenAsTheHello() {
        let rig = ControlRig(); defer { rig.dispose() }
        let server = ControlServer(tools: rig.tools)
        let connection = FakeConnection()
        server.open(connection)
        say(server, connection, #"{"client":"cli","version":"\ud800"}"#,
            ControlHello.line(client: ControlHello.cliClient, version: "1.2.2"))
        XCTAssertEqual(server.hosts.map(\.isCLI), [false], "the second line is not a second hello")
        XCTAssertEqual(server.hosts.count, 1)
        XCTAssertNil(server.hosts[0].clientVersion)
        XCTAssertEqual(connection.sent, [#"{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}"#],
                       "it reached the session, which refused it as no request")
    }

    func testFramesAfterTheHelloReachTheSessionAsBytes() {
        let rig = ControlRig(); defer { rig.dispose() }
        let server = ControlServer(tools: rig.tools)
        let connection = FakeConnection()
        server.open(connection)
        say(server, connection, ControlHello.line(client: ControlHello.cliClient, version: "1.2.2"))
        server.receive(.tooLarge, from: connection)
        server.receive(.line(Data([0x7B, 0xFF, 0x7D])), from: connection)
        XCTAssertEqual(connection.sent, [
            #"{"error":{"code":-32600,"message":"Message too large"},"id":null,"jsonrpc":"2.0"}"#,
            #"{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}"#,
        ])
    }

    func testTheServerTakesAHandfulOfConnectionsAndClosesTheRest() {
        let rig = ControlRig(); defer { rig.dispose() }
        let server = ControlServer(tools: rig.tools)
        let connections = (0..<ControlServer.maxConnections).map { _ in FakeConnection() }
        XCTAssertTrue(connections.allSatisfy { server.open($0) })
        let extra = FakeConnection()
        XCTAssertFalse(server.open(extra))
        XCTAssertTrue(extra.closed, "closed unanswered")
        server.close(connections[0])
        XCTAssertTrue(connections[0].closed)
        XCTAssertTrue(server.open(FakeConnection()), "a closed connection frees its place")
        XCTAssertEqual(server.connectionCount, ControlServer.maxConnections)
    }

    func testAClosedConnectionsHostIsClosedAndItsFramesAreIgnored() {
        let rig = ControlRig(); defer { rig.dispose() }
        let server = ControlServer(tools: rig.tools)
        let connection = FakeConnection(), other = FakeConnection()
        server.open(connection)
        server.open(other)
        say(server, connection, ControlHello.line(client: ControlHello.cliClient, version: "1.2.2"))
        say(server, other, ControlHello.line(client: ControlHello.relayClient, version: "1.2.2"))
        let host = server.hosts[0]
        server.close(connection)
        XCTAssertFalse(host.isOpen)
        say(server, connection, ControlServerTests.initializeMessage)
        XCTAssertEqual(connection.sent, [])
        let relayHost = server.hosts[0]
        server.closeAll()
        XCTAssertFalse(relayHost.isOpen)
        XCTAssertTrue(other.closed)
        XCTAssertEqual(server.connectionCount, 0)
    }

    /// An asking call whose connection closed is still answered once the user responds; that answer
    /// goes to a closed session and must reach nothing, on a connection the test still holds.
    func testAnAnswerAfterTheConnectionClosedIsSentNowhere() {
        let rig = ControlRig(); defer { rig.dispose() }
        let server = ControlServer(tools: rig.tools)
        let connection = FakeConnection(), other = FakeConnection()
        server.open(connection)
        server.open(other)
        say(server, connection, ControlHello.line(client: ControlHello.relayClient, version: "1.2.2"), ControlServerTests.initializeMessage,
            ControlServerTests.initializedMessage,
            #"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"delete_connectors","arguments":{"names":["aws-mcp"]}}}"#)
        say(server, other, ControlHello.line(client: ControlHello.relayClient, version: "1.2.2"))
        rig.h.drain()
        XCTAssertEqual(rig.confirmer.waitingCount, 1)
        let before = connection.sent.count
        server.close(connection)
        rig.confirmer.answer(true)
        rig.h.drain()
        XCTAssertEqual(connection.sent.count, before, "nothing is written after close")
        XCTAssertEqual(other.sent, [], "and the answer reaches no other connection")
        XCTAssertNil(rig.state.store.mcps["aws-mcp"], "the allowed change still applies")
        XCTAssertEqual(rig.h.notifier.sent.filter { $0.body.contains("deleted") }.count, 1, "and is announced once")
    }

    func testClosingTwiceClosesOnce() {
        let rig = ControlRig(); defer { rig.dispose() }
        let server = ControlServer(tools: rig.tools)
        let connection = FakeConnection()
        server.open(connection)
        server.close(connection)
        connection.closed = false
        server.close(connection)
        XCTAssertFalse(connection.closed, "a second close does nothing")
        XCTAssertEqual(server.connectionCount, 0)
    }

    /// A connection that, as it closes, tells the server it closed.
    final class ReentrantConnection: ControlConnection, @unchecked Sendable {
        var server: ControlServer?
        var closeCalls = 0
        func send(_ line: String) {}
        func close() {
            closeCalls += 1
            MainActor.assumeIsolated { server?.close(self) }
        }
    }

    func testAConnectionThatClosesTheServerAgainWhileItIsClosingAllIsSafe() {
        let rig = ControlRig(); defer { rig.dispose() }
        let server = ControlServer(tools: rig.tools)
        let connections = (0..<3).map { _ in ReentrantConnection() }
        for connection in connections {
            connection.server = server
            server.open(connection)
        }
        server.closeAll()
        XCTAssertEqual(connections.map(\.closeCalls), [1, 1, 1], "each is closed exactly once")
        XCTAssertEqual(server.connectionCount, 0)
    }

    /// The point of sharing one ControlTools: a change from one connection waits behind another
    /// connection's open dialog, as Claude's two relays and a command line must.
    func testAWriteFromOneConnectionWaitsBehindAnotherConnectionsDialog() {
        let rig = ControlRig(); defer { rig.dispose() }
        let server = ControlServer(tools: rig.tools)
        let chat = FakeConnection(), agent = FakeConnection()
        server.open(chat)
        server.open(agent)
        say(server, chat, ControlHello.line(client: ControlHello.relayClient, version: "1.2.2"), ControlServerTests.initializeMessage,
            ControlServerTests.initializedMessage,
            #"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"delete_connectors","arguments":{"names":["aws-mcp"]}}}"#)
        say(server, agent, ControlHello.line(client: ControlHello.relayClient, version: "1.2.2"), ControlServerTests.initializeMessage,
            ControlServerTests.initializedMessage,
            #"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"create_collection","arguments":{"name":"Later"}}}"#)
        rig.h.drain()
        XCTAssertEqual(rig.confirmer.waitingCount, 1)
        XCTAssertEqual(agent.sent.count, 1, "the second connection's write waits its turn")
        XCTAssertNil(rig.state.store.collections["Later"])
        rig.confirmer.answer(false)
        rig.h.drain()
        XCTAssertEqual(agent.sent.count, 2)
        XCTAssertNotNil(rig.state.store.collections["Later"])
    }

    /// The gate goes by the hello: the command line runs a gated change without asking; the relay
    /// is asked, and has no answer until the user gives one.
    func testTheCommandLineRunsAGatedChangeAndTheRelayIsAsked() {
        let rig = ControlRig(); defer { rig.dispose() }
        let server = ControlServer(tools: rig.tools)
        let cli = FakeConnection(), relay = FakeConnection()
        server.open(cli)
        server.open(relay)
        let delete = #"{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"delete_connectors","arguments":{"names":["aws-mcp"]}}}"#
        say(server, relay, ControlHello.line(client: ControlHello.relayClient, version: "1.2.2"), ControlServerTests.initializeMessage,
            ControlServerTests.initializedMessage, delete)
        rig.h.drain()
        XCTAssertEqual(rig.confirmer.requests.count, 1, "the relay is asked")
        XCTAssertEqual(relay.sent.count, 1, "and has only its initialize answer")
        rig.confirmer.answer(false)
        say(server, cli, ControlHello.line(client: ControlHello.cliClient, version: "1.2.2"), ControlServerTests.initializeMessage,
            ControlServerTests.initializedMessage, delete)
        rig.h.drain()
        XCTAssertEqual(rig.confirmer.requests.count, 1, "the command line is never asked")
        XCTAssertTrue(cli.sent.last?.contains(#""isError":false"#) == true)
        XCTAssertNil(rig.state.store.mcps["aws-mcp"])
    }
}
