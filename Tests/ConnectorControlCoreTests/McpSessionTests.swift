import XCTest
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/McpSessionTests.cs
@MainActor
final class McpSessionTests: XCTestCase {
    private final class FakeHost: McpToolHost {
        struct Call {
            let name: String
            let arguments: JSONValue
            let token: JSONValue?
            let progress: (Double) -> Void
            let completion: (JSONValue) -> Void
        }
        var calls: [Call] = []
        func listTools() -> [JSONValue] { [.object(["name": .string("echo")])] }
        func callTool(name: String, arguments: JSONValue, progressToken: JSONValue?,
                      progress: @escaping (Double) -> Void, completion: @escaping (JSONValue) -> Void) {
            calls.append(Call(name: name, arguments: arguments, token: progressToken, progress: progress, completion: completion))
        }
    }

    private let host = FakeHost()
    private var sent: [String] = []
    private lazy var session = McpSession(host: host, version: "9.9.9", send: { [unowned self] in self.sent.append($0) })

    private func initialize(_ version: String = "2025-06-18") {
        session.receive(#"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"\#(version)","capabilities":{},"clientInfo":{"name":"claude-ai","version":"0.1"}}}"#)
    }

    func testInitializeEchoesASupportedVersionAndNamesTheServer() {
        initialize()
        XCTAssertEqual(sent, [#"{"id":1,"jsonrpc":"2.0","result":{"capabilities":{"tools":{}},"protocolVersion":"2025-06-18","serverInfo":{"name":"connector-control","version":"9.9.9"}}}"#])
        XCTAssertEqual(session.clientName, "claude-ai")
        XCTAssertTrue(session.isInitialized)
    }

    func testAnUnknownVersionIsAnsweredWithTheNewest() {
        initialize("1999-01-01")
        XCTAssertTrue(sent[0].contains(#""protocolVersion":"2025-11-25""#))
    }

    func testPingIsAnsweredEvenBeforeInitialize() {
        session.receive(#"{"jsonrpc":"2.0","id":"p","method":"ping"}"#)
        XCTAssertEqual(sent, [#"{"id":"p","jsonrpc":"2.0","result":{}}"#])
    }

    func testARequestBeforeInitializeIsRefused() {
        session.receive(#"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#)
        XCTAssertEqual(sent, [#"{"error":{"code":-32002,"message":"Server not initialized"},"id":2,"jsonrpc":"2.0"}"#])
    }

    func testToolsListReturnsTheHostsTools() {
        initialize()
        session.receive(#"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#)
        XCTAssertEqual(sent.last, #"{"id":2,"jsonrpc":"2.0","result":{"tools":[{"name":"echo"}]}}"#)
    }

    func testToolsCallRunsTheToolAndAnswersWithItsResult() throws {
        initialize()
        session.receive(#"{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"a":1}}}"#)
        let call = try XCTUnwrap(host.calls.first)
        XCTAssertEqual(call.name, "echo")
        XCTAssertEqual(call.arguments, .object(["a": .int(1)]))
        XCTAssertNil(call.token)
        XCTAssertEqual(sent.count, 1, "nothing is said until the tool answers")
        call.completion(.object(["isError": .bool(false)]))
        XCTAssertEqual(sent.last, #"{"id":3,"jsonrpc":"2.0","result":{"isError":false}}"#)
        call.completion(.object(["isError": .bool(true)]))
        XCTAssertEqual(sent.count, 2, "a request is answered once")
    }

    func testProgressIsSentOnlyWhenTheClientGaveAToken() throws {
        initialize()
        session.receive(#"{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"echo","_meta":{"progressToken":"t1"}}}"#)
        session.receive(#"{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"echo"}}"#)
        host.calls[0].progress(5)
        host.calls[1].progress(5)
        XCTAssertEqual(sent.dropFirst().map { $0 }, [#"{"jsonrpc":"2.0","method":"notifications\/progress","params":{"progress":5,"progressToken":"t1"}}"#])
        XCTAssertEqual(host.calls[0].token, .string("t1"))
    }

    func testACancelledRequestIsNeverAnswered() {
        initialize()
        session.receive(#"{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"echo","_meta":{"progressToken":7}}}"#)
        session.receive(#"{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":6,"reason":"timeout"}}"#)
        host.calls[0].progress(5)
        host.calls[0].completion(.object([:]))
        XCTAssertEqual(sent.count, 1)
    }

    func testMalformedJSONIsAParseError() {
        session.receive("{nope")
        XCTAssertEqual(sent, [#"{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}"#])
    }

    func testAnythingButAJSONRPCRequestIsAnInvalidRequest() {
        session.receive(#"{"id":3,"method":"ping"}"#)
        session.receive("[1,2]")
        session.receive(#"{"jsonrpc":"2.0","id":true,"method":"ping"}"#)
        XCTAssertEqual(sent, [
            #"{"error":{"code":-32600,"message":"Invalid Request"},"id":3,"jsonrpc":"2.0"}"#,
            #"{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}"#,
            #"{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}"#,
        ])
    }

    func testAnUnknownMethodIsMethodNotFound() {
        initialize()
        session.receive(#"{"jsonrpc":"2.0","id":4,"method":"resources/list"}"#)
        XCTAssertEqual(sent.last, #"{"error":{"code":-32601,"message":"Method not found"},"id":4,"jsonrpc":"2.0"}"#)
    }

    func testAnUnknownToolAndBadArgumentsAreInvalidParams() {
        initialize()
        session.receive(#"{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"nope"}}"#)
        session.receive(#"{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"echo","arguments":[1]}}"#)
        session.receive(#"{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{}}"#)
        XCTAssertEqual(Array(sent.dropFirst()), [
            #"{"error":{"code":-32602,"message":"Unknown tool: nope"},"id":5,"jsonrpc":"2.0"}"#,
            #"{"error":{"code":-32602,"message":"Invalid params"},"id":6,"jsonrpc":"2.0"}"#,
            #"{"error":{"code":-32602,"message":"Invalid params"},"id":7,"jsonrpc":"2.0"}"#,
        ])
        XCTAssertTrue(host.calls.isEmpty)
    }

    func testAMessageOverOneMegabyteIsRefusedUnread() {
        session.receive(String(repeating: "x", count: McpSession.maxMessageBytes + 1))
        XCTAssertEqual(sent, [#"{"error":{"code":-32600,"message":"Message too large"},"id":null,"jsonrpc":"2.0"}"#])
    }

    func testNotificationsAndClientResponsesAreNeverAnswered() {
        initialize()
        session.receive(#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#)
        session.receive(#"{"jsonrpc":"2.0","id":99,"result":{}}"#)
        session.receive("")
        XCTAssertEqual(sent.count, 1)
    }

    func testCompactLinesAreOneLineKeySortedJSON() {
        XCTAssertEqual(JSONValue.object(["b": .int(1), "a": .string("x/y “q”"), "c": .array([.bool(true), .null])]).compactLine(),
                       #"{"a":"x\/y “q”","b":1,"c":[true,null]}"#)
    }
}
