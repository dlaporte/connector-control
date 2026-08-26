import XCTest
@testable import ConnectorControlCore

final class ReconcilerTests: XCTestCase {
    private let configA = JSONValue.object(["command": .string("a")])
    private let configB = JSONValue.object(["command": .string("b")])

    private func store(_ mcps: [String: MCPEntry]) -> MasterStore {
        MasterStore(version: 1, mcps: mcps)
    }

    // MARK: ingestion — the only file→store flow

    func testUnknownServerIsImportedEnabled() {
        let outcome = Reconciler.reconcile(store: .empty, claudeServers: ["new": configA])
        XCTAssertEqual(outcome.store.mcps["new"],
                       MCPEntry(enabled: true, config: configA, lastEditView: .form))
        XCTAssertTrue(outcome.storeChanged)
    }

    func testPendingRemovalNotResurrected() {
        // Connector deleted from the store; Claude's file still lists it,
        // unchanged from the baseline → pending removal, must NOT re-import.
        let outcome = Reconciler.reconcile(
            store: .empty, claudeServers: ["gone": configA],
            baseline: ["gone": configA])
        XCTAssertNil(outcome.store.mcps["gone"])
        XCTAssertFalse(outcome.storeChanged)
    }

    func testExternallyAddedServerImportsMidSession() {
        // Baseline lacks the name → genuinely added outside the app → import.
        let outcome = Reconciler.reconcile(
            store: .empty, claudeServers: ["new": configA], baseline: [:])
        XCTAssertEqual(outcome.store.mcps["new"],
                       MCPEntry(enabled: true, config: configA, lastEditView: .form))
        XCTAssertTrue(outcome.storeChanged)
    }

    // MARK: store is the source of truth — the file never edits known entries

    func testExternalEditDoesNotChangeStore() {
        // Fresh launch (nil baseline): a hand-edit made while the app was off
        // still loses — Claude's config is downstream.
        let outcome = Reconciler.reconcile(
            store: store(["s": MCPEntry(enabled: true, config: configA)]),
            claudeServers: ["s": configB])
        XCTAssertEqual(outcome.store.mcps["s"]?.config, configA)
        XCTAssertFalse(outcome.storeChanged)
    }

    func testExternalEditWithChangedBaselineDoesNotChangeStore() {
        let outcome = Reconciler.reconcile(
            store: store(["s": MCPEntry(enabled: true, config: configA)]),
            claudeServers: ["s": configB], baseline: ["s": configA])
        XCTAssertEqual(outcome.store.mcps["s"]?.config, configA)
        XCTAssertFalse(outcome.storeChanged)
    }

    func testPendingEditSurvivesReloadWhenFileUnchanged() {
        let outcome = Reconciler.reconcile(
            store: store(["s": MCPEntry(enabled: true, config: configB)]),
            claudeServers: ["s": configA], baseline: ["s": configA])
        XCTAssertEqual(outcome.store.mcps["s"]?.config, configB)
        XCTAssertFalse(outcome.storeChanged)
    }

    func testDisabledEntryStaysDisabledWhenPresentInFile() {
        let outcome = Reconciler.reconcile(
            store: store(["s": MCPEntry(enabled: false, config: configA)]),
            claudeServers: ["s": configA], baseline: [:])
        XCTAssertEqual(outcome.store.mcps["s"]?.enabled, false)
        XCTAssertFalse(outcome.storeChanged)
    }

    func testDisabledEntryStaysDisabledWhenExternallyModified() {
        let outcome = Reconciler.reconcile(
            store: store(["s": MCPEntry(enabled: false, config: configA)]),
            claudeServers: ["s": configB], baseline: ["s": configA])
        XCTAssertEqual(outcome.store.mcps["s"]?.enabled, false)
        XCTAssertEqual(outcome.store.mcps["s"]?.config, configA)
        XCTAssertFalse(outcome.storeChanged)
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

    func testEnabledButMissingLeavesStoreUntouched() {
        // External removals never delete or disable; the caller regenerates
        // the file from the store instead.
        let s = store(["gone": MCPEntry(enabled: true, config: configA),
                       "also-gone": MCPEntry(enabled: true, config: configB)])
        let outcome = Reconciler.reconcile(store: s, claudeServers: [:])
        XCTAssertEqual(outcome.store, s)
        XCTAssertFalse(outcome.storeChanged)
    }

    func testDisabledAndAbsentIsNormalNoChange() {
        let s = store(["off": MCPEntry(enabled: false, config: configA)])
        let outcome = Reconciler.reconcile(store: s, claudeServers: [:])
        XCTAssertEqual(outcome.store, s)
        XCTAssertFalse(outcome.storeChanged)
    }

    func testIdenticalStateIsNoChange() {
        let s = store(["s": MCPEntry(enabled: true, config: configA)])
        let outcome = Reconciler.reconcile(store: s, claudeServers: ["s": configA])
        XCTAssertEqual(outcome.store, s)
        XCTAssertFalse(outcome.storeChanged)
    }
}
