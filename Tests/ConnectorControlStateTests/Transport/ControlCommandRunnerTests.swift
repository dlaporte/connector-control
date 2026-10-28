import XCTest
import ConnectorControlCore
@testable import ConnectorControlTransport

/// Mirror: windows/tests/ConnectorControl.Core.Tests/Transport/ControlCommandRunnerTests.cs
final class ControlCommandRunnerTests: XCTestCase {
    /// Hands the runner the lines the test wrote for it, one per read, and keeps what it sends.
    final class ScriptedStream: ControlStream, @unchecked Sendable {
        var written = Data()
        var replies: [String]
        init(_ replies: [String]) { self.replies = replies }
        func read(maximum: Int) -> Data { replies.isEmpty ? Data() : Data((replies.removeFirst() + "\n").utf8) }
        func write(_ data: Data) -> Bool { written.append(data); return true }
        func close() {}
    }

    private let answered = #"{"id":1,"jsonrpc":"2.0","result":{}}"#

    func testTheHelloHandshakeAndCallGoOutTogether() {
        XCTAssertEqual(ControlCommandRunner.openingLines(tool: "get_status", arguments: .object([:]), version: "1.5.0"), [
            #"{"client":"cli","version":"1.5.0"}"#,
            #"{"id":1,"jsonrpc":"2.0","method":"initialize","params":{"capabilities":{},"clientInfo":{"name":"connector-control","version":"1.5.0"},"protocolVersion":"2025-11-25"}}"#,
            #"{"jsonrpc":"2.0","method":"notifications\/initialized"}"#,
            #"{"id":2,"jsonrpc":"2.0","method":"tools\/call","params":{"_meta":{"progressToken":"cli"},"arguments":{},"name":"get_status"}}"#,
        ])
    }

    func testTheCallsAnswerIsWhatPrintsAndProgressIsNoticedOnce() {
        let stream = ScriptedStream([
            answered,
            #"{"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":5,"progressToken":"cli"}}"#,
            #"{"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":10,"progressToken":"cli"}}"#,
            #"{"id":2,"jsonrpc":"2.0","result":{"content":[{"text":"Did it.","type":"text"}],"isError":false,"structuredContent":{}}}"#,
        ])
        var notices = 0
        let output = ControlCommandRunner.run(tool: "get_status", arguments: .object([:]), json: false, version: "1.5.0",
                                              over: stream, notice: { notices += 1 })
        XCTAssertEqual(output, ControlOutput(stdout: "Did it.", stderr: nil, exitCode: 0))
        XCTAssertEqual(notices, 1)
        XCTAssertEqual(String(decoding: stream.written, as: UTF8.self),
                       ControlCommandRunner.openingLines(tool: "get_status", arguments: .object([:]), version: "1.5.0")
                           .map { $0 + "\n" }.joined())
    }

    func testAnErrorWithNoIDIsTheAnswer() {
        let stream = ScriptedStream([#"{"error":{"code":-32600,"message":"Message too large"},"id":null,"jsonrpc":"2.0"}"#])
        XCTAssertEqual(ControlCommandRunner.run(tool: "add_connector", arguments: .object([:]), json: false, version: "1.5.0",
                                                over: stream, notice: {}),
                       ControlOutput(stdout: nil, stderr: McpSession.tooLargeMessage, exitCode: 1))
    }

    func testAnAnswerPastTheLimitEndsTheCommandAsUnreachable() {
        let stream = ScriptedStream([String(repeating: "x", count: 100)])
        XCTAssertEqual(ControlCommandRunner.run(tool: "get_status", arguments: .object([:]), json: false, version: "1.5.0",
                                                over: stream, notice: {}, limit: 64),
                       ControlOutput.unreachable(ControlClient.droppedError))
    }

    func testAConnectionThatEndsBeforeTheAnswerIsUnreachable() {
        XCTAssertEqual(ControlCommandRunner.run(tool: "get_status", arguments: .object([:]), json: false, version: "1.5.0",
                                                over: ScriptedStream([answered]), notice: {}),
                       ControlOutput.unreachable(ControlClient.droppedError))
    }
}
