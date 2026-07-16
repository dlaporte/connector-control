import XCTest
@testable import MCPEnablerCore

final class ApplyPlanTests: XCTestCase {
    private let configA = JSONValue.object(["command": .string("a")])
    private let configB = JSONValue.object(["command": .string("b")])

    private func store(_ mcps: [String: MCPEntry]) -> MasterStore {
        MasterStore(version: 1, mcps: mcps)
    }

    func testIdenticalStateProducesNoChanges() {
        let s = store(["x": MCPEntry(enabled: true, config: configA)])
        let changes = ApplyPlan.changes(store: s, current: ["x": configA])
        XCTAssertEqual(changes, [])
    }

    func testEnabledEntryNotInCurrentIsAdded() {
        let s = store(["x": MCPEntry(enabled: true, config: configA)])
        let changes = ApplyPlan.changes(store: s, current: [:])
        XCTAssertEqual(changes, ["Add “x”"])
    }

    func testCurrentEntryNotDesiredIsRemoved() {
        let s = store(["x": MCPEntry(enabled: true, config: configA)])
        let changes = ApplyPlan.changes(store: s, current: ["x": configA, "y": configB])
        XCTAssertEqual(changes, ["Remove “y”"])
    }

    func testChangedConfigIsUpdated() {
        let s = store(["x": MCPEntry(enabled: true, config: configB)])
        let changes = ApplyPlan.changes(store: s, current: ["x": configA])
        XCTAssertEqual(changes, ["Update “x”"])
    }

    func testDisabledEntryStillInCurrentIsCountedAsRemoval() {
        let s = store(["x": MCPEntry(enabled: false, config: configA)])
        let changes = ApplyPlan.changes(store: s, current: ["x": configA])
        XCTAssertEqual(changes, ["Remove “x”"])
    }

    func testMultipleChangesSortedByName() {
        let s = store([
            "b": MCPEntry(enabled: true, config: configA),
            "c": MCPEntry(enabled: true, config: configB),
        ])
        let changes = ApplyPlan.changes(
            store: s, current: ["a": configA, "c": configA])
        XCTAssertEqual(changes, ["Add “b”", "Remove “a”", "Update “c”"])
    }
}
