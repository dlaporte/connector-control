import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/AppStateWatcherTests.cs
/// against the real DispatchSource watchers. The 300 ms sleeps keep the
/// external write's mtime distinct from the file the harness just wrote.
@MainActor
final class AppStateWatcherTests: XCTestCase {
    private let wait: TimeInterval = 8
    private let settle: TimeInterval = 1.5
    private let fixture = ["aws-mcp", "scoutbook", "service-now"]

    func testClaudeConfigWatcherRegeneratesAnExternalEdit() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        Thread.sleep(forTimeInterval: 0.3)
        try h.writeClaudeServers([("scoutbook", try XCTUnwrap(state.store.mcps["scoutbook"]).config)])
        XCTAssertTrue(h.ui.pumpUntil({ h.notifier.sent.count == 1 }, timeout: wait))
        XCTAssertEqual(h.notifier.sent[0].body, AppState.claudeConfigRegeneratedBody)
        XCTAssertEqual(try h.claudeServers().keys.sorted(), fixture)
        _ = h.ui.pumpUntil({ false }, timeout: settle)   // the regenerating write echoes through the watcher: it must stay quiet
        XCTAssertEqual(h.notifier.sent.count, 1)
    }

    func testExternalEditIsSilentWhenNotifyExternalChangesIsDisabledButStillReloads() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.settings.notifyExternalChanges = false
        let state = h.create()
        Thread.sleep(forTimeInterval: 0.3)
        try h.writeClaudeServers([("scoutbook", try XCTUnwrap(state.store.mcps["scoutbook"]).config)])
        XCTAssertTrue(h.ui.pumpUntil({ (try? h.claudeServers().keys.sorted()) == self.fixture }, timeout: wait))
        _ = h.ui.pumpUntil({ false }, timeout: settle)   // give the regenerating write's own echo a chance to fire too
        XCTAssertTrue(h.notifier.sent.isEmpty)
    }

    func testStoreWatcherAdoptsAnExternalStoreAndAnnouncesTheRestart() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.claude.isRunning = true
        h.claude.launchDate = h.now.addingTimeInterval(-3600)
        let state = h.create()
        Thread.sleep(forTimeInterval: 0.3)
        var synced = try h.storeOnDisk()
        synced.mcps["scoutbook"]?.enabled = false
        try MasterStoreIO.save(synced, to: h.masterStoreURL)   // another machine's list arrives via sync
        XCTAssertTrue(h.ui.pumpUntil({ h.notifier.sent.count == 1 }, timeout: wait))
        XCTAssertEqual(h.notifier.sent[0], FakeNotifier.Sent(
            title: Notifications.title,
            body: AppState.connectorListChangedBody(ServerDelta(removed: ["scoutbook"]), restartRequired: true),
            category: Notifications.restartCategory))
        XCTAssertEqual(state.store.mcps["scoutbook"]?.enabled, false)
        XCTAssertEqual(try h.claudeServers().keys.sorted(), ["aws-mcp", "service-now"])
        XCTAssertTrue(state.needsClaudeRestart)
    }

    /// A synced list can add a command Claude will run. With Claude not running there is no
    /// restart to offer, but the adoption still has to be announced — before, every branch of
    /// the notification chain missed this case and the new server simply started next launch.
    func testStoreWatcherAnnouncesAnAdoptedAdditionEvenWhenClaudeIsNotRunning() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.claude.isRunning = false
        let state = h.create()
        Thread.sleep(forTimeInterval: 0.3)
        var synced = try h.storeOnDisk()
        synced.mcps["evil"] = MCPEntry(config: .object(["command": .string("curl"), "args": .array([.string("https://x.example/run")])]))
        try MasterStoreIO.save(synced, to: h.masterStoreURL)   // another machine's list arrives via sync
        XCTAssertTrue(h.ui.pumpUntil({ h.notifier.sent.count == 1 }, timeout: wait))
        XCTAssertEqual(h.notifier.sent[0], FakeNotifier.Sent(
            title: Notifications.title,
            body: AppState.connectorListChangedBody(ServerDelta(added: ["evil"]), restartRequired: false),
            category: nil))
        XCTAssertEqual(try h.claudeServers().keys.sorted(), ["aws-mcp", "evil", "scoutbook", "service-now"])
        XCTAssertFalse(state.needsClaudeRestart)
    }

    func testStoreWatcherIgnoresOurOwnWriteEcho() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        Thread.sleep(forTimeInterval: 0.3)
        state.setEnabled("aws-mcp", false)
        _ = h.ui.pumpUntil({ false }, timeout: settle)
        XCTAssertTrue(h.notifier.sent.isEmpty)
        XCTAssertEqual(state.store.mcps["aws-mcp"]?.enabled, false)
    }

    func testStoreWatcherIgnoresAnUndecodablePartialWrite() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        Thread.sleep(forTimeInterval: 0.3)
        try Data("{\"version\": 2, \"acti".utf8).write(to: h.masterStoreURL)   // a sync tool mid-write
        _ = h.ui.pumpUntil({ false }, timeout: settle)
        XCTAssertEqual(state.sortedNames, fixture)
        XCTAssertEqual(try String(contentsOf: h.masterStoreURL, encoding: .utf8), "{\"version\": 2, \"acti")   // not moved aside: no reload happened
        XCTAssertTrue(h.notifier.sent.isEmpty)
    }

    func testDeletedStoreFileIsRePersistedFromMemory() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        Thread.sleep(forTimeInterval: 0.3)
        try FileManager.default.removeItem(at: h.masterStoreURL)
        XCTAssertTrue(h.ui.pumpUntil({ FileManager.default.fileExists(atPath: h.masterStoreURL.path) }, timeout: wait))
        XCTAssertEqual(try h.storeOnDisk(), state.store)
        XCTAssertTrue(h.notifier.sent.isEmpty)
    }

    /// The C# version repoints the Claude config into a directory that does not
    /// exist yet; the Mac has no such repoint, so the missing directory is the
    /// Claude folder itself at launch (spec §6.3: both watchers live after every reload).
    func testReloadArmsOnlyTheWatcherThatCouldNotArmYet() throws {
        let h = AppStateHarness(seedClaudeConfig: false, createClaudeDirectory: false)
        defer { h.dispose() }
        let state = h.create()
        // No Claude folder and no store written (an empty reconcile saves nothing): neither watcher could arm.
        XCTAssertFalse(state.watchersArmed)
        XCTAssertNil(state.upsert(name: "only", entry: MCPEntry(config: AppStateHarness.remote("https://only.example/mcp")), renamedFrom: nil))
        state.applyInteractively()   // creates the Claude folder and the file
        XCTAssertTrue(FileManager.default.fileExists(atPath: h.claudeConfigURL.path))
        XCTAssertFalse(state.watchersArmed, "an apply is not a reload: nothing re-arms yet")
        state.reload()
        XCTAssertTrue(state.watchersArmed, "the re-arm at the end of reload caught up")
        // The "only" half: a further reload leaves the now-armed watchers alone
        // instead of tearing them down and re-baselining their mtimes.
        let armed = state.watcherIdentities
        state.reload()
        XCTAssertTrue(state.watchersArmed)
        XCTAssertEqual(state.watcherIdentities.claude, armed.claude, "an armed watcher is not replaced by reload")
        XCTAssertEqual(state.watcherIdentities.store, armed.store, "an armed watcher is not replaced by reload")

        Thread.sleep(forTimeInterval: 0.3)
        try h.writeClaudeServers([("only", AppStateHarness.remote("https://moved.example/mcp"))])
        XCTAssertTrue(h.ui.pumpUntil({ h.notifier.sent.count == 1 }, timeout: wait))   // the location really is watched
        XCTAssertEqual(h.notifier.sent[0].body, AppState.claudeConfigRegeneratedBody)
    }

    /// The store watcher's own directory disappearing (not just its file) is
    /// self-healing: the deletion's own reload re-persists the store from
    /// memory, recreating the directory the next reArm needs.
    func testDeletingTheStoreDirectoryReArmsOnTheNextReload() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        XCTAssertTrue(state.watchersArmed)
        try FileManager.default.removeItem(at: h.storeDir)
        _ = h.ui.pumpUntil({ false }, timeout: settle)   // let the deletion's own detection run
        state.reload()
        XCTAssertTrue(state.watchersArmed)
    }

    func testRepointStoreSeedsAnEmptyLocationAndReArmsTheWatcher() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let synced = h.dir.file("synced")
        state.repointStore(to: synced)
        XCTAssertEqual(h.settings.masterStoreDir, synced.path)
        XCTAssertEqual(state.service.paths.storeDirURL.path, synced.path)
        XCTAssertEqual(state.service.paths.backupsDirURL.path, h.backupsDir.path)   // backups never follow the store
        XCTAssertEqual(MasterStoreIO.read(from: synced.appendingPathComponent("mcps.json")), state.store)
        XCTAssertTrue(FileManager.default.fileExists(atPath: h.masterStoreURL.path))   // the previous file is never deleted
        XCTAssertTrue(h.notifier.sent.isEmpty)

        Thread.sleep(forTimeInterval: 0.3)
        var edited = state.store
        edited.mcps["aws-mcp"]?.enabled = false
        try MasterStoreIO.save(edited, to: synced.appendingPathComponent("mcps.json"))
        XCTAssertTrue(h.ui.pumpUntil({ state.store.mcps["aws-mcp"]?.enabled == false }, timeout: wait))   // the new location is watched
    }

    func testRepointStoreAdoptsAnExistingStoreQuietly() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let synced = h.dir.file("synced")
        try FileManager.default.createDirectory(at: synced, withIntermediateDirectories: true)
        var theirs = state.store
        theirs.mcps["aws-mcp"]?.enabled = false
        theirs.mcps["synced-only"] = MCPEntry(config: AppStateHarness.remote("https://synced.example/mcp"))
        try MasterStoreIO.save(theirs, to: synced.appendingPathComponent("mcps.json"))

        state.repointStore(to: synced)
        XCTAssertEqual(state.sortedNames, ["aws-mcp", "scoutbook", "service-now", "synced-only"])
        XCTAssertEqual(state.store.mcps["aws-mcp"]?.enabled, false)
        XCTAssertEqual(try h.claudeServers().keys.sorted(), ["scoutbook", "service-now", "synced-only"])   // regenerated
        XCTAssertTrue(h.notifier.sent.isEmpty)   // quiet adoption: the user is watching
    }

    /// The new location exists but cannot be written into (its directory is
    /// write-blocked): the seed write throws, so the repoint must not adopt a
    /// service pointed at a store that was never actually seeded.
    func testARepointWhoseSeedCannotBeWrittenKeepsTheOldStore() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let target = h.dir.file("blocked")
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
        let block = try WriteBlock(target.appendingPathComponent("mcps.json"))
        defer { block.dispose() }
        XCTAssertTrue(block.isEffective)
        let previousStoreDir = state.service.paths.storeDirURL
        let beforeClaudeConfig = try Data(contentsOf: h.claudeConfigURL)

        state.repointStore(to: target)

        XCTAssertEqual(state.service.paths.storeDirURL, previousStoreDir)
        XCTAssertNotNil(state.lastError)
        XCTAssertEqual(try Data(contentsOf: h.claudeConfigURL), beforeClaudeConfig)
        XCTAssertNil(h.settings.masterStoreDir, "restored to its previous value")
    }

    func testRepointStoreBackToTheDefault() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.repointStore(to: h.dir.file("synced"))
        state.repointStore(to: nil)
        XCTAssertNil(h.settings.masterStoreDir)
        XCTAssertEqual(state.service.paths.storeDirURL.path, h.storeDir.path)
        XCTAssertEqual(state.sortedNames, fixture)
    }

    func testRefreshServiceSettingsAppliesTheKeepCountWithoutReloading() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        h.settings.backupKeepCount = 7
        state.refreshServiceSettings()
        XCTAssertEqual(state.service.backups.keepCount, 7)
        XCTAssertNil(h.settings.lastApplyDate)
        XCTAssertEqual(state.sortedNames, fixture)
    }

    func testRestoreClaudeConfigIsQuietAndSyncsTheBaseline() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.setEnabled("aws-mcp", false)
        h.notifier.clearSent()
        h.now = h.now.addingTimeInterval(300)
        let backup = try XCTUnwrap(state.service.backups.backups(series: "claude_desktop_config").first)   // the three-server file from before the toggle

        try state.restoreClaudeConfig(from: backup)
        XCTAssertEqual(try h.claudeServers().keys.sorted(), fixture)
        XCTAssertEqual(state.store.mcps["aws-mcp"]?.enabled, true)   // adopted into the store
        XCTAssertEqual(state.appliedServers.keys.sorted(), fixture)
        XCTAssertEqual(h.settings.lastApplyDate, h.now)
        XCTAssertFalse(state.isDirty)
        XCTAssertTrue(h.notifier.sent.isEmpty)
    }

    func testRestoreFailurePropagatesWithoutTouchingTheFile() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let bad = h.dir.file("bad.json")
        try Data("{not json".utf8).write(to: bad)
        let before = try Data(contentsOf: h.claudeConfigURL)
        XCTAssertThrowsError(try state.restoreClaudeConfig(from: bad)) { error in
            XCTAssertEqual(error as? ClaudeConfigError, .malformed("backup bad.json is not a valid config file"))
        }
        XCTAssertEqual(try Data(contentsOf: h.claudeConfigURL), before)
    }

    func testDisposeStopsTheWatchers() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        Thread.sleep(forTimeInterval: 0.3)
        state.dispose()
        try h.writeClaudeServers([("scoutbook", try XCTUnwrap(state.store.mcps["scoutbook"]).config)])
        XCTAssertFalse(h.ui.pumpUntil({ !h.notifier.sent.isEmpty }, timeout: settle))
        XCTAssertEqual(try h.claudeServers().keys.sorted(), ["scoutbook"])   // nobody regenerated it
    }
}
