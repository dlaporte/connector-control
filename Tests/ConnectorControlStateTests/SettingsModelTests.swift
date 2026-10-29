import Combine
import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/SettingsModelTests.cs, with
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

        init(commandLineTool: CommandLineTool? = nil) {
            state = h.create()
            model = SettingsModel(state: state, settings: h.settings, autostart: autostart, updater: updater,
                                  commandLineTool: commandLineTool)
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
        XCTAssertTrue(rig.model.loginItemNoteIsWarning, "a failure is amber")
        XCTAssertGreaterThan(raised, 0)
        rig.autostart.failWith = nil
        rig.model.launchAtLogin = true
        XCTAssertNil(rig.model.loginItemNote)
        XCTAssertFalse(rig.model.loginItemNoteIsWarning)
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
        XCTAssertFalse(rig.model.loginItemNoteIsWarning, "an instruction, not a problem")
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
        XCTAssertFalse(rig.model.isPreviewBuild)
        XCTAssertNil(rig.model.updatesCaption)
    }

    /// Only a preview says where its updates come from; a release and a development build say nothing.
    func testOnlyAPreviewSaysWhereItsUpdatesComeFrom() {
        let rig = Rig()
        defer { rig.dispose() }
        rig.updater.versionDisplay = "1.5.0-preview.10 (10500)"
        XCTAssertTrue(rig.model.isPreviewBuild)
        XCTAssertEqual(rig.model.updatesCaption, SettingsModel.previewUpdatesCaption)
        rig.updater.versionDisplay = "1.5.0 (10500)"
        XCTAssertFalse(rig.model.isPreviewBuild)
        XCTAssertNil(rig.model.updatesCaption)
        rig.updater.versionDisplay = "1.5.0"
        XCTAssertFalse(rig.model.isPreviewBuild)
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
        XCTAssertEqual(rig.model.backupKeepCount, 20)
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
    }

    func testStoreLocationFollowsRepointing() {
        let rig = Rig()
        defer { rig.dispose() }
        XCTAssertEqual(rig.model.storeDir.path, rig.h.storeDir.path)
        XCTAssertEqual(rig.model.storeDirName, "Connector Control")
        XCTAssertEqual(rig.model.storeDirParent, SettingsModel.shownParent(rig.h.storeDir.path))
        XCTAssertEqual(rig.model.backupsDirName, "backups")
        XCTAssertEqual(rig.model.backupsDirParent, SettingsModel.shownParent(rig.h.backupsDir.path))
        XCTAssertFalse(rig.model.canUseDefaultStore)
        let synced = rig.h.dir.file("synced")
        rig.model.chooseStoreDir(synced)
        XCTAssertEqual(rig.model.storeDir.path, synced.path)
        XCTAssertEqual(rig.model.storeDirName, "synced")
        XCTAssertTrue(rig.model.canUseDefaultStore)
        XCTAssertEqual(rig.model.backupsDir.path, rig.h.backupsDir.path)
        rig.model.useDefaultStoreDir()
        XCTAssertEqual(rig.model.storeDir.path, rig.h.storeDir.path)
        XCTAssertFalse(rig.model.canUseDefaultStore)
    }

    /// A path row shows the name, and under it the folder it is in; the Mac shortens the home folder to ~.
    func testAPathRowShowsTheNameAndTheFolderItIsIn() {
        XCTAssertEqual(SettingsModel.shownName("/Applications/Claude.app"), "Claude.app")
        XCTAssertEqual(SettingsModel.shownParent("/Applications/Claude.app"), "/Applications")
        XCTAssertEqual(SettingsModel.shownName("/usr/local/bin/"), "bin")
        XCTAssertEqual(SettingsModel.shownParent("/usr/local/bin/connector-control"), "/usr/local/bin")
        XCTAssertEqual(SettingsModel.shownParent("/Applications"), "/")
        let home = NSHomeDirectory()
        XCTAssertEqual(SettingsModel.shownParent(home + "/Library/CloudStorage/OneDrive/Connector Control"),
                       "~/Library/CloudStorage/OneDrive")
    }

    // MARK: Claude

    func testClaudeAppPathShowsTheOverrideOrTheDefault() {
        let rig = Rig()
        defer { rig.dispose() }
        XCTAssertEqual(rig.model.claudeApp.path, "/Applications/Claude.app")
        XCTAssertEqual(rig.model.claudeAppName, "Claude.app")
        XCTAssertEqual(rig.model.claudeAppParent, "/Applications")
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
        XCTAssertTrue(rig.model.toolRows.allSatisfy { $0.statusText == "Checking…" })   // probed off the main thread: nothing yet
        rig.h.drain()
        let rows = rig.model.toolRows
        XCTAssertEqual(rows.map(\.statusText), ["10.9.2", "Found", "Not found", "Not found"])
        XCTAssertEqual(rows.map(\.isProblem), [false, false, true, true])
        XCTAssertNil(rows[0].note)
        XCTAssertNil(rows[1].note)
        XCTAssertEqual(try XCTUnwrap(rows[2].note).linkTitle, "Install uv")
        XCTAssertEqual(try XCTUnwrap(rows[3].note).installCommand, "brew install uv")
        XCTAssertEqual(rig.model.toolFooterLines.map(\.text), ["uv and uvx: Install uv or run brew install uv."],
                       "one line for the family, not one a tool")
    }

    /// The footer has a line for each family with a problem, Node.js before uv as the rows run, the link and the
    /// command set apart; nothing while every tool is found or still being checked.
    func testTheToolsFooterHasALineForEachFamilyWithAProblem() throws {
        XCTAssertEqual(ToolFooterLine.make(statuses: [:]), [])
        XCTAssertEqual(ToolFooterLine.make(statuses: [.npx: .found(path: "/opt/homebrew/bin/npx", version: "10.9.2")]), [])
        let lines = ToolFooterLine.make(statuses: [.uv: .notFound, .npx: .notFound, .node: .notFound])
        XCTAssertEqual(lines.map(\.text), ["node and npx: Install Node.js or run brew install node.",
                                           "uv and uvx: Install uv or run brew install uv."])
        let uv = try XCTUnwrap(lines.last)
        XCTAssertEqual(uv.linkTitle, "Install uv")
        XCTAssertEqual(uv.linkURL, ToolFamily.uv.linkURL)
        XCTAssertEqual(uv.installCommand, "brew install uv")
        XCTAssertEqual(uv.runs, [TextRun("uv and uvx: "), TextRun("Install uv", isSetApart: true), TextRun(" or run "),
                                 TextRun("brew install uv", isSetApart: true), TextRun(".")])
        XCTAssertNil(uv.advice)
    }

    /// macOS only: a family with a tool only the login shell sees adds the advice under its install line.
    func testAShellOnlyToolAddsTheAdviceToItsFamilysLine() {
        let lines = ToolFooterLine.make(statuses: [.node: .foundInShellOnly(path: "/Users/me/.nvm/versions/node/v22/bin/node",
                                                                            version: "22.11.0"),
                                                   .uvx: .notFound])
        XCTAssertEqual(lines.map(\.text), ["node and npx: Install Node.js or run brew install node.",
                                           "uv and uvx: Install uv or run brew install uv."])
        XCTAssertEqual(lines.map(\.advice), [ToolNote.shellOnlyAdvice, nil])
    }

    func testToolRowsRaiseWhenStatusesArrive() {
        let rig = Rig()
        defer { rig.dispose() }
        var raised = 0
        let subscription = rig.model.objectWillChange.sink { _ in raised += 1 }
        defer { subscription.cancel() }
        rig.model.refreshTools()
        rig.h.drain()
        XCTAssertEqual(rig.state.toolStatuses.count, 4)
        XCTAssertGreaterThan(raised, 0)
        XCTAssertEqual(rig.h.tools.batches, 1)
    }

    func testDisposeStopsRelayingAppStateToolStatusChanges() {
        let rig = Rig()
        defer { rig.dispose() }
        rig.h.tools.statuses[.npx] = .notFound
        rig.model.refreshTools()
        rig.h.drain()
        XCTAssertEqual(rig.state.toolStatuses.count, 4)

        rig.model.dispose()
        var raised = 0
        let subscription = rig.model.objectWillChange.sink { _ in raised += 1 }
        defer { subscription.cancel() }

        // Publish a change that would flip npx's row on a live (not disposed) model.
        rig.h.tools.statuses[.npx] = .found(path: "/opt/homebrew/bin/npx", version: "1.0.0")
        rig.state.refreshTools([.npx])
        rig.h.drain()
        XCTAssertEqual(rig.state.toolStatuses[.npx], .found(path: "/opt/homebrew/bin/npx", version: "1.0.0"))
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

    // MARK: Claude integration

    func testTheIntegrationSwitchAndTheConfirmSwitch() throws {
        let rig = Rig(); defer { rig.dispose() }
        XCTAssertFalse(rig.model.claudeIntegrationEnabled)
        XCTAssertTrue(rig.model.canChangeClaudeIntegration)
        XCTAssertNil(rig.model.claudeIntegrationNote)
        XCTAssertFalse(rig.model.canChangeConfirmClaudeChanges, "the second switch works only while the first is on")
        XCTAssertEqual(rig.model.confirmClaudeChangesTitleOpacity, SettingsModel.dimmedOpacity, "its title fades with it")
        XCTAssertNil(rig.model.confirmClaudeChangesCaption, "its caption hides while the integration is off")
        rig.model.claudeIntegrationEnabled = true
        XCTAssertTrue(rig.h.settings.claudeIntegrationEnabled)
        XCTAssertTrue(rig.model.canChangeConfirmClaudeChanges)
        XCTAssertEqual(rig.model.confirmClaudeChangesTitleOpacity, 1)
        XCTAssertEqual(rig.model.confirmClaudeChangesCaption, SettingsModel.confirmClaudeChangesOnCaption)
        XCTAssertFalse(rig.model.confirmClaudeChangesCaptionIsWarning)
        rig.model.confirmClaudeChanges = false
        XCTAssertFalse(rig.h.settings.confirmClaudeChanges)
        XCTAssertEqual(rig.model.confirmClaudeChangesCaption, SettingsModel.confirmClaudeChangesOffCaption)
        XCTAssertTrue(rig.model.confirmClaudeChangesCaptionIsWarning)
        rig.model.claudeIntegrationEnabled = false
        XCTAssertFalse(rig.model.confirmClaudeChanges, "the second switch keeps its value while dimmed")
        XCTAssertEqual(rig.model.version, rig.updater.versionDisplay)
    }

    func testAClashDimsTheSwitchAndNamesIt() throws {
        let h = AppStateHarness()
        try MasterStoreIO.save(.single([ClaudeIntegration.entryName: MCPEntry(config: .object(["command": .string("x")]))]),
                               to: h.masterStoreURL)
        let state = h.create()
        let model = SettingsModel(state: state, settings: h.settings, autostart: FakeAutostart(), updater: FakeUpdater())
        defer { model.dispose(); h.dispose() }
        XCTAssertFalse(model.canChangeClaudeIntegration)
        XCTAssertEqual(model.claudeIntegrationNote, ClaudeIntegration.clashNote)
    }

    /// The Claude group follows a change made anywhere else while the window is open: the command-line
    /// tool turning the integration on, the endpoint failing. Dispose stops it.
    func testTheClaudeGroupFollowsChangesMadeElsewhere() {
        let rig = Rig(); defer { rig.dispose() }
        var raised = 0
        let subscription = rig.model.objectWillChange.sink { _ in raised += 1 }
        defer { subscription.cancel() }
        XCTAssertNil(rig.state.setClaudeIntegrationEnabled(true), "as the command-line tool turns it on")
        XCTAssertGreaterThan(raised, 0)
        XCTAssertTrue(rig.model.claudeIntegrationEnabled)
        XCTAssertNil(rig.model.controlEndpointProblem)
        let before = raised
        rig.state.reportControlEndpointUnavailable()
        XCTAssertGreaterThan(raised, before)
        XCTAssertEqual(rig.model.controlEndpointProblem, ClaudeIntegration.endpointUnavailableText)
        rig.model.dispose()
        let disposed = raised
        rig.state.setClaudeIntegrationEnabled(false)
        XCTAssertEqual(raised, disposed, "dispose stops the relay")
    }

    /// Switching into a collection whose connector holds the name, and out again: while on, the switch
    /// can still go off and names the clash in place of its caption; while off, it is dimmed until the
    /// clash ends.
    func testSwitchingIntoAndOutOfAClashingCollection() throws {
        let h = AppStateHarness()
        try MasterStoreIO.save(MasterStore(activeCollection: "Default", collections: [
            "Default": Collection(mcps: [:]),
            "Old": Collection(mcps: [ClaudeIntegration.entryName: MCPEntry(config: .object(["command": .string("x")]))]),
        ]), to: h.masterStoreURL)
        h.controlToolPath = "/x/connector-control"
        h.settings.claudeIntegrationEnabled = true
        let state = h.create()
        let model = SettingsModel(state: state, settings: h.settings, autostart: FakeAutostart(), updater: FakeUpdater())
        defer { model.dispose(); h.dispose() }
        h.drain()
        XCTAssertEqual(model.claudeIntegrationCaptionText, SettingsModel.claudeIntegrationCaption)
        XCTAssertFalse(model.claudeIntegrationCaptionIsWarning)
        XCTAssertEqual(model.claudeIntegrationTitleOpacity, 1)
        XCTAssertEqual(try h.claudeServers()[ClaudeIntegration.entryName], ClaudeIntegration.entry(toolPath: "/x/connector-control"))
        state.switchCollection(to: "Old")
        h.drain()
        XCTAssertTrue(model.canChangeClaudeIntegration, "on, it can still go off")
        XCTAssertEqual(model.claudeIntegrationCaptionText, ClaudeIntegration.clashNote)
        XCTAssertTrue(model.claudeIntegrationCaptionIsWarning)
        XCTAssertNotEqual(try h.claudeServers()[ClaudeIntegration.entryName], ClaudeIntegration.entry(toolPath: "/x/connector-control"),
                          "the app's own entry gives way to the connector that holds the name")
        model.claudeIntegrationEnabled = false
        XCTAssertFalse(model.canChangeClaudeIntegration, "off, it is dimmed while the clash lasts")
        XCTAssertEqual(model.claudeIntegrationTitleOpacity, SettingsModel.dimmedOpacity, "its title fades; the note does not")
        state.switchCollection(to: "Default")
        h.drain()
        XCTAssertTrue(model.canChangeClaudeIntegration)
        XCTAssertEqual(model.claudeIntegrationCaptionText, SettingsModel.claudeIntegrationCaption)
    }

    /// An app run from where it will not stay cannot turn the integration on or install the tool: the
    /// switch and Install are dimmed and say why. Removing a link made earlier still works.
    func testAnAppRunFromWhereItWillNotStayDimsTheSwitchAndInstall() {
        let h = AppStateHarness()
        h.controlToolPath = "/Volumes/Connector Control/Connector Control.app/Contents/MacOS/connector-control"
        h.controlToolIsTransient = true
        let state = h.create()
        let tool = FakeCommandLineTool()
        let model = SettingsModel(state: state, settings: h.settings, autostart: FakeAutostart(), updater: FakeUpdater(),
                                  commandLineTool: tool)
        defer { model.dispose(); h.dispose() }
        XCTAssertFalse(model.canChangeClaudeIntegration)
        XCTAssertEqual(model.claudeIntegrationCaptionText, ClaudeIntegration.moveToApplicationsError)
        XCTAssertTrue(model.claudeIntegrationCaptionIsWarning)
        XCTAssertFalse(model.canUseCommandLineToolButton)
        XCTAssertEqual(model.commandLineToolMessage, ClaudeIntegration.moveToApplicationsError)
        XCTAssertEqual(model.commandLineToolCaption, [], "no path for scripts that is about to be gone")
        XCTAssertFalse(model.hasCommandLineToolCaption, "the row leaves the empty line out")
        model.commandLineToolAction()
        XCTAssertEqual(tool.installs, 0)
        tool.isInstalled = true
        XCTAssertTrue(model.canUseCommandLineToolButton, "a link made earlier can still go")
        XCTAssertNil(model.commandLineToolMessage)
        XCTAssertEqual(model.commandLineToolCaption, TextRun.runs(CommandLineToolText.runHelpCaption, settingApart: [CommandLineToolText.helpCommand]))
        XCTAssertTrue(model.hasCommandLineToolCaption)
    }

    // MARK: Command-line tool

    func testTheCommandLineToolRowInstallsAndRemoves() {
        let h = AppStateHarness()
        let state = h.create()
        let tool = FakeCommandLineTool()
        let model = SettingsModel(state: state, settings: h.settings, autostart: FakeAutostart(), updater: FakeUpdater(),
                                  commandLineTool: tool)
        defer { model.dispose(); h.dispose() }
        XCTAssertTrue(model.hasCommandLineTool)
        XCTAssertEqual(model.commandLineToolStatus, [TextRun(CommandLineToolText.notInstalledText)])
        XCTAssertEqual(model.commandLineToolCaption, [TextRun("Scripts can always use "), TextRun(tool.toolPath, isSetApart: true)])
        XCTAssertTrue(model.hasCommandLineToolCaption)
        XCTAssertEqual(model.commandLineToolButton, CommandLineToolText.installButton)
        XCTAssertNil(model.commandLineToolMessage)

        model.commandLineToolAction()
        XCTAssertEqual(tool.installs, 1)
        XCTAssertTrue(model.commandLineToolBusy)
        XCTAssertFalse(model.canUseCommandLineToolButton, "one at a time, while the password prompt is up")
        model.commandLineToolAction()
        XCTAssertEqual(tool.installs, 1)
        let reopened = SettingsModel(state: state, settings: h.settings, autostart: FakeAutostart(), updater: FakeUpdater(),
                                     commandLineTool: tool)
        XCTAssertFalse(reopened.canUseCommandLineToolButton, "a Settings window opened while the prompt is up cannot start a second one")
        tool.finish(installed: true)
        XCTAssertFalse(model.commandLineToolBusy)
        XCTAssertTrue(reopened.canUseCommandLineToolButton)
        reopened.dispose()
        XCTAssertEqual(model.commandLineToolStatus, [TextRun("Installed in "), TextRun("/usr/local/bin", isSetApart: true)])
        XCTAssertEqual(model.commandLineToolCaption, [TextRun("Run "), TextRun(CommandLineToolText.helpCommand, isSetApart: true),
                                                      TextRun(" in Terminal.")])
        XCTAssertEqual(model.commandLineToolButton, CommandLineToolText.removeButton)
        tool.installedLocation = nil
        XCTAssertEqual(model.commandLineToolStatus, [TextRun(CommandLineToolText.inPathText)], "in the PATH, as Windows puts it there")

        model.commandLineToolAction()
        XCTAssertEqual(tool.removals, 1)
        tool.finish(installed: true, problem: CommandLineToolText.removeFailedError("Access denied."))
        XCTAssertEqual(model.commandLineToolMessage, CommandLineToolText.removeFailedError("Access denied."))
        XCTAssertEqual(model.commandLineToolButton, CommandLineToolText.removeButton)
        model.commandLineToolAction()
        XCTAssertNil(model.commandLineToolMessage, "a new try clears the last failure")
        tool.finish(installed: false)
        XCTAssertEqual(model.commandLineToolButton, CommandLineToolText.installButton)
    }

    /// The row repaints as an operation starts and as it ends: the seam's own announcements, relayed.
    func testTheCommandLineToolRowRaisesAsAnOperationStartsAndEnds() {
        let tool = FakeCommandLineTool()
        let rig = Rig(commandLineTool: tool); defer { rig.dispose() }
        var raised = 0
        let subscription = rig.model.objectWillChange.sink { _ in raised += 1 }
        defer { subscription.cancel() }
        rig.model.commandLineToolAction()
        XCTAssertGreaterThan(raised, 0, "busy: Install dims")
        let started = raised
        tool.finish(installed: false, problem: CommandLineToolText.installFailedError("Access denied."))
        XCTAssertGreaterThan(raised, started, "done: Install comes back, with the failure under it")
    }

    /// A Settings window closed and opened again while Install or Remove runs hears its end, not only the
    /// window that started it, and shows its failure. Dispose stops it hearing.
    func testAWindowOpenedDuringAnOperationHearsItsEnd() {
        let tool = FakeCommandLineTool()
        let rig = Rig(commandLineTool: tool); defer { rig.dispose() }
        rig.model.commandLineToolAction()
        rig.model.dispose()   // the window that started it closes
        let reopened = SettingsModel(state: rig.state, settings: rig.h.settings, autostart: FakeAutostart(), updater: FakeUpdater(),
                                     commandLineTool: tool)
        defer { reopened.dispose() }
        var raised = 0
        let subscription = reopened.objectWillChange.sink { _ in raised += 1 }
        defer { subscription.cancel() }
        XCTAssertFalse(reopened.canUseCommandLineToolButton)
        tool.finish(installed: false, problem: CommandLineToolText.installFailedError("Access denied."))
        XCTAssertGreaterThan(raised, 0)
        XCTAssertTrue(reopened.canUseCommandLineToolButton)
        XCTAssertEqual(reopened.commandLineToolMessage, CommandLineToolText.installFailedError("Access denied."))
        reopened.dispose()
        let disposed = raised
        tool.install { _ in }
        tool.finish(installed: true)
        XCTAssertEqual(raised, disposed, "dispose stops it hearing the seam")
    }

    /// A cancelled prompt says nothing, even after a failure: the next try clears the failure, and the
    /// cancel puts nothing in its place.
    func testACancelledPromptSaysNothing() {
        let tool = FakeCommandLineTool()
        let rig = Rig(commandLineTool: tool); defer { rig.dispose() }
        rig.model.commandLineToolAction()
        tool.finish(installed: false, problem: CommandLineToolText.installFailedError("Access denied."))
        XCTAssertEqual(rig.model.commandLineToolMessage, CommandLineToolText.installFailedError("Access denied."))
        rig.model.commandLineToolAction()
        tool.finish(installed: false)
        XCTAssertNil(rig.model.commandLineToolMessage)
        XCTAssertEqual(rig.model.commandLineToolButton, CommandLineToolText.installButton)
        XCTAssertTrue(rig.model.canUseCommandLineToolButton)
    }

    func testWithNoToolThereIsNoRow() {
        let rig = Rig(); defer { rig.dispose() }
        XCTAssertFalse(rig.model.hasCommandLineTool)
        XCTAssertEqual(rig.model.commandLineToolStatus, [])
        XCTAssertFalse(rig.model.canUseCommandLineToolButton)
    }
}
