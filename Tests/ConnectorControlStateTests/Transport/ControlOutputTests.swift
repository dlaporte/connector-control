import XCTest
import ConnectorControlCore
@testable import ConnectorControlState
@testable import ConnectorControlTransport

/// Mirror: windows/tests/ConnectorControl.Core.Tests/Transport/ControlOutputTests.cs
@MainActor
final class ControlOutputTests: XCTestCase {
    private func response(_ result: ControlResult) -> JSONValue {
        .object(["jsonrpc": .string("2.0"), "id": .int(2), "result": result.mcpResult])
    }

    func testAnAnswerPrintsItsTextOrUnderJSONItsStructure() {
        let done = response(ControlResult(structured: .object(["applied": .bool(true)]), text: "Turned on “x”.", isError: false))
        XCTAssertEqual(ControlOutput.answer(done, json: false), ControlOutput(stdout: "Turned on “x”.", stderr: nil, exitCode: 0))
        XCTAssertEqual(ControlOutput.answer(done, json: true), ControlOutput(stdout: #"{"applied":true}"#, stderr: nil, exitCode: 0))
    }

    func testARefusalGoesToStderrAndExitsOne() {
        let refused = response(.refusal("No such thing."))
        XCTAssertEqual(ControlOutput.answer(refused, json: false), ControlOutput(stdout: nil, stderr: "No such thing.", exitCode: 1))
        XCTAssertEqual(ControlOutput.answer(refused, json: true), ControlOutput(stdout: #"{"error":"No such thing."}"#, stderr: nil, exitCode: 1))
    }

    func testAnErrorTheSessionAnsweredIsARefusal() throws {
        // A slash in the sentence tells the escaped writer from the unescaped one.
        let message = ControlTools.needsNewerAppError("9.0.0") + " See /Applications/Connector Control.app."
        let line = McpSession.errorLine(code: McpSession.invalidParamsCode, message: message, id: .int(2))
        let error = try JSONValue.parse(Data(line.utf8))
        XCTAssertEqual(ControlOutput.answer(error, json: false),
                       ControlOutput(stdout: nil, stderr: message, exitCode: 1))
        XCTAssertEqual(ControlOutput.answer(error, json: true).stdout,
                       JSONValue.object(["error": .string(message)]).compactLine(escapingSlashes: false))
    }

    /// `--json` is for reading and scripts: a path comes out as written. The wire format still escapes it.
    func testJSONOutputWritesSlashesAsTheyAre() {
        let path = "/Users/me/work/team.json"
        let done = response(ControlResult(structured: .object(["path": .string(path)]), text: "x", isError: false))
        XCTAssertEqual(ControlOutput.answer(done, json: true).stdout, #"{"path":"/Users/me/work/team.json"}"#)
        XCTAssertEqual(ControlOutput.answer(response(.refusal(path)), json: true).stdout, #"{"error":"/Users/me/work/team.json"}"#)
        XCTAssertEqual(JSONValue.string(path).compactLine(), #""\/Users\/me\/work\/team.json""#, "the wire format is unchanged")
        XCTAssertEqual(JSONValue.string(path).compactLine(escapingSlashes: false), #""/Users/me/work/team.json""#)
    }

    func testTheExitCodes() {
        XCTAssertEqual([ControlOutput.doneExit, ControlOutput.refusedExit, ControlOutput.usageExit, ControlOutput.unreachableExit], [0, 1, 2, 3])
        XCTAssertEqual(ControlOutput.usage("x"), ControlOutput(stdout: nil, stderr: "x", exitCode: 2))
        XCTAssertEqual(ControlOutput.unreachable("y"), ControlOutput(stdout: nil, stderr: "y", exitCode: 3))
    }
}
