import XCTest
@testable import MCPEnablerCore

final class ReconcilerTests: XCTestCase {
    private let configA = JSONValue.object(["command": .string("a")])
    private let configB = JSONValue.object(["command": .string("b")])

    private func store(_ mcps: [String: MCPEntry]) -> MasterStore {
        MasterStore(version: 1, mcps: mcps)
    }

    func testUnknownServerIsImportedEnabled() {
        let outcome = Reconciler.reconcile(store: .empty, claudeServers: ["new": configA])
        XCTAssertEqual(outcome.store.mcps["new"],
                       MCPEntry(enabled: true, config: configA, lastEditView: .form))
        XCTAssertTrue(outcome.storeChanged)
        XCTAssertEqual(outcome.missingEnabled, [])
    }

    func testExternalEditWinsOverStore() {
        let outcome = Reconciler.reconcile(
            store: store(["s": MCPEntry(enabled: true, config: configA, lastEditView: .json)]),
            claudeServers: ["s": configB])
        XCTAssertEqual(outcome.store.mcps["s"]?.config, configB)
        XCTAssertEqual(outcome.store.mcps["s"]?.lastEditView, .json,
                       "view memory must survive reconciliation")
        XCTAssertTrue(outcome.storeChanged)
    }

    func testDisabledButPresentBecomesEnabled() {
        let outcome = Reconciler.reconcile(
            store: store(["s": MCPEntry(enabled: false, config: configA)]),
            claudeServers: ["s": configA], baseline: [:])
        XCTAssertEqual(outcome.store.mcps["s"]?.enabled, true)
        XCTAssertTrue(outcome.storeChanged)
    }

    func testPendingDisableSurvivesReloadWhenFileUnchanged() {
        let outcome = Reconciler.reconcile(
            store: store(["s": MCPEntry(enabled: false, config: configA)]),
            claudeServers: ["s": configA], baseline: ["s": configA])
        XCTAssertEqual(outcome.store.mcps["s"]?.enabled, false)
        XCTAssertFalse(outcome.storeChanged)
    }

    func testPendingDisableSurvivesFreshLaunch() {
        let outcome = Reconciler.reconcile(
            store: store(["s": MCPEntry(enabled: false, config: configA)]),
            claudeServers: ["s": configA], baseline: nil)
        XCTAssertEqual(outcome.store.mcps["s"]?.enabled, false)
        XCTAssertFalse(outcome.storeChanged)
    }

    func testDisabledButExternallyModifiedBecomesEnabled() {
        let outcome = Reconciler.reconcile(
            store: store(["s": MCPEntry(enabled: false, config: configA)]),
            claudeServers: ["s": configB], baseline: ["s": configA])
        XCTAssertEqual(outcome.store.mcps["s"]?.enabled, true)
        XCTAssertEqual(outcome.store.mcps["s"]?.config, configB)
        XCTAssertTrue(outcome.storeChanged)
    }

    func testEnabledButMissingIsFlaggedNotDeleted() {
        let outcome = Reconciler.reconcile(
            store: store(["gone": MCPEntry(enabled: true, config: configA),
                          "also-gone": MCPEntry(enabled: true, config: configB)]),
            claudeServers: [:])
        XCTAssertEqual(outcome.missingEnabled, ["also-gone", "gone"])
        XCTAssertEqual(outcome.store.mcps.count, 2, "never silently deleted")
        XCTAssertFalse(outcome.storeChanged)
    }

    func testDisabledAndAbsentIsNormalNoChange() {
        let s = store(["off": MCPEntry(enabled: false, config: configA)])
        let outcome = Reconciler.reconcile(store: s, claudeServers: [:])
        XCTAssertEqual(outcome.store, s)
        XCTAssertFalse(outcome.storeChanged)
        XCTAssertEqual(outcome.missingEnabled, [])
    }

    func testIdenticalStateIsNoChange() {
        let s = store(["s": MCPEntry(enabled: true, config: configA)])
        let outcome = Reconciler.reconcile(store: s, claudeServers: ["s": configA])
        XCTAssertEqual(outcome.store, s)
        XCTAssertFalse(outcome.storeChanged)
    }
}
