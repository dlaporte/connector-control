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
        let line = McpSession.errorLine(code: McpSession.invalidParamsCode, message: ControlTools.needsNewerAppError("9.0.0"), id: .int(2))
        let error = try JSONValue.parse(Data(line.utf8))
        XCTAssertEqual(ControlOutput.answer(error, json: false),
                       ControlOutput(stdout: nil, stderr: ControlTools.needsNewerAppError("9.0.0"), exitCode: 1))
        XCTAssertEqual(ControlOutput.answer(error, json: true).stdout,
                       JSONValue.object(["error": .string(ControlTools.needsNewerAppError("9.0.0"))]).compactLine())
    }

    func testTheExitCodes() {
        XCTAssertEqual([ControlOutput.doneExit, ControlOutput.refusedExit, ControlOutput.usageExit, ControlOutput.unreachableExit], [0, 1, 2, 3])
        XCTAssertEqual(ControlOutput.usage("x"), ControlOutput(stdout: nil, stderr: "x", exitCode: 2))
        XCTAssertEqual(ControlOutput.unreachable("y"), ControlOutput(stdout: nil, stderr: "y", exitCode: 3))
    }
}
