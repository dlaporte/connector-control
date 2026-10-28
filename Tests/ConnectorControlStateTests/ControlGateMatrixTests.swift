import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlGateMatrixTests.cs
///
/// The gate on real tools, one of each class: every caller, both confirmation settings, the
/// integration on and off — whether the call was refused, asked about, run, and announced.
@MainActor
final class ControlGateMatrixTests: XCTestCase {
    /// One real tool of each class, and how to tell it ran.
    private let samples: [(ControlGateClass, String, [String: JSONValue], (ControlRig, ControlResult?) -> Bool)] = [
        (.read, "get_status", [:], { _, result in result?.structured.value(at: JSONPointer(["activeCollection"])) != nil }),
        (.direct, "switch_collection", ["collection": .string("Work")], { rig, _ in rig.state.activeCollection == "Work" }),
        (.gated, "add_connector", ["name": .string("x"), "config": .object(["command": .string("node")])],
         { rig, _ in rig.state.store.mcps["x"] != nil }),
        (.alwaysConfirm, "restart_claude", [:], { rig, _ in rig.h.claude.restartCalls == 1 }),
    ]

    func testTheMatrix() {
        for (gate, name, arguments, ran) in samples {
            XCTAssertEqual(ControlTools.catalog.first { $0.name == name }?.gate, gate, name)
            for caller in [ControlCaller.cli, .relay(clientName: "claude-ai")] {
                for integration in [true, false] {
                    for confirm in [true, false] {
                        let rig = ControlRig(integration: integration, confirm: confirm)
                        rig.confirmer.scripted = true
                        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
                        let label = "\(name) \(caller) integration:\(integration) confirm:\(confirm)"
                        XCTAssertTrue(rig.h.notifier.sent.isEmpty, label)
                        let result = rig.call(name, arguments, as: caller)
                        let relay = caller != .cli
                        let refused = relay && !integration
                        let asked = relay && integration && (gate == .alwaysConfirm || (gate == .gated && confirm))
                        let announced = relay && integration && gate == .gated && !confirm
                        XCTAssertEqual(result == .refusal(ControlTools.integrationOffError), refused, label)
                        XCTAssertEqual(!rig.confirmer.requests.isEmpty, asked, label)
                        XCTAssertEqual(!rig.h.notifier.sent.isEmpty, announced, label)
                        XCTAssertEqual(ran(rig, result), !refused, label)
                        if !refused { XCTAssertEqual(result?.isError, false, label) }
                        rig.dispose()
                    }
                }
            }
        }
    }
}
