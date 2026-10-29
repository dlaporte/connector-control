import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlGateTests.cs
@MainActor
final class ControlGateTests: XCTestCase {
    private let classes: [ControlGateClass] = [.read, .direct, .gated, .alwaysConfirm]

    func testTheCommandLineIsNeverGated() {
        for gate in classes {
            for integration in [true, false] {
                for confirm in [true, false] {
                    XCTAssertEqual(ControlGate.decide(caller: .cli, gate: gate, integrationEnabled: integration,
                                                      confirmChanges: confirm), .run)
                }
            }
        }
    }

    func testTheRelaysMatrix() {
        let off = ControlGate.Decision.refuse(ControlTools.integrationOffError)
        // gate class, integration on, confirmations on, what happens
        let rows: [(ControlGateClass, Bool, Bool, ControlGate.Decision)] = [
            (.read, true, true, .run), (.read, true, false, .run), (.read, false, true, off), (.read, false, false, off),
            (.direct, true, true, .run), (.direct, true, false, .run), (.direct, false, true, off), (.direct, false, false, off),
            (.gated, true, true, .confirm), (.gated, true, false, .runAndNotify), (.gated, false, true, off), (.gated, false, false, off),
            (.alwaysConfirm, true, true, .confirm), (.alwaysConfirm, true, false, .confirm),
            (.alwaysConfirm, false, true, off), (.alwaysConfirm, false, false, off),
        ]
        for (gate, integration, confirm, expected) in rows {
            XCTAssertEqual(ControlGate.decide(caller: .relay(clientName: "claude-ai"), gate: gate,
                                              integrationEnabled: integration, confirmChanges: confirm),
                           expected, "\(gate) integration:\(integration) confirm:\(confirm)")
        }
    }

    func testTheAskerIsClaudeForClaudeDesktopAndTheClientsOwnNameOtherwise() {
        XCTAssertEqual(ControlCaller.relay(clientName: "claude-ai").askerName, "Claude")
        XCTAssertEqual(ControlCaller.relay(clientName: "Claude Desktop").askerName, "Claude")
        XCTAssertEqual(ControlCaller.relay(clientName: "local-agent-mode-connector-control").askerName, "Claude")
        XCTAssertEqual(ControlCaller.relay(clientName: "Cursor").askerName, "Cursor")
        XCTAssertEqual(ControlCaller.relay(clientName: "").askerName, ControlCaller.unknownClientName)
    }

    func testArgumentsAreCheckedAgainstTheSchemaBeforeAnythingRuns() {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertEqual(rig.call("get_connector"), .refusal(ControlTools.missingArgumentError("name")))
        XCTAssertEqual(rig.call("get_connector", ["name": .int(3)]),
                       .refusal(ControlTools.wrongTypeError("name", ControlTools.textKind)))
        XCTAssertEqual(rig.call("set_setting", ["key": .string("theme"), "value": .string("on")]),
                       .refusal(ControlTools.notOneOfError("key", ControlSettingKey.all.joined(separator: ", "))))
        XCTAssertEqual(rig.call("no_such_tool"), .refusal(ControlTools.unknownToolError("no_such_tool")))
        XCTAssertEqual(rig.call("get_status")?.isError, false)
    }

    func testEveryRelayCallIsRefusedWhileTheIntegrationIsOff() {
        let rig = ControlRig(integration: false); defer { rig.dispose() }
        XCTAssertEqual(rig.call("get_status"), .refusal(ControlTools.integrationOffError))
        XCTAssertEqual(rig.call("list_connectors"), .refusal(ControlTools.integrationOffError))
        XCTAssertEqual(rig.call("get_status", as: .cli)?.isError, false, "the command line is the user")
    }

    func testAResultCarriesItsTextAndStructureTheWayMCPWantsThem() {
        let result = ControlResult(structured: .object(["a": .int(1)]), text: "done", isError: false)
        XCTAssertEqual(result.mcpResult, .object([
            "content": .array([.object(["type": .string("text"), "text": .string("done")])]),
            "structuredContent": .object(["a": .int(1)]),
            "isError": .bool(false),
        ]))
        XCTAssertEqual(ControlResult.refusal("no"), ControlResult(structured: .object(["error": .string("no")]), text: "no", isError: true))
    }

    /// The tool name a refusal echoes is Claude's own text, so it shows as a name does.
    func testAnUnknownToolShowsItsNameOneWayOnly() {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertEqual(rig.call("bad\n\\name"), .refusal(ControlTools.unknownToolError(#"bad\n\\name"#)))
    }
}
