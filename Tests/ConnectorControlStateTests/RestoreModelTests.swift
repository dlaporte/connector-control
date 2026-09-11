import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/RestoreModelTests.cs; the
/// confirmation is a sheet (pending state + the button's method).
@MainActor
final class RestoreModelTests: XCTestCase {
    func testListsBackupsNewestFirstWithTheOriginalLast() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.setEnabled("aws-mcp", false)           // backup 1 (three servers) + original snapshot
        Thread.sleep(forTimeInterval: 0.005)          // a different millisecond in the next backup's name
        state.setEnabled("scoutbook", false)         // backup 2 (two servers)
        let model = RestoreModel(state: state)
        model.load()
        XCTAssertEqual(model.backups.count, 3)
        XCTAssertTrue(model.backupNames[0].hasPrefix("claude_desktop_config."))
        XCTAssertGreaterThan(model.backupNames[0], model.backupNames[1])   // newest first
        XCTAssertEqual(model.backupNames[2], "claude_desktop_config.original.json")
        XCTAssertNil(model.selection)
        XCTAssertFalse(model.canRestore)
    }

    /// Commit-in-progress (K-6): a listing failure used to be swallowed by
    /// `try?`, leaving an empty list with no explanation; it now surfaces.
    func testLoadSurfacesABackupsListingFailure() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.setEnabled("aws-mcp", false)   // a real backup to list, if listing worked
        let model = RestoreModel(state: state)
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: h.backupsDir.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: h.backupsDir.path) }

        model.load()
        XCTAssertTrue(model.backups.isEmpty)
        XCTAssertTrue(model.hasRestoreError)
    }

    func testRestoreConfirmsWithTheFileNameAndRestoresThroughAppState() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.setEnabled("aws-mcp", false)
        let model = RestoreModel(state: state)
        model.load()
        model.selection = model.backups[0]
        XCTAssertTrue(model.canRestore)

        model.requestRestore()
        XCTAssertTrue(model.confirming)
        XCTAssertEqual(model.confirmMessage, "Replace Claude's config with \(model.backupNames[0])?")
        XCTAssertEqual(RestoreModel.restoreButton, "Restore")
        model.cancelRestore()
        XCTAssertFalse(model.confirming)
        XCTAssertEqual(state.store.mcps["aws-mcp"]?.enabled, false)   // cancelled: nothing restored

        model.requestRestore()
        XCTAssertTrue(model.confirmRestore())
        XCTAssertFalse(model.confirming)
        XCTAssertEqual(state.store.mcps["aws-mcp"]?.enabled, true)
        XCTAssertNil(model.restoreError)
        XCTAssertTrue(h.dialogs.confirms.isEmpty)   // a sheet, not an NSAlert
    }

    func testRestoreFailureShowsInlineAndInLastError() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let bad = h.backupsDir.appendingPathComponent("claude_desktop_config.2026-09-04T00-00-00-000Z.json")
        try FileManager.default.createDirectory(at: h.backupsDir, withIntermediateDirectories: true)
        try Data("{not json".utf8).write(to: bad)
        let model = RestoreModel(state: state)
        model.load()
        model.selection = bad
        model.requestRestore()
        XCTAssertFalse(model.confirmRestore())
        XCTAssertEqual(model.restoreError, "backup claude_desktop_config.2026-09-04T00-00-00-000Z.json is not a valid config file")
        XCTAssertEqual(state.lastError, model.restoreError)
        XCTAssertTrue(model.hasRestoreError)
        XCTAssertFalse(model.confirming)
    }

    /// Commit 8c61005: a fresh attempt starts with a clean sheet — the previous
    /// attempt's error must not outlive a new selection or a cancelled confirmation.
    func testRequestRestoreClearsThePreviousError() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let bad = h.backupsDir.appendingPathComponent("claude_desktop_config.2026-09-04T00-00-00-000Z.json")
        try FileManager.default.createDirectory(at: h.backupsDir, withIntermediateDirectories: true)
        try Data("{not json".utf8).write(to: bad)
        let model = RestoreModel(state: state)
        model.load()
        model.selection = bad
        model.requestRestore()
        XCTAssertFalse(model.confirmRestore())
        XCTAssertNotNil(model.restoreError)

        model.requestRestore()
        XCTAssertNil(model.restoreError)
        XCTAssertTrue(model.confirming)
        model.cancelRestore()
        XCTAssertNil(model.restoreError)

        model.selection = nil
        model.requestRestore()   // nothing selected: no sheet, and still no stale error
        XCTAssertFalse(model.confirming)
        XCTAssertNil(model.restoreError)
    }

    func testStringsMatchTheMacApp() {
        XCTAssertEqual(RestoreModel.headline, "Restore Claude config from a backup")
        XCTAssertEqual(RestoreModel.caption, "The current file is backed up first, then replaced by the selected backup.")
        XCTAssertEqual(RestoreModel.cancelTitle, "Cancel")
        XCTAssertEqual(RestoreModel.restoreTitle, "Restore…")
        XCTAssertEqual(RestoreModel.restoreButton, "Restore")
        XCTAssertEqual(RestoreModel.series, "claude_desktop_config")
        XCTAssertEqual(RestoreModel.confirmMessage(fileName: "x.json"), "Replace Claude's config with x.json?")
    }
}
