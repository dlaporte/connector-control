import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/AppStateTests.cs, line for line.
@MainActor
final class AppStateTests: XCTestCase {
    private let fixture = ["aws-mcp", "scoutbook", "service-now"]

    func testFirstLoadImportsClaudeServersEnabled() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        XCTAssertEqual(state.sortedNames, fixture)
        XCTAssertTrue(state.store.mcps.values.allSatisfy(\.enabled))
        XCTAssertEqual(state.appliedServers.keys.sorted(), fixture)
        XCTAssertTrue(h.notifier.sent.isEmpty)
        XCTAssertNil(state.lastError)
        XCTAssertFalse(state.isDirty)
        XCTAssertFalse(state.needsClaudeRestart)
        XCTAssertFalse(state.applyRetryNeeded)
        XCTAssertEqual(state.headerSubtitle, "3 of 3 enabled")
        XCTAssertEqual(state.profileNames, ["Default"])
        XCTAssertEqual(state.activeProfile, "Default")
        XCTAssertTrue(FileManager.default.fileExists(atPath: h.masterStoreURL.path))
        XCTAssertEqual(h.settings.sweepVersion, PermissionsSweep.currentVersion)
    }

    func testHeaderSubtitleForAnEmptyStore() {
        let h = AppStateHarness(seedClaudeConfig: false)
        defer { h.dispose() }
        let state = h.create()
        XCTAssertEqual(state.headerSubtitle, "No connectors configured")
        XCTAssertTrue(state.sortedNames.isEmpty)
    }

    func testSetEnabledPersistsAndAppliesImmediately() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.setEnabled("aws-mcp", false)
        XCTAssertEqual(try h.claudeServers().keys.sorted(), ["scoutbook", "service-now"])
        XCTAssertEqual(try h.storeOnDisk().mcps["aws-mcp"]?.enabled, false)
        XCTAssertEqual(h.settings.lastApplyDate, h.now)
        XCTAssertEqual(state.headerSubtitle, "2 of 3 enabled")
        XCTAssertFalse(state.isDirty)
        XCTAssertEqual(try state.service.backups.backups(series: "claude_desktop_config").count, 1)
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: h.backupsDir.appendingPathComponent("claude_desktop_config.original.json").path))
        XCTAssertTrue(h.notifier.sent.isEmpty)
    }

    func testRestartRequiredFollowsClaudeLaunchTime() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        h.claude.isRunning = true
        h.claude.launchDate = h.now.addingTimeInterval(-3600)
        state.setEnabled("aws-mcp", false)
        XCTAssertTrue(state.needsClaudeRestart)

        h.claude.launchDate = h.now.addingTimeInterval(60)   // Claude relaunched after our write
        state.refreshRestartState()
        XCTAssertFalse(state.needsClaudeRestart)

        h.claude.launchDate = h.now.addingTimeInterval(-3600)
        h.claude.isRunning = false                           // not running ⇒ never "required"
        state.refreshRestartState()
        XCTAssertFalse(state.needsClaudeRestart)
    }

    func testRestartRequiredAtLaunchFromAPersistedApplyDate() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.settings.lastApplyDate = h.now
        h.claude.isRunning = true
        h.claude.launchDate = h.now.addingTimeInterval(-3600)
        let state = h.create()
        XCTAssertTrue(state.needsClaudeRestart)
    }

    func testUpsertValidatesNames() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let entry = MCPEntry(config: AppStateHarness.remote("https://new.example/mcp"))
        XCTAssertEqual(state.upsert(name: "", entry: entry, renamedFrom: nil), "Name must not be empty.")
        XCTAssertEqual(state.upsert(name: " \t ", entry: entry, renamedFrom: nil), "Name must not be empty.")
        XCTAssertEqual(state.upsert(name: "scoutbook", entry: entry, renamedFrom: nil),
                       "A connector named “scoutbook” already exists.")
        XCTAssertNil(state.upsert(name: "scoutbook", entry: entry, renamedFrom: "scoutbook"))   // saving under its own name replaces
        XCTAssertEqual(state.store.mcps["scoutbook"]?.config, entry.config)
        XCTAssertNil(state.upsert(name: " new ", entry: entry, renamedFrom: nil))              // spaces trimmed
        XCTAssertNotNil(state.store.mcps["new"])
    }

    func testUpsertPersistsButOnlyInteractiveApplyWritesClaudesConfig() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        XCTAssertNil(state.upsert(name: "new", entry: MCPEntry(config: AppStateHarness.remote("https://new.example/mcp")), renamedFrom: nil))
        XCTAssertNotNil(try h.storeOnDisk().mcps["new"])
        XCTAssertNil(try h.claudeServers()["new"])
        XCTAssertTrue(state.isDirty)
        state.applyInteractively()
        XCTAssertNotNil(try h.claudeServers()["new"])
        XCTAssertFalse(state.isDirty)
    }

    func testUpsertRenameRemovesTheOldKey() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let entry = try XCTUnwrap(state.store.mcps["scoutbook"])
        XCTAssertNil(state.upsert(name: "scoutbook2", entry: entry, renamedFrom: "scoutbook"))
        XCTAssertNil(state.store.mcps["scoutbook"])
        XCTAssertNotNil(state.store.mcps["scoutbook2"])
        XCTAssertNil(try h.storeOnDisk().mcps["scoutbook"])
    }

    func testRemovePersistsButDoesNotApply() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.remove(name: "aws-mcp")
        XCTAssertNil(try h.storeOnDisk().mcps["aws-mcp"])
        XCTAssertNotNil(try h.claudeServers()["aws-mcp"])
        XCTAssertTrue(state.isDirty)
        state.applyInteractively()
        XCTAssertNil(try h.claudeServers()["aws-mcp"])
    }

    func testApplyInteractivelyIsANoOpWhenCleanButApplyAlwaysWrites() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.applyInteractively()
        XCTAssertNil(h.settings.lastApplyDate)
        state.apply()
        XCTAssertEqual(h.settings.lastApplyDate, h.now)
    }

    func testPendingRemovalIsRegeneratedQuietlyOnReload() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.remove(name: "aws-mcp")
        state.reload()
        XCTAssertNil(try h.claudeServers()["aws-mcp"])     // regenerated from the store
        XCTAssertNil(state.store.mcps["aws-mcp"])          // not resurrected: it matched the baseline
        XCTAssertTrue(h.notifier.sent.isEmpty)             // our own change is not "external"
        XCTAssertFalse(state.isDirty)
    }

    func testExternalEditOfClaudesConfigIsRegeneratedAndAnnounced() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let original = try XCTUnwrap(state.store.mcps["scoutbook"]).config
        try h.writeClaudeServers([
            ("scoutbook", AppStateHarness.remote("https://changed.example/mcp")),
            ("service-now", try XCTUnwrap(state.store.mcps["service-now"]).config),
            ("newcomer", AppStateHarness.remote("https://newcomer.example/mcp")),
        ])
        state.reload()
        XCTAssertEqual(state.sortedNames, ["aws-mcp", "newcomer", "scoutbook", "service-now"])
        XCTAssertEqual(state.store.mcps["newcomer"]?.enabled, true)
        XCTAssertEqual(state.store.mcps["scoutbook"]?.config, original)   // known entries are never modified by the file
        XCTAssertEqual(try h.claudeServers()["scoutbook"], original)      // and the file is regenerated from the store
        XCTAssertNotNil(try h.claudeServers()["aws-mcp"])
        XCTAssertEqual(h.notifier.sent, [
            FakeNotifier.Sent(title: Notifications.title, body: AppState.claudeConfigRegeneratedBody, category: nil)])
        XCTAssertEqual(h.settings.lastApplyDate, h.now)
        XCTAssertFalse(state.isDirty)
    }

    func testExternalRemovalIsRegeneratedAndAnnounced() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        try h.writeClaudeServers([("scoutbook", try XCTUnwrap(state.store.mcps["scoutbook"]).config)])
        state.reload()
        XCTAssertEqual(try h.claudeServers().keys.sorted(), fixture)
        XCTAssertEqual(h.notifier.sent.map(\.body), [AppState.claudeConfigRegeneratedBody])
    }

    func testExternalEditThatMatchesTheStoreOnlyAnnouncesTheChange() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.remove(name: "aws-mcp")   // pending removal: store and file now differ
        try h.writeClaudeServers([      // someone writes exactly what the store would render
            ("scoutbook", try XCTUnwrap(state.store.mcps["scoutbook"]).config),
            ("service-now", try XCTUnwrap(state.store.mcps["service-now"]).config),
        ])
        state.reload()
        XCTAssertEqual(h.notifier.sent.map(\.body), [AppState.claudeConfigChangedBody])
        XCTAssertNil(h.settings.lastApplyDate)   // nothing to regenerate
        XCTAssertFalse(state.isDirty)
    }

    func testStoreEditedOutsideWithoutRegenerationIsAnnounced() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.setEnabled("aws-mcp", false)
        h.notifier.clearSent()
        var edited = try h.storeOnDisk()
        edited.mcps["aws-mcp"]?.config = AppStateHarness.remote("https://synced.example/mcp")
        try MasterStoreIO.save(edited, to: h.masterStoreURL)   // a sync tool wrote the store; the disabled entry needs no regeneration
        state.reload()
        XCTAssertEqual(h.notifier.sent.map(\.body), [AppState.storeChangedBody])
        XCTAssertEqual(state.store.mcps["aws-mcp"]?.config, AppStateHarness.remote("https://synced.example/mcp"))
    }

    func testMalformedClaudeConfigReportsTheNoteAndBlocksApply() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        try Data("{oops".utf8).write(to: h.claudeConfigURL)
        state.reload()
        XCTAssertEqual(state.lastError,
                       "Claude's config file is not valid JSON. Your MCP list is safe; use Backups ▸ Restore… to repair the file.")
        XCTAssertEqual(state.sortedNames, fixture)
        XCTAssertTrue(h.notifier.sent.isEmpty)
        XCTAssertFalse(state.applyRetryNeeded)

        state.setEnabled("aws-mcp", false)
        XCTAssertTrue(state.applyRetryNeeded)
        let message = try XCTUnwrap(state.lastError)
        XCTAssertTrue(message.hasPrefix("Claude's config file is not valid JSON ("), message)
        XCTAssertTrue(message.hasSuffix("). Nothing was written. Use Backups ▸ Restore… to recover it."), message)
        XCTAssertEqual(try h.storeOnDisk().mcps["aws-mcp"]?.enabled, false)   // the store change persisted even though the apply failed
        XCTAssertEqual(try String(contentsOf: h.claudeConfigURL, encoding: .utf8), "{oops")

        try Data(Fixtures.realisticClaudeConfig.utf8).write(to: h.claudeConfigURL)   // the user repaired the file
        state.reload()
        XCTAssertFalse(state.applyRetryNeeded)
        XCTAssertNil(state.lastError)
        XCTAssertEqual(try h.claudeServers().keys.sorted(), ["scoutbook", "service-now"])
        XCTAssertTrue(h.notifier.sent.isEmpty)   // a retry that succeeds is the user's own change, not an external one
    }

    func testRegenerationFailureIsAnnouncedOnceOnTheTransitionOnly() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        try h.writeClaudeServers([("scoutbook", try XCTUnwrap(state.store.mcps["scoutbook"]).config)])   // external edit needing regeneration
        let block = try WriteBlock(h.claudeConfigURL)
        defer { block.dispose() }
        // Asserted, never branched on: if the block does not bind the rest of this
        // test proves nothing, and CI fails skips, so it has to fail here instead.
        XCTAssertTrue(block.isEffective)
        state.reload()
        XCTAssertTrue(state.applyRetryNeeded)
        XCTAssertEqual(h.notifier.sent.map(\.body), [AppState.regenerationFailedBody])
        state.reload()   // every popover open retries; the failure must not be re-announced
        XCTAssertTrue(state.applyRetryNeeded)
        XCTAssertEqual(h.notifier.sent.count, 1)

        block.dispose()
        state.reload()
        XCTAssertFalse(state.applyRetryNeeded)
        XCTAssertEqual(try h.claudeServers().keys.sorted(), fixture)
        XCTAssertEqual(h.notifier.sent.count, 1)   // the eventual success is quiet
    }

    func testCorruptStoreIsRebuiltWithANote() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        try Data("garbage".utf8).write(to: h.masterStoreURL)
        state.reload()
        let message = try XCTUnwrap(state.lastError)
        XCTAssertTrue(message.hasPrefix("The MCP list file was unreadable; it was preserved as mcps.corrupt."), message)
        XCTAssertEqual(state.sortedNames, fixture)
        XCTAssertTrue(h.notifier.sent.isEmpty)
    }

    func testCorruptStoreAndMalformedClaudeConfigSurfaceBothNotes() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        try Data("garbage".utf8).write(to: h.masterStoreURL)
        try Data("{oops".utf8).write(to: h.claudeConfigURL)
        state.reload()
        // Both sentences, in reconcile order; the second is the one that says what to do.
        let message = try XCTUnwrap(state.lastError)
        XCTAssertTrue(message.hasPrefix("The MCP list file was unreadable; it was preserved as mcps.corrupt."), message)
        XCTAssertTrue(message.hasSuffix(" Claude's config file is not valid JSON. Your MCP list is safe; use Backups ▸ Restore… to repair the file."), message)
    }

    func testReloadOverwritesLastError() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.lastError = "stale"
        state.reload()
        XCTAssertNil(state.lastError)
    }

    func testFriendlyMapsMalformedConfigAndPassesOtherMessagesThrough() {
        XCTAssertEqual(
            AppState.friendly(ClaudeConfigError.malformed("top level is not a JSON object")),
            "Claude's config file is not valid JSON (top level is not a JSON object). Nothing was written. Use Backups ▸ Restore… to recover it.")
        XCTAssertEqual(
            AppState.friendly(NSError(domain: "test", code: 1, userInfo: [NSLocalizedDescriptionKey: "disk full"])),
            "disk full")
    }

    func testMakeServiceHonorsSettingsAndKeepsBackupsMachineLocal() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.settings.masterStoreDir = h.dir.file("synced").path
        h.settings.backupKeepCount = 7
        let service = AppState.makeService(settings: h.settings, paths: h.context)
        XCTAssertEqual(service.paths.storeDirURL.path, h.dir.file("synced").path)
        XCTAssertEqual(service.paths.backupsDirURL.path, h.backupsDir.path)
        XCTAssertEqual(service.paths.stagingDirURL.path, h.stagingDir.path, "temp files are never born in the synced folder")
        XCTAssertEqual(service.paths.claudeConfigURL.path, h.claudeConfigURL.path)
        XCTAssertEqual(service.backups.keepCount, 7)
    }

    /// A stored empty string (e.g. a setting cleared by hand) counts as absent,
    /// same as nil — the default location, not a literal empty path.
    func testAnEmptyStoredStoreDirIsTheDefault() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.settings.masterStoreDir = ""
        let state = h.create()
        XCTAssertEqual(state.service.paths.storeDirURL.path, h.storeDir.path)
    }

    func testRefreshToolsProbesOffTheUiThreadAndPublishesThroughTheHost() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.tools.statuses[.uvx] = .notFound
        let state = h.create()
        XCTAssertTrue(state.toolStatuses.isEmpty)   // nothing is probed until the editor or Settings asks
        var raised = 0
        let subscription = state.objectWillChange.sink { _ in raised += 1 }
        defer { subscription.cancel() }
        state.refreshTools()
        // The results are posted to the host; nothing is published until the queue is pumped.
        XCTAssertTrue(h.ui.pumpUntil({ state.toolStatuses.count == 4 }, timeout: 5))
        XCTAssertEqual(state.toolStatuses[.uvx], .notFound)
        XCTAssertEqual(state.toolStatuses[.npx], .found(path: "/fake/bin/npx", version: "1.0.0"))
        XCTAssertGreaterThan(raised, 0)
        XCTAssertEqual(h.tools.probed, Tool.allCases)
        XCTAssertEqual(h.tools.batches, 1)
    }

    func testRefreshToolsDoesNotProbeAToolAlreadyInFlight() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.refreshTools([.npx])
        state.refreshTools([.npx, .node])   // npx joins the flight already in the air; node starts one
        XCTAssertTrue(h.ui.pumpUntil({ state.toolStatuses.count == 2 }, timeout: 5))
        XCTAssertEqual(Set(h.tools.probed), [.npx, .node])
        XCTAssertEqual(h.tools.probed.count, 2)
        XCTAssertEqual(h.tools.batches, 2)
        state.refreshTools([])   // nothing wanted: no batch
        XCTAssertEqual(h.tools.batches, 2)
        // Once published, the same tool can be probed again (the editor asks when the command changes).
        state.refreshTools([.npx])
        XCTAssertTrue(h.ui.pumpUntil({ h.tools.probed.count == 3 }, timeout: 5))
    }

    /// Every string AppState shows, byte for byte as the views showed them.
    func testStringsMatchTheCatalog() {
        XCTAssertEqual(Notifications.title, "Connector Control")
        XCTAssertEqual(Notifications.restartCategory, "restartPending")
        XCTAssertEqual(Notifications.restartAction, "restartClaude")
        XCTAssertEqual(Notifications.restartToastButton, "Restart Claude")
        XCTAssertEqual(AppState.noConnectorsSubtitle, "No connectors configured")
        XCTAssertEqual(AppState.enabledSubtitle(enabled: 2, total: 3), "2 of 3 enabled")
        XCTAssertEqual(AppState.claudeConfigRegeneratedBody,
                       "Claude's config was changed outside Connector Control — regenerated from your connector list. Restart Claude to pick it up.")
        XCTAssertEqual(
            AppState.connectorListChangedBody(ServerDelta(added: ["evil"], removed: ["fs"]), restartRequired: true),
            "The connector list changed outside Connector Control — Claude's config now adds evil; removes fs. Restart Claude to pick it up.")
        XCTAssertEqual(
            AppState.connectorListChangedBody(ServerDelta(changed: ["aws-mcp"]), restartRequired: false),
            "The connector list changed outside Connector Control — Claude's config now changes aws-mcp. Claude will use it the next time it starts.")
        XCTAssertEqual(
            AppState.connectorListChangedBody(ServerDelta(), restartRequired: false),
            "The connector list changed outside Connector Control — Claude's config was regenerated. Claude will use it the next time it starts.")
        XCTAssertEqual(AppState.regenerationFailedBody,
                       "The connector configuration changed, but Claude's config could not be updated — open Connector Control to retry.")
        XCTAssertEqual(AppState.claudeConfigChangedBody, "Claude's config changed outside Connector Control.")
        XCTAssertEqual(AppState.storeChangedBody,
                       "The connector list changed outside Connector Control — review it before your next change is applied.")
        XCTAssertEqual(AppState.quitMessage, "Quit Connector Control?")
        XCTAssertEqual(AppState.quitButton, "Quit")
        XCTAssertEqual(AppState.restartMessage, "Restart Claude Desktop now?")
        XCTAssertEqual(AppState.restartInformative, "Any in-progress Claude conversation will be interrupted.")
        XCTAssertEqual(AppState.restartButton, "Restart")
        XCTAssertEqual(AppState.newProfileTitle, "New Profile")
        XCTAssertEqual(AppState.renameProfileTitle, "Rename Profile")
        XCTAssertEqual(AppState.deleteProfileMessage("Work"), "Delete Profile “Work”?")
        XCTAssertEqual(AppState.deleteProfileInformative, "Its connector list is removed; backups keep prior states.")
        XCTAssertEqual(AppState.deleteButton, "Delete")
        XCTAssertEqual(AppState.nameEmptyError, "Name must not be empty.")
        XCTAssertEqual(AppState.duplicateNameError("x"), "A connector named “x” already exists.")
        XCTAssertEqual(AppState.malformedConfigMessage(detail: "d"),
                       "Claude's config file is not valid JSON (d). Nothing was written. Use Backups ▸ Restore… to recover it.")
        XCTAssertEqual(AppState.defaultClaudeAppPath, "/Applications/Claude.app")
        XCTAssertEqual(AppState.restartRecheckDelay, 3)
    }
}
