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
        func closeWrite() {}
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

    /// An answer that follows the oversized line is never reached, so only a working limit ends in the
    /// dropped result: a broken one would print the answer.
    func testAnOversizedLineIsNeverFollowedByTheAnswerThatComesAfterIt() {
        let stream = ScriptedStream([String(repeating: "x", count: 100), answerLine(padded: 0)])
        XCTAssertEqual(ControlCommandRunner.run(tool: "get_status", arguments: .object([:]), json: false, version: "1.5.0",
                                                over: stream, notice: {}, limit: 64),
                       ControlOutput.unreachable(ControlClient.droppedError))
    }

    func testALineJustOverTheLimitIsRefusedAndOneJustUnderIsAccepted() {
        let limit = answerLine(padded: 0).utf8.count + 10
        XCTAssertEqual(ControlCommandRunner.run(tool: "get_status", arguments: .object([:]), json: false, version: "1.5.0",
                                                over: ScriptedStream([answerLine(padded: 11)]), notice: {}, limit: limit),
                       ControlOutput.unreachable(ControlClient.droppedError), "limit + 1 bytes")
        XCTAssertEqual(ControlCommandRunner.run(tool: "get_status", arguments: .object([:]), json: false, version: "1.5.0",
                                                over: ScriptedStream([answerLine(padded: 10)]), notice: {}, limit: limit),
                       ControlOutput(stdout: String(repeating: "p", count: 10), stderr: nil, exitCode: 0), "exactly the limit")
    }

    /// The default is the client's 64 MB, not the framer's 1 MB, which a long answer would pass.
    func testTheDefaultLimitIsTheClientsAnswerLimit() {
        XCTAssertEqual(ControlClient.maxAnswerBytes, 64 * 1_048_576)
        let size = McpSession.maxMessageBytes + 1
        XCTAssertEqual(ControlCommandRunner.run(tool: "get_status", arguments: .object([:]), json: false, version: "1.5.0",
                                                over: ScriptedStream([answerLine(padded: size)]), notice: {}).exitCode, 0)
    }

    /// The call's answer with `padded` bytes of text in it.
    private func answerLine(padded: Int) -> String {
        let text = String(repeating: "p", count: padded)
        return #"{"id":2,"jsonrpc":"2.0","result":{"content":[{"text":""# + text
            + #"","type":"text"}],"isError":false,"structuredContent":{}}}"#
    }

    func testAConnectionThatEndsBeforeTheAnswerIsUnreachable() {
        XCTAssertEqual(ControlCommandRunner.run(tool: "get_status", arguments: .object([:]), json: false, version: "1.5.0",
                                                over: ScriptedStream([answered]), notice: {}),
                       ControlOutput.unreachable(ControlClient.droppedError))
    }
}
