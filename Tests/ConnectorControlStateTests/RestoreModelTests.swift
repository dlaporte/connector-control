import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/RestoreModelTests.cs; the
/// confirmation is a sheet (pending state + the button's method).
@MainActor
final class RestoreModelTests: XCTestCase {
    /// Each row is the backup's time, as the Collections tab names its backups (" (2)" for a later one
    /// in the same second), and the first-run original, last, reads as what it is.
    func testListsBackupsNewestFirstWithTheOriginalLast() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        state.setEnabled("aws-mcp", false)           // backup 1 (three servers) + original snapshot
        Thread.sleep(forTimeInterval: 0.005)          // a different millisecond in the next backup's name
        state.setEnabled("scoutbook", false)         // backup 2 (two servers)
        let model = RestoreModel(state: state)
        model.load()
        XCTAssertEqual(model.backups.count, 3)
        let names = model.backups.map(\.lastPathComponent)
        XCTAssertTrue(names[0].hasPrefix("claude_desktop_config."))
        XCTAssertGreaterThan(names[0], names[1])   // newest first
        XCTAssertEqual(names[2], "claude_desktop_config.original.json")
        let labels = BackupManager.labels(for: model.backups)
        let times = try [XCTUnwrap(labels[names[0]]), XCTUnwrap(labels[names[1]])]
        XCTAssertEqual(model.backups.map(model.label(of:)), times + [RestoreModel.originalLabel])
        XCTAssertNotEqual(times[0], names[0], "a time, not a file name")
        XCTAssertNil(model.selection)
        XCTAssertFalse(model.canRestore)
    }

    /// A listing failure used to be swallowed by
    /// `try?`, leaving an empty list with no explanation; it now surfaces.
    func testLoadSurfacesABackupsListingFailure() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        state.setEnabled("aws-mcp", false)   // a real backup to list, if listing worked
        let model = RestoreModel(state: state)
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: h.backupsDir.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: h.backupsDir.path) }

        model.load()
        XCTAssertTrue(model.backups.isEmpty)
        XCTAssertNotNil(model.restoreError)
    }

    func testRestoreConfirmsWithTheBackupsTimeAndRestoresThroughAppState() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        state.setEnabled("aws-mcp", false)
        let model = RestoreModel(state: state)
        model.load()
        model.selection = model.backups[0]
        XCTAssertTrue(model.canRestore)

        model.requestRestore()
        XCTAssertTrue(model.confirming)
        let time = try XCTUnwrap(BackupManager.labels(for: model.backups)[model.backups[0].lastPathComponent])
        XCTAssertEqual(model.confirmMessage, "Replace Claude’s config with the backup from \(time)?")
        XCTAssertNil(model.confirmInformative, "its collection is still there")
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

    /// A backup whose collection is gone says, under the confirmation's message, that the collection is
    /// created again with only what Claude was running; the restore then makes it so.
    func testAGoneCollectionsRestoreSaysItIsCreatedAgain() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createActiveCopy(named: "Gone"))
        XCTAssertNil(state.upsert(name: "gone-only", entry: AppStateHarness.localConnector("g"), renamedFrom: nil, in: "Gone"))
        state.apply()
        state.switchCollection(to: "Default")   // backs up Gone's file, recorded as Gone's
        XCTAssertNil(state.deleteCollection(named: "Gone"))
        let model = RestoreModel(state: state)
        model.load()
        model.selection = model.backups[0]
        XCTAssertEqual(BackupCollections.collection(of: model.backups[0], in: h.backupsDir), "Gone")

        model.requestRestore()
        XCTAssertTrue(model.confirming)
        XCTAssertEqual(model.confirmMessage, RestoreModel.confirmMessage(model.label(of: model.backups[0])))
        XCTAssertEqual(model.confirmInformative, RestoreModel.recreateInformative("Gone"))
        XCTAssertTrue(model.confirmRestore())
        XCTAssertEqual(state.activeCollection, "Gone")
        XCTAssertNotNil(state.store.collections["Gone"]?.mcps["gone-only"])
        XCTAssertNil(model.restoreError)
    }

    /// The first-run original has no time to name it by, so its question names it as what it is.
    func testTheFirstRunOriginalsQuestionNamesIt() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        state.setEnabled("aws-mcp", false)           // backup 1 + original snapshot
        let model = RestoreModel(state: state)
        model.load()
        let original = try XCTUnwrap(model.backups.last)
        XCTAssertEqual(original.lastPathComponent, "claude_desktop_config.original.json")
        model.selection = original

        model.requestRestore()
        XCTAssertTrue(model.confirming)
        XCTAssertEqual(model.confirmMessage, "Replace Claude’s config with the first-run original?")
        XCTAssertEqual(model.confirmMessage, RestoreModel.confirmOriginalMessage)
        model.cancelRestore()
    }

    /// What is restored is the backup the user was asked about, not the file as it is by the time
    /// they confirm.
    func testTheRestoreLandsTheBackupAsItWasWhenAsked() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        state.setEnabled("aws-mcp", false)
        let model = RestoreModel(state: state)
        model.load()
        let backup = model.backups[0]
        model.selection = backup
        let asked = try ClaudeConfigIO.readMCPServers(at: backup)
        model.requestRestore()
        XCTAssertTrue(model.confirming)
        try Data(#"{"mcpServers":{"swapped":{"command":"evil"}}}"#.utf8).write(to: backup)
        XCTAssertTrue(model.confirmRestore())
        XCTAssertEqual(try h.claudeServers(), asked)
    }

    /// A restore AppState would refuse is refused before the confirmation asks anything.
    func testARefusedRestoreSaysWhyWithoutAsking() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        state.switchCollection(to: "Data team")
        let model = RestoreModel(state: state)
        model.load()
        model.selection = try XCTUnwrap(state.service.backups.originalSnapshotURL(series: RestoreModel.series))
        let banner = state.lastError
        model.requestRestore()
        XCTAssertFalse(model.confirming)
        XCTAssertEqual(model.restoreError, AppState.restoreSubscribedError("Data team"))
        XCTAssertEqual(state.lastError, banner, "a refusal is the sheet's alone")
        XCTAssertEqual(state.activeCollection, "Data team")
    }

    /// A refusal is the sheet's alone on either side of the confirmation: nothing was restored, so
    /// the banner has nothing to say. The collection becomes subscribed-active while it is up.
    func testARefusalAfterTheConfirmationIsTheSheetsAlone() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        // There and back, so the first apply leaves the first-run original among the backups.
        state.switchCollection(to: "Data team")
        state.switchCollection(to: "Default")
        let model = RestoreModel(state: state)
        model.load()
        model.selection = try XCTUnwrap(state.service.backups.originalSnapshotURL(series: RestoreModel.series))
        model.requestRestore()
        XCTAssertTrue(model.confirming, "the active collection is local, so it asks")

        state.switchCollection(to: "Data team")
        let banner = state.lastError
        XCTAssertFalse(model.confirmRestore())
        XCTAssertEqual(model.restoreError, AppState.restoreSubscribedError("Data team"))
        XCTAssertEqual(state.lastError, banner)
        XCTAssertEqual(state.activeCollection, "Data team")
    }

    func testRestoreFailureShowsInlineAndInLastError() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let bad = h.backupsDir.appendingPathComponent("claude_desktop_config.2026-09-04T00-00-00-000Z.json")
        try FileManager.default.createDirectory(at: h.backupsDir, withIntermediateDirectories: true)
        try Data("{not json".utf8).write(to: bad)
        let model = RestoreModel(state: state)
        model.load()
        model.selection = bad
        model.requestRestore()
        XCTAssertFalse(model.confirmRestore())
        XCTAssertEqual(model.restoreError,
                       "backup claude_desktop_config.2026-09-04T00-00-00-000Z.json is not a valid config file "
                       + "(the text does not parse as JSON)")
        XCTAssertEqual(state.lastError, model.restoreError)
        XCTAssertFalse(model.confirming)
    }

    /// A fresh attempt starts with a clean sheet - the previous
    /// attempt's error must not outlive a new selection or a cancelled confirmation.
    func testRequestRestoreClearsThePreviousError() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
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

    /// The backup-file series name: an internal identifier, deliberately out
    /// of the shared string catalog (it names an on-disk file series, not
    /// anything the user reads).
    func testSeriesNameIsSessionLocalAndStable() {
        XCTAssertEqual(RestoreModel.series, "claude_desktop_config")
    }
}
