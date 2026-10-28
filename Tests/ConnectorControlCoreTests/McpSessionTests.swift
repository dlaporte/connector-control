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
        func unknownToolMessage(_ name: String) -> String { "No \(name) here" }
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
            #"{"error":{"code":-32602,"message":"No nope here"},"id":5,"jsonrpc":"2.0"}"#,
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

    func testProgressThatIsNotFiniteIsDroppedAndFractionsSurvive() {
        initialize()
        session.receive(#"{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"echo","_meta":{"progressToken":"t"}}}"#)
        host.calls[0].progress(.nan)
        host.calls[0].progress(.infinity)
        host.calls[0].progress(-.infinity)
        XCTAssertEqual(sent.count, 1)
        host.calls[0].progress(0.5)
        XCTAssertEqual(sent.last, #"{"jsonrpc":"2.0","method":"notifications\/progress","params":{"progress":0.5,"progressToken":"t"}}"#)
    }

    func testCompactLineNeverAbortsOnANonFiniteNumber() {
        XCTAssertEqual(JSONValue.object(["a": .double(.nan)]).compactLine(), "null")
        XCTAssertEqual(JSONValue.double(.infinity).compactLine(), "null")
    }

    private func pingLine(padding: String) -> String {
        #"{"jsonrpc":"2.0","id":1,"method":"ping","pad":"\#(padding)"}"#
    }

    func testNestingDeeperThanSixtyFourIsAParseError() {
        let ok = #"{"jsonrpc":"2.0","id":1,"method":"ping","p":"# + String(repeating: "[", count: 63) + String(repeating: "]", count: 63) + "}"
        let deep = #"{"jsonrpc":"2.0","id":1,"method":"ping","p":"# + String(repeating: "[", count: 64) + String(repeating: "]", count: 64) + "}"
        session.receive(ok)
        session.receive(deep)
        XCTAssertEqual(sent, [#"{"id":1,"jsonrpc":"2.0","result":{}}"#,
                              #"{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}"#])
    }

    func testBracketsInsideStringsDoNotCountAsNesting() {
        session.receive(pingLine(padding: String(repeating: "[", count: 200) + #"\""# + String(repeating: "[", count: 200)))
        XCTAssertEqual(sent, [#"{"id":1,"jsonrpc":"2.0","result":{}}"#])
    }

    func testTwoObjectsOnOneLineAreAParseError() {
        session.receive(#"{"jsonrpc":"2.0","id":1,"method":"ping"}{"b":2}"#)
        XCTAssertEqual(sent, [#"{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}"#])
    }

    func testAnIntegerIdBeyondSixtyFourBitsIsAnInvalidRequest() {
        session.receive(#"{"jsonrpc":"2.0","id":123456789012345678901234567890,"method":"ping"}"#)
        XCTAssertEqual(sent, [#"{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}"#])
    }

    func testAWhitespaceOnlyLineIsIgnoredAndANullIdIsInvalid() {
        session.receive("  \t ")
        session.receive(#"{"jsonrpc":"2.0","id":null,"method":"ping"}"#)
        XCTAssertEqual(sent, [#"{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}"#])
    }

    /// Text that already holds U+FFFD is text; bytes that are not UTF-8 never become text (below).
    func testReplacementCharactersAlreadyInTextAreReadAsText() {
        session.receive(pingLine(padding: "\u{FFFD}\u{FFFD}"))
        XCTAssertEqual(sent, [#"{"id":1,"jsonrpc":"2.0","result":{}}"#])
    }

    func testAnAnswerJSONCannotHoldIsAnInternalErrorNotSilence() throws {
        initialize()
        session.receive(#"{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"echo"}}"#)
        try XCTUnwrap(host.calls.first).completion(.object(["value": .double(.nan)]))
        XCTAssertEqual(sent.last, #"{"error":{"code":-32603,"message":"Internal error"},"id":9,"jsonrpc":"2.0"}"#)
    }

    func testBytesThatAreNotUTF8AreAParseErrorBeforeAnythingDecodesThem() {
        let invalid: [[UInt8]] = [[0xC3, 0x28], [0xC0, 0xAF], [0xED, 0xA0, 0x80], [0xFF], [0xF4, 0x90, 0x80, 0x80], [0xE2]]
        for bad in invalid {
            sent = []
            session.receive(Data(#"{"jsonrpc":"2.0","id":1,"method":"ping","params":{"x":""#.utf8) + Data(bad) + Data(#""}}"#.utf8))
            XCTAssertEqual(sent, [#"{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}"#], "\(bad)")
        }
        sent = []
        session.receive(Data(#"{"jsonrpc":"2.0","id":"é","method":"ping"}"#.utf8))
        XCTAssertEqual(sent, [#"{"id":"é","jsonrpc":"2.0","result":{}}"#], "valid UTF-8 is read as it always was")
    }

    func testBytesPastTheLimitAndAFramersReportAreRefusedUnread() {
        // 0xFF is not UTF-8: only a size check that runs before decoding answers "too large" for it.
        session.receive(Data(repeating: 0xFF, count: McpSession.maxMessageBytes + 1))
        session.receiveTooLarge()
        XCTAssertEqual(sent, Array(repeating: #"{"error":{"code":-32600,"message":"Message too large"},"id":null,"jsonrpc":"2.0"}"#,
                                   count: 2))
    }

    func testErrorLinesAreWrittenAsTheSessionWritesThem() {
        XCTAssertEqual(McpSession.errorLine(code: McpSession.internalErrorCode, message: McpSession.internalErrorMessage, id: .int(4)),
                       #"{"error":{"code":-32603,"message":"Internal error"},"id":4,"jsonrpc":"2.0"}"#)
    }

    func testTheSizeLimitCountsBytesNotCharacters() {
        let base = pingLine(padding: "").utf8.count
        var room = McpSession.maxMessageBytes - base
        var padding = ""
        if room % 2 == 1 { padding = "x"; room -= 1 }
        padding += String(repeating: "é", count: room / 2)
        let exact = pingLine(padding: padding)
        XCTAssertEqual(exact.utf8.count, McpSession.maxMessageBytes)
        session.receive(exact)
        session.receive(pingLine(padding: padding + "x"))
        XCTAssertEqual(sent, [#"{"id":1,"jsonrpc":"2.0","result":{}}"#,
                              #"{"error":{"code":-32600,"message":"Message too large"},"id":null,"jsonrpc":"2.0"}"#])
    }

    func testARequestIdAlreadyRunningIsRefusedWithoutAnID() {
        initialize()
        session.receive(#"{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo"}}"#)
        session.receive(#"{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo"}}"#)
        XCTAssertEqual(sent.last, #"{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}"#)
        XCTAssertEqual(host.calls.count, 1)
        host.calls[0].completion(.object([:]))
        XCTAssertEqual(sent.last, #"{"id":3,"jsonrpc":"2.0","result":{}}"#)
    }

    func testASecondInitializeIsRefusedAndKeepsTheFirstName() {
        initialize()
        session.receive(#"{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"someone-else"}}}"#)
        XCTAssertEqual(sent.last, #"{"error":{"code":-32600,"message":"Invalid Request"},"id":2,"jsonrpc":"2.0"}"#)
        XCTAssertEqual(session.clientName, "claude-ai")
    }

    func testCompactLinesAreOneLineKeySortedJSON() {
        XCTAssertEqual(JSONValue.object(["b": .int(1), "a": .string("x/y “q”"), "c": .array([.bool(true), .null])]).compactLine(),
                       #"{"a":"x\/y “q”","b":1,"c":[true,null]}"#)
    }
}
