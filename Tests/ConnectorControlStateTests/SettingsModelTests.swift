import Combine
import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/SettingsModelTests.cs, with
/// the Mac's Claude tab (an app path, not an install kind / config path /
/// launch target) and Sparkle behind the Updater seam.
@MainActor
final class SettingsModelTests: XCTestCase {
    @MainActor
    private final class Rig {
        let h = AppStateHarness()
        let state: AppState
        let autostart = FakeAutostart()
        let updater = FakeUpdater()
        let model: SettingsModel

        init() {
            state = h.create()
            model = SettingsModel(state: state, settings: h.settings, autostart: autostart, updater: updater)
        }

        func dispose() {
            model.dispose()
            h.dispose()
        }
    }

    // MARK: General

    func testLaunchAtLoginTogglesAutostart() {
        let rig = Rig()
        defer { rig.dispose() }
        XCTAssertFalse(rig.model.launchAtLogin)
        rig.model.launchAtLogin = true
        XCTAssertTrue(rig.autostart.enabled)
        XCTAssertNil(rig.model.loginItemNote)
        rig.model.launchAtLogin = false
        XCTAssertFalse(rig.autostart.enabled)
        XCTAssertEqual(rig.autostart.setCalls, 2)
    }

    func testLaunchAtLoginDoesNothingWhenTheOsAlreadyAgrees() {
        let rig = Rig()
        defer { rig.dispose() }
        rig.autostart.enabled = true   // enabled behind our back (System Settings)
        rig.model.refresh()
        XCTAssertTrue(rig.model.launchAtLogin)
        rig.model.launchAtLogin = true
        XCTAssertEqual(rig.autostart.setCalls, 0)
    }

    func testLaunchAtLoginFailureRevertsAndNotes() {
        let rig = Rig()
        defer { rig.dispose() }
        XCTAssertNil(rig.model.loginItemNote)
        rig.autostart.failWith = "Access is denied."
        var raised = 0
        let subscription = rig.model.objectWillChange.sink { _ in raised += 1 }
        defer { subscription.cancel() }
        rig.model.launchAtLogin = true
        XCTAssertFalse(rig.model.launchAtLogin)
        XCTAssertEqual(rig.model.loginItemNote, "Couldn't update login item: Access is denied.")
        XCTAssertGreaterThan(raised, 0)
        rig.autostart.failWith = nil
        rig.model.launchAtLogin = true
        XCTAssertNil(rig.model.loginItemNote)
    }

    /// macOS only: registered, but System Settings has not approved it yet.
    func testLaunchAtLoginRequiringApprovalShowsTheApprovalNote() {
        let rig = Rig()
        defer { rig.dispose() }
        rig.autostart.requiresApproval = true
        rig.model.launchAtLogin = true
        XCTAssertEqual(rig.autostart.setCalls, 1)
        XCTAssertTrue(rig.model.launchAtLogin)
        XCTAssertEqual(rig.model.loginItemNote, "Approve Connector Control under System Settings → General → Login Items.")
        rig.model.launchAtLogin = false
        XCTAssertNil(rig.model.loginItemNote)   // the note is about turning it on
    }

    func testConfirmAndNotifyTogglesPersistToSettings() {
        let rig = Rig()
        defer { rig.dispose() }
        rig.model.confirmBeforeRestart = false
        rig.model.confirmBeforeQuit = false
        rig.model.notifyExternalChanges = false
        rig.model.autoUpdate = false
        XCTAssertFalse(rig.h.settings.confirmBeforeRestart)
        XCTAssertFalse(rig.h.settings.confirmBeforeQuit)
        XCTAssertFalse(rig.h.settings.notifyExternalChanges)
        XCTAssertFalse(rig.updater.automaticallyDownloadsUpdates)   // Sparkle persists this one itself
        // A flag changed in Sparkle's own dialog must not leave the toggle
        // stale: the republish in init is the one line a passthrough getter
        // cannot prove, so assert it and assert dispose() cuts it.
        var repaints = 0
        let subscription = rig.model.objectWillChange.sink { _ in repaints += 1 }
        defer { subscription.cancel() }
        rig.updater.automaticallyDownloadsUpdates = true
        XCTAssertTrue(rig.model.autoUpdate)
        XCTAssertGreaterThan(repaints, 0, "Sparkle's own change is republished to the view")
        rig.model.dispose()
        let before = repaints
        rig.updater.automaticallyDownloadsUpdates = false
        XCTAssertFalse(rig.model.autoUpdate)
        XCTAssertEqual(repaints, before)
    }

    func testUpdatesAreDisabledForADevelopmentBuild() {
        let rig = Rig()
        defer { rig.dispose() }
        rig.updater.isAvailable = false
        rig.updater.versionDisplay = "development build"
        rig.model.refresh()
        XCTAssertFalse(rig.model.updatesEnabled)
        XCTAssertEqual(rig.model.versionText, "Version development build")
    }

    func testCheckForUpdatesRunsAnInteractiveCheck() {
        let rig = Rig()
        defer { rig.dispose() }
        rig.updater.versionDisplay = "1.3.0"
        XCTAssertEqual(rig.model.versionText, "Version 1.3.0")
        rig.model.checkForUpdates()
        XCTAssertEqual(rig.updater.checks, 1)   // Sparkle shows "up to date" or the update itself
    }

    // MARK: Storage

    func testBackupKeepCountClampsAndRebuildsTheService() {
        let rig = Rig()
        defer { rig.dispose() }
        XCTAssertEqual(rig.model.keepCountLabel, "Keep 20 backups of each file")
        rig.model.backupKeepCount = 500
        XCTAssertEqual(rig.model.backupKeepCount, 100)
        XCTAssertEqual(rig.h.settings.backupKeepCount, 100)
        XCTAssertEqual(rig.state.service.backups.keepCount, 100)
        rig.model.backupKeepCount = 1
        XCTAssertEqual(rig.model.backupKeepCount, 5)
        rig.model.decrementKeepCount()
        XCTAssertEqual(rig.model.backupKeepCount, 5)
        rig.model.incrementKeepCount()
        XCTAssertEqual(rig.model.backupKeepCount, 6)
        XCTAssertEqual(rig.model.keepCountLabel, "Keep 6 backups of each file")
    }

    func testStoreLocationFollowsRepointing() {
        let rig = Rig()
        defer { rig.dispose() }
        XCTAssertEqual(rig.model.storeDir.path, rig.h.storeDir.path)
        XCTAssertFalse(rig.model.canUseDefaultStore)
        let synced = rig.h.dir.file("synced")
        rig.model.chooseStoreDir(synced)
        XCTAssertEqual(rig.model.storeDir.path, synced.path)
        XCTAssertTrue(rig.model.canUseDefaultStore)
        XCTAssertEqual(rig.model.backupsDir.path, rig.h.backupsDir.path)
        rig.model.useDefaultStoreDir()
        XCTAssertEqual(rig.model.storeDir.path, rig.h.storeDir.path)
        XCTAssertFalse(rig.model.canUseDefaultStore)
    }

    // MARK: Claude

    func testClaudeAppPathShowsTheOverrideOrTheDefault() {
        let rig = Rig()
        defer { rig.dispose() }
        XCTAssertEqual(rig.model.claudeApp.path, "/Applications/Claude.app")
        XCTAssertFalse(rig.model.canUseDefaultClaudeApp)
        rig.model.chooseClaudeApp(URL(fileURLWithPath: "/Applications/Claude Beta.app"))
        XCTAssertEqual(rig.model.claudeApp.path, "/Applications/Claude Beta.app")
        XCTAssertEqual(rig.h.settings.claudeAppPath, "/Applications/Claude Beta.app")
        XCTAssertTrue(rig.model.canUseDefaultClaudeApp)
        rig.model.useDefaultClaudeApp()
        XCTAssertNil(rig.h.settings.claudeAppPath)
        XCTAssertEqual(rig.model.claudeApp.path, "/Applications/Claude.app")
        XCTAssertFalse(rig.model.canUseDefaultClaudeApp)
        rig.h.settings.claudeAppPath = "/Applications/Claude.app"   // an older build wrote the default literally
        XCTAssertFalse(rig.model.canUseDefaultClaudeApp, "Use Default stays disabled when the stored path IS the default")
    }

    /// The two numeric bounds: not strings, so StringCatalogTests can't cover them.
    func testKeepCountBoundsAreSessionLocalAndStable() {
        XCTAssertEqual(SettingsModel.minKeepCount, 5)
        XCTAssertEqual(SettingsModel.maxKeepCount, 100)
    }

    // MARK: Tools

    func testToolRowsStartAsCheckingAndFillInAfterARefresh() throws {
        let rig = Rig()
        defer { rig.dispose() }
        rig.h.tools.statuses[.npx] = .found(path: "/opt/homebrew/bin/npx", version: "10.9.2")
        rig.h.tools.statuses[.node] = .found(path: "/opt/homebrew/bin/node", version: nil)
        rig.h.tools.statuses[.uvx] = .notFound
        rig.h.tools.statuses[.uv] = .notFound
        XCTAssertEqual(rig.model.toolRows.map(\.name), ["npx", "node", "uvx", "uv"])
        XCTAssertTrue(rig.model.toolRows.allSatisfy { $0.statusText == "Checking…" })
        XCTAssertTrue(rig.model.toolRows.allSatisfy { !$0.isProblem })
        XCTAssertTrue(rig.model.toolRows.allSatisfy { $0.note == nil })
        rig.model.refreshTools()
        XCTAssertTrue(rig.h.ui.pumpUntil({ rig.state.toolStatuses.count == 4 }, timeout: 5))
        let rows = rig.model.toolRows
        XCTAssertEqual(rows.map(\.statusText), ["10.9.2", "Found", "Not found", "Not found"])
        XCTAssertEqual(rows.map(\.isProblem), [false, false, true, true])
        XCTAssertNil(rows[0].note)
        XCTAssertNil(rows[1].note)
        XCTAssertEqual(try XCTUnwrap(rows[2].note).linkTitle, "Install uv")
        XCTAssertEqual(try XCTUnwrap(rows[3].note).installCommand, "brew install uv")
    }

    func testToolRowsRaiseWhenStatusesArrive() {
        let rig = Rig()
        defer { rig.dispose() }
        var raised = 0
        let subscription = rig.model.objectWillChange.sink { _ in raised += 1 }
        defer { subscription.cancel() }
        rig.model.refreshTools()
        XCTAssertTrue(rig.h.ui.pumpUntil({ rig.state.toolStatuses.count == 4 }, timeout: 5))
        XCTAssertGreaterThan(raised, 0)
        XCTAssertEqual(rig.h.tools.batches, 1)
    }

    func testDisposeStopsRelayingAppStateToolStatusChanges() {
        let rig = Rig()
        defer { rig.dispose() }
        rig.h.tools.statuses[.npx] = .notFound
        rig.model.refreshTools()
        XCTAssertTrue(rig.h.ui.pumpUntil({ rig.state.toolStatuses.count == 4 }, timeout: 5))

        rig.model.dispose()
        var raised = 0
        let subscription = rig.model.objectWillChange.sink { _ in raised += 1 }
        defer { subscription.cancel() }

        // Publish a change that would flip npx's row on a live (not disposed) model.
        rig.h.tools.statuses[.npx] = .found(path: "/opt/homebrew/bin/npx", version: "1.0.0")
        rig.state.refreshTools([.npx])
        XCTAssertTrue(rig.h.ui.pumpUntil({ rig.state.toolStatuses[.npx] == .found(path: "/opt/homebrew/bin/npx", version: "1.0.0") }, timeout: 5))
        XCTAssertEqual(raised, 0)   // the view was never told to re-read: dispose stopped the relay
    }

    func testToolRowsCarryTheNoteFromToolNote() {
        XCTAssertEqual(ToolRow.make(tool: .npx, status: .notFound),
                       ToolRow(name: "npx", statusText: "Not found", isProblem: true,
                               note: ToolNote.make(tool: .npx, status: .notFound), isShellOnly: false))
        XCTAssertEqual(ToolRow.make(tool: .node, status: nil),
                       ToolRow(name: "node", statusText: "Checking…", isProblem: false, note: nil, isShellOnly: false))
    }

    /// macOS only: under a shell-only row, the sentence, the advice line, then the install line.
    func testToolRowShowsTheShellOnlySentence() throws {
        let status = ToolStatus.foundInShellOnly(path: "/Users/me/.nvm/versions/node/v22/bin/node", version: "22.11.0")
        let row = ToolRow.make(tool: .node, status: status)
        XCTAssertEqual(row.statusText, "Not visible to Claude Desktop")
        XCTAssertTrue(row.isProblem)
        XCTAssertTrue(row.isShellOnly)
        let note = try XCTUnwrap(row.note)
        XCTAssertEqual(note.text, ToolNote.shellOnlyText(.node, path: "/Users/me/.nvm/versions/node/v22/bin/node"))
        XCTAssertEqual(note.advice, ToolNote.shellOnlyAdvice)
        XCTAssertEqual(note.installCommand, "brew install node")
    }
}
