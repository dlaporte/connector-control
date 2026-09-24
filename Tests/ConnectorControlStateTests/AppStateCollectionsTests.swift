import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/AppStateCollectionsTests.cs.
/// The sidecar, the machine-local cache and the named collection actions, against the real on-disk
/// layout the harness builds.
///
/// Where a test needs a synced or published collection without subscribing or publishing, it is
/// set up the way those flows leave it — the two files on disk — and read back through a real
/// reload.
@MainActor
final class AppStateCollectionsTests: XCTestCase {
    private let emptyCache = CollectionsLocalCache(synced: [:], published: [:])

    private func synced(fileName: String, relativeToStore: String? = nil,
                        needs: [String: [String: CollectionsFile.Need]] = [:]) -> CollectionsFile.Entry {
        CollectionsFile.Entry(kind: .synced, fileName: fileName, relativeToStore: relativeToStore, needs: needs)
    }

    private func published(slug: String) -> CollectionsFile.Entry {
        CollectionsFile.Entry(kind: .local, publish: CollectionsFile.PublishRecord(slug: slug, origin: "origin", intent: .none))
    }

    private func bound(_ path: String?) -> CollectionsLocalCache.SyncedBinding {
        CollectionsLocalCache.SyncedBinding(path: path, lastHash: nil, excluded: [:])
    }

    /// Writes both files where the app reads them. With a state, reloads so it picks them up.
    private func seed(_ h: AppStateHarness, _ state: AppState? = nil,
                      file: CollectionsFile, cache: CollectionsLocalCache? = nil) throws {
        try file.save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        let cacheURL = state?.service.paths.collectionsCacheURL
            ?? h.storeDir.appendingPathComponent(CollectionsLocalCache.fileName)
        try (cache ?? emptyCache).save(to: cacheURL, staging: nil)
        state?.reload()
    }

    /// A store on disk with the named collections, so a sidecar entry has something to annotate.
    private func seedStore(_ h: AppStateHarness, collections: [String], active: String = "Default") throws {
        var store = MasterStore.empty
        for name in collections { XCTAssertNil(store.addCollection(named: name, copyingCurrent: false)) }
        store.activeCollection = active
        try MasterStoreIO.save(store, to: h.masterStoreURL)
    }

    // MARK: - Load

    func testTheSidecarAndCacheLoadWithTheStoreAndReconcile() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        try seed(h, file: CollectionsFile(collections: ["Ghost": synced(fileName: "g.json")]),
                 cache: CollectionsLocalCache(synced: ["Ghost": bound("/nowhere/g.json")],
                                              published: ["Ghost": .init(folder: "/nowhere", lastWrittenHash: nil)]))
        let state = h.create()
        XCTAssertTrue(state.collectionsFile.collections.isEmpty, "a sidecar entry with no collection in the store is dropped")
        XCTAssertTrue(state.collectionsCache.synced.isEmpty, "a binding the sidecar no longer vouches for is dropped")
        XCTAssertTrue(state.collectionsCache.published.isEmpty)
        XCTAssertEqual(state.kind(of: state.activeCollection), .local)
        XCTAssertFalse(state.activeCollectionIsSynced)
    }

    func testAnUnreadableSidecarLeavesTheCacheAlone() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        try seed(h, state, file: CollectionsFile(collections: ["Default": published(slug: "default")]),
                 cache: CollectionsLocalCache(synced: [:], published: ["Default": .init(folder: "/tmp/share", lastWrittenHash: nil)]))
        XCTAssertEqual(state.collectionsCache.published["Default"]?.folder, "/tmp/share")

        try TempDir.touch(h.storeDir.appendingPathComponent(CollectionsFile.fileName), "{not json")
        state.reload()
        XCTAssertEqual(state.collectionsCache.published["Default"]?.folder, "/tmp/share",
                       "a half-written sidecar must not be read as \u{201C}nothing is published any more\u{201D}")
        XCTAssertTrue(state.isPublished("Default"))
    }

    func testAnUnlocatedSyncedCollectionBindsAFileBesideTheStore() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        try seedStore(h, collections: ["Team"])
        try seed(h, file: CollectionsFile(collections: [
            "Team": synced(fileName: "team.json", relativeToStore: "shared/team.json"),
        ]))
        let source = h.storeDir.appendingPathComponent("shared/team.json")
        try TempDir.touch(source, "{\"version\":1}")

        let state = h.create()
        XCTAssertEqual(state.sourceBinding(of: "Team")?.path, source.standardizedFileURL.path)
        XCTAssertEqual(state.sourceBinding(of: "Team")?.lastHash, ContentHash.sha256(Data("{\"version\":1}".utf8)))
        XCTAssertEqual(CollectionsLocalCache.load(from: state.service.paths.collectionsCacheURL).synced["Team"]?.path,
                       source.standardizedFileURL.path, "the binding is persisted, so the next launch starts bound")
        XCTAssertNil(state.collectionBanner, "a bound collection asks for nothing")
    }

    func testASyncedCollectionWithNoFileBesideTheStoreKeepsAskingToBeLocated() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        try seedStore(h, collections: ["Team"])
        try seed(h, file: CollectionsFile(collections: [
            "Team": synced(fileName: "team.json", relativeToStore: "shared/team.json"),
        ]))
        let state = h.create()
        XCTAssertNil(state.sourceBinding(of: "Team")?.path)
        XCTAssertEqual(state.collectionBanner, .locate(collection: "Team", fileName: "team.json"))
    }

    func testACustomStoreDirKeepsTheCacheMachineLocal() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let custom = h.dir.file("Dropbox/Connector Control")
        h.settings.masterStoreDir = custom.path
        let state = h.create()
        XCTAssertEqual(state.service.paths.storeDirURL.path, custom.path)
        XCTAssertEqual(state.service.paths.collectionsCacheURL,
                       h.storeDir.appendingPathComponent(CollectionsLocalCache.fileName),
                       "the bindings name files that exist on this machine only")
        XCTAssertFalse(state.service.paths.collectionsCacheURL.path.hasPrefix(custom.path))
    }

    // MARK: - Named actions

    func testCreateRenameAndDeleteByName() {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Work"))
        XCTAssertNil(state.renameCollection("Work", to: "Team"))
        XCTAssertEqual(state.collectionNames, ["Default", "Team"])
        XCTAssertEqual(state.activeCollection, "Team", "a new collection becomes the active one")
        XCTAssertNil(state.deleteCollection(named: "Team"))
        XCTAssertEqual(state.collectionNames, ["Default"])
        XCTAssertEqual(state.deleteCollection(named: state.activeCollection), AppState.lastLocalCollectionError)
    }

    /// Enabling a connector in a named collection: the popover's switch reaches the active one
    /// through the same verb.
    func testSetEnabledInAnInactiveCollectionLeavesClaudesConfigAlone() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Work"))
        state.switchCollection(to: "Default")

        state.setEnabled("aws-mcp", false, in: "Work")
        XCTAssertEqual(state.store.collections["Work"]?.mcps["aws-mcp"]?.enabled, false)
        XCTAssertEqual(try h.storeOnDisk().collections["Work"]?.mcps["aws-mcp"]?.enabled, false)
        XCTAssertEqual(state.store.collections["Default"]?.mcps["aws-mcp"]?.enabled, true)
        XCTAssertNotNil(try h.claudeServers()["aws-mcp"], "Claude runs the active collection, which did not change")

        // The same call on the active collection does reach Claude.
        state.setEnabled("aws-mcp", false, in: "Default")
        XCTAssertNil(try h.claudeServers()["aws-mcp"])
    }

    func testCreateCopiesTheActiveCollectionAndReportsItsErrors() {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertEqual(state.createCollection(named: "Default"), "A collection named \u{201C}Default\u{201D} already exists.")
        XCTAssertEqual(state.createCollection(named: "   "), AppState.nameEmptyError)
        XCTAssertEqual(state.collectionNames, ["Default"])
        XCTAssertNil(state.createCollection(named: "Work"))
        XCTAssertEqual(state.sortedNames, ["aws-mcp", "scoutbook", "service-now"], "a COPY of the active collection")
        XCTAssertNil(h.settings.lastApplyDate, "a copy runs what Claude already runs, so nothing is written")
        XCTAssertEqual(state.collectionsCache.lastAppliedCollection, "Work",
                       "but Claude's file now holds the new collection, which the launch ingest reads")
    }

    /// Copy to ▸ New Collection's first half: a collection holding nothing, not a copy of the
    /// active one, and not made active, since switching to it would empty Claude's config.
    func testAddEmptyCollectionLeavesTheActiveCollectionAndClaudesConfigAlone() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNotNil(state.store.collections["Default"]?.mcps["aws-mcp"])
        let before = try Data(contentsOf: h.claudeConfigURL)
        XCTAssertNil(state.addEmptyCollection(named: "  Empty  "))
        XCTAssertEqual(state.collectionNames, ["Default", "Empty"])
        XCTAssertEqual(state.store.collections["Empty"]?.mcps, [:], "empty, not a copy of the active collection")
        XCTAssertNil(state.store.collections["Empty"]?.mcps["aws-mcp"])
        XCTAssertEqual(state.activeCollection, "Default")
        XCTAssertEqual(try Data(contentsOf: h.claudeConfigURL), before, "nothing Claude runs has changed, to the byte")
        state.reload()
        XCTAssertEqual(state.store.collections["Empty"]?.mcps, [:], "and it was saved")

        // A name the store refuses is the store's own error, and nothing is added.
        XCTAssertEqual(state.addEmptyCollection(named: "Empty"), "A collection named \u{201C}Empty\u{201D} already exists.")
        XCTAssertEqual(state.collectionNames, ["Default", "Empty"])
        XCTAssertEqual(state.activeCollection, "Default")
    }

    func testRenameAndDeleteReportTheStoresErrors() {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertEqual(state.renameCollection("Nope", to: "Q"), "No collection named \u{201C}Nope\u{201D}.")
        XCTAssertEqual(state.deleteCollection(named: "Nope"), "No collection named \u{201C}Nope\u{201D}.")
        XCTAssertNil(state.createCollection(named: "Work"))
        XCTAssertEqual(state.renameCollection("Work", to: "Default"), "A collection named \u{201C}Default\u{201D} already exists.")
        XCTAssertEqual(state.renameCollection("Work", to: " "), AppState.nameEmptyError)
    }

    /// A collection Claude does not run can be renamed or deleted without Claude hearing of it:
    /// its config is not rewritten and no restart is asked for.
    func testRenamingOrDeletingAnotherCollectionLeavesClaudesConfigAlone() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.addEmptyCollection(named: "Other"))
        h.settings.lastApplyDate = nil
        let before = try Data(contentsOf: h.claudeConfigURL)
        let backups = BackupManager(backupsDir: h.backupsDir)
        let backedUp = try backups.backups(series: "claude_desktop_config")

        XCTAssertNil(state.renameCollection("Other", to: "Else"))
        XCTAssertNil(h.settings.lastApplyDate, "nothing Claude runs changed, so there is nothing to restart for")
        XCTAssertNil(state.deleteCollection(named: "Else"))
        XCTAssertNil(h.settings.lastApplyDate)
        XCTAssertEqual(try Data(contentsOf: h.claudeConfigURL), before)
        XCTAssertEqual(try backups.backups(series: "claude_desktop_config"), backedUp)
        XCTAssertEqual(state.collectionsCache.lastAppliedCollection, "Default")
    }

    func testDeletingTheActiveCollectionSwitchesToTheAlphabeticallyFirstRemaining() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Zeta"))
        XCTAssertNil(state.createCollection(named: "Work"))
        XCTAssertEqual(state.activeCollection, "Work")
        XCTAssertNil(state.deleteCollection(named: "Work"))
        XCTAssertEqual(state.collectionNames, ["Default", "Zeta"])
        XCTAssertEqual(state.activeCollection, "Default")
        XCTAssertEqual(try h.storeOnDisk().activeCollection, "Default")
    }

    func testTheLastLocalCollectionStaysEvenBesideASyncedOne() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]))
        XCTAssertEqual(state.deleteCollection(named: "Default"), AppState.lastLocalCollectionError)
        XCTAssertNil(state.deleteCollection(named: "Team"), "a synced collection is not the one that has to stay")
        XCTAssertEqual(state.collectionNames, ["Default"])
    }

    func testDeletingASyncedCollectionDropsItsBindingAndLeavesTheSourceFile() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let source = h.dir.file("shared/team.json")
        try TempDir.touch(source, "{\"version\":1}")
        XCTAssertNil(state.createCollection(named: "Team"))
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]),
                 cache: CollectionsLocalCache(synced: ["Team": bound(source.path)], published: [:]))
        XCTAssertEqual(state.sourceBinding(of: "Team")?.path, source.path)

        XCTAssertNil(state.deleteCollection(named: "Team"))
        XCTAssertNil(state.collectionsFile.collections["Team"])
        XCTAssertNil(state.sourceBinding(of: "Team"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: source.path), "the source file is never ours to delete")
    }

    func testRenamingCarriesTheSidecarEntryTheBindingsAndTheDerivedState() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]),
                 cache: CollectionsLocalCache(synced: ["Team": bound("/shared/team.json")], published: [:]))
        state.pendingUpdates = ["Team": CollectionDiff(added: ["jira"], removed: [], changed: [])]
        state.sourceErrors = ["Team": "unreadable"]
        state.publishError = CollectionPublishError(collection: "Team", message: "no room")

        XCTAssertNil(state.renameCollection("Team", to: "Data"))
        XCTAssertNil(state.collectionsFile.collections["Team"])
        XCTAssertEqual(state.collectionsFile.collections["Data"]?.fileName, "team.json")
        XCTAssertEqual(state.sourceBinding(of: "Data")?.path, "/shared/team.json")
        XCTAssertEqual(state.pendingUpdates["Data"]?.added, ["jira"])
        XCTAssertEqual(state.sourceErrors["Data"], "unreadable")
        XCTAssertEqual(state.publishError?.collection, "Data")
        XCTAssertEqual(state.activeCollection, "Data")
    }

    // MARK: - Persist

    func testPersistWritesTheSidecarBesideTheStore() {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Work"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: h.storeDir.appendingPathComponent(CollectionsFile.fileName).path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: state.service.paths.collectionsCacheURL.path))
    }

    func testTheSidecarAndCacheSurviveARestart() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let first = h.create()
        XCTAssertNil(first.createCollection(named: "Team"))
        try seed(h, first, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]),
                 cache: CollectionsLocalCache(synced: ["Team": bound("/shared/team.json")], published: [:]))
        XCTAssertNil(first.renameCollection("Team", to: "Data"))   // a real persist of both files
        first.dispose()

        let second = h.create()
        XCTAssertEqual(second.kind(of: "Data"), .synced)
        XCTAssertEqual(second.sourceBinding(of: "Data")?.path, "/shared/team.json")
    }

    func testAHalfWrittenSidecarIsNeverOverwrittenByASave() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        let sidecar = h.storeDir.appendingPathComponent(CollectionsFile.fileName)
        let cacheURL = state.service.paths.collectionsCacheURL
        let bindings = try Data(contentsOf: cacheURL)

        // A sync tool is halfway through writing the sidecar when the app next looks at it.
        try TempDir.touch(sidecar, "{half")
        state.reload()
        state.setEnabled("aws-mcp", false)
        XCTAssertEqual(state.lastError, AppState.collectionsNotSavedNote, "a change that was not written says so")
        XCTAssertEqual(try String(contentsOf: sidecar, encoding: .utf8), "{half",
                       "a save must not land our copy of the sidecar on top of the real one")
        XCTAssertEqual(try Data(contentsOf: cacheURL), bindings, "the bindings that hang off it wait too")
        XCTAssertEqual(try h.storeOnDisk().mcps["aws-mcp"]?.enabled, false, "the master list is ours alone, and still saves")

        // The write finishes: the next load reads it, and saves resume.
        try CollectionsFile(collections: ["Data team": synced(fileName: "data-team.json")])
            .save(to: sidecar, staging: nil)
        state.reload()
        XCTAssertEqual(state.kind(of: "Data team"), .synced)
        state.stopSyncing("Data team")
        XCTAssertTrue(CollectionsFile.load(from: sidecar).collections.isEmpty)
        XCTAssertNil(state.lastError, "the save landed, so the note goes with it")
    }

    func testASidecarChangedElsewhereIsRewrittenWhenOurBytesReturn() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        let sidecar = h.storeDir.appendingPathComponent(CollectionsFile.fileName)

        // Another machine writes the same collection under a different file name.
        var entry = try XCTUnwrap(state.collectionsFile.collections["Data team"])
        entry.fileName = "moved.json"
        try CollectionsFile(collections: ["Data team": entry]).save(to: sidecar, staging: nil)
        state.reload()
        XCTAssertEqual(state.collectionsFile.collections["Data team"]?.fileName, "moved.json")

        // Pointing it back at the file this machine has restores exactly the bytes we once wrote.
        XCTAssertNil(state.locateSource(for: "Data team", path: url.path))
        XCTAssertEqual(CollectionsFile.load(from: sidecar).collections["Data team"]?.fileName, "data-team.json",
                       "what is in memory is what the file must hold, whatever this app last wrote")
    }

    func testASidecarSaveFailureSetsTheErrorAndStopsTheChain() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        let sidecar = h.storeDir.appendingPathComponent(CollectionsFile.fileName)
        let cacheURL = state.service.paths.collectionsCacheURL
        let bindings = try Data(contentsOf: cacheURL)

        // Nothing can be written where a directory holds the name. Simpler than a permission
        // change, and the same failure on both platforms.
        try FileManager.default.removeItem(at: sidecar)
        try FileManager.default.createDirectory(at: sidecar, withIntermediateDirectories: false)

        // Stop Syncing changes the sidecar, so the save is really attempted, and it is the one
        // collection action that does not re-apply afterwards and clear the error again.
        state.stopSyncing("Data team")
        XCTAssertNotNil(state.lastError)
        XCTAssertEqual(try Data(contentsOf: cacheURL), bindings, "the cache save behind it never ran")
        XCTAssertNotNil(try h.storeOnDisk().collections["Data team"], "the master list saved first, and is intact")
        XCTAssertNil(state.collectionsFile.collections["Data team"], "the failure is the disk's, not a rollback")
    }

    // MARK: - Queries

    func testTheCollectionQueriesReadTheSidecarAndTheStore() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        let tokenPointer = try XCTUnwrap(JSONPointer(string: "/env/DBT_TOKEN"))
        let config = JSONValue.object([
            "command": .string("node"),
            "args": .array([.string("\(Placeholder.directoryToken)/server.js")]),
            "env": .object(["DBT_TOKEN": .string(Placeholder.marker("DBT_TOKEN"))]),
        ])
        XCTAssertNil(state.upsert(name: "dbt", entry: MCPEntry(config: config), renamedFrom: nil))
        try seed(h, state, file: CollectionsFile(collections: [
            "Team": synced(fileName: "team.json",
                           needs: ["dbt": ["DBT_TOKEN": CollectionsFile.Need(hint: "your dbt token", pointer: tokenPointer)]]),
        ]))

        XCTAssertTrue(state.isSynced("Team"))
        XCTAssertTrue(state.activeCollectionIsSynced)
        XCTAssertFalse(state.isSynced("Default"))
        XCTAssertFalse(state.isPublished("Team"))
        XCTAssertEqual(state.localCollectionNames, ["Default"])
        XCTAssertEqual(state.needs(of: "dbt", in: "Team")["DBT_TOKEN"]?.hint, "your dbt token")
        XCTAssertTrue(state.needs(of: "dbt", in: "Default").isEmpty)
        XCTAssertEqual(state.connectorCaution("dbt", in: "Team"), AppState.needsValueCaution("DBT_TOKEN"))
        XCTAssertNil(state.connectorCaution("aws-mcp", in: "Team"))
        XCTAssertNil(state.connectorCaution("nope", in: "Team"))

        // With the marker filled, the unbound directory token is what is left to complain about.
        let filled = try XCTUnwrap(config.replacing(at: tokenPointer, with: .string("secret")))
        XCTAssertNil(state.upsert(name: "dbt", entry: MCPEntry(config: filled), renamedFrom: "dbt"))
        XCTAssertEqual(state.connectorCaution("dbt", in: "Team"), AppState.locateCaution)
    }

    func testPublishingIsAFactAboutALocalCollection() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        try seed(h, state, file: CollectionsFile(collections: ["Default": published(slug: "default")]),
                 cache: CollectionsLocalCache(synced: [:], published: ["Default": .init(folder: "/tmp/share", lastWrittenHash: nil)]))
        XCTAssertTrue(state.isPublished("Default"))
        XCTAssertEqual(state.kind(of: "Default"), .local, "publishing is a fact about a local collection, not a third kind")
        XCTAssertFalse(state.isPublished("Nope"))
    }

    // MARK: - Banner

    func testTheBannerFollowsItsPrecedence() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Alpha"))
        XCTAssertNil(state.createCollection(named: "Team"))
        let bothSynced = CollectionsFile(collections: [
            "Alpha": synced(fileName: "alpha.json"), "Team": synced(fileName: "team.json"),
        ])
        try seed(h, state, file: bothSynced)
        XCTAssertEqual(state.activeCollection, "Team")

        // Locate: the active collection first, then the alphabetically first of the rest.
        XCTAssertEqual(state.collectionBanner, .locate(collection: "Team", fileName: "team.json"))
        try seed(h, state, file: bothSynced,
                 cache: CollectionsLocalCache(synced: ["Team": bound("/shared/team.json")], published: [:]))
        XCTAssertEqual(state.collectionBanner, .locate(collection: "Alpha", fileName: "alpha.json"))

        // Any pending update outranks any locate; the active collection's outranks the rest.
        let alphaDiff = CollectionDiff(added: ["jira"], removed: [], changed: [])
        state.pendingUpdates = ["Alpha": alphaDiff]
        XCTAssertEqual(state.collectionBanner, .updateAvailable(collection: "Alpha", summary: alphaDiff.summary()))
        let teamDiff = CollectionDiff(added: [], removed: ["datadog"], changed: [])
        state.pendingUpdates["Team"] = teamDiff
        XCTAssertEqual(state.collectionBanner, .updateAvailable(collection: "Team", summary: teamDiff.summary()))

        // A failed publish outranks everything, for any collection.
        state.publishError = CollectionPublishError(collection: "Default", message: "the folder is read-only")
        XCTAssertEqual(state.collectionBanner, .publishFailed(collection: "Default", message: "the folder is read-only"))
        state.publishError = nil
        XCTAssertEqual(state.collectionBanner, .updateAvailable(collection: "Team", summary: teamDiff.summary()))
    }

    func testAPurelyLocalSetupHasNoBanner() {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Work"))
        XCTAssertNil(state.collectionBanner)
        XCTAssertTrue(state.pendingUpdates.isEmpty)
        XCTAssertTrue(state.sourceErrors.isEmpty)
        XCTAssertNil(state.publishError)
    }

    // MARK: - Synced collections


    /// One local connector, authored on `platform`, so a test can pin what a launcher from the
    /// other platform (or a directory token) does without carrying the four-connector sample.
    private func oneLocalConnector(_ name: String, command: String, args: [String],
                                   platform: CollectionPlatform = .current) -> CollectionDocument {
        CollectionDocument(
            name: "Tools", author: nil, origin: "o-tools", exported: "2026-09-21T14:02:11Z",
            connectors: [name: .init(launcher: .local(.init(command: command, args: args, platform: platform)))])
    }

    /// The sample with github gone and dbt's arguments changed — an author's next commit.
    private func changedSample() -> CollectionDocument {
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["github"] = nil
        doc.connectors["dbt"]?.launcher = .local(.init(command: "npx", args: ["-y", "@dbt/mcp@2"], platform: .mac))
        return doc
    }

    func testSubscribeCreatesADisabledReadOnlyMirror() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        XCTAssertEqual(state.kind(of: "Data team"), .synced)
        let mcps = try XCTUnwrap(state.store.collections["Data team"]).mcps
        XCTAssertEqual(mcps.count, 4)
        XCTAssertTrue(mcps.values.allSatisfy { !$0.enabled })
        XCTAssertEqual(mcps["dbt"]?.config.value(at: JSONPointer(["env", "DBT_TOKEN"])), .string("${CC_NEEDS:DBT_TOKEN}"))
        XCTAssertEqual(state.sourceBinding(of: "Data team")?.path, url.path)
        XCTAssertEqual(state.needs(of: "dbt", in: "Data team")["DBT_TOKEN"]?.hint, "cloud.getdbt.com ▸ API tokens")
        XCTAssertTrue(state.pendingUpdates.isEmpty)
        XCTAssertEqual(state.activeCollection, "Default",
                       "everything in it arrives disabled, so subscribing must not empty Claude's config")
        XCTAssertEqual(state.watchedSourceCollections, ["Data team"])
    }

    func testSubscribeNamesTheCollectionAndRefusesWhatItCannotRead() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("data-team.json")
        try h.writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: "Analytics"), "the caller's name beats the document's")
        XCTAssertEqual(state.kind(of: "Analytics"), .synced)

        let missing = h.dir.file("gone.json")
        XCTAssertNotNil(state.subscribe(documentAt: missing.path, as: nil))
        let half = h.dir.file("half.json")
        try TempDir.touch(half, "{half")
        XCTAssertEqual(state.subscribe(documentAt: half.path, as: nil)?.hasPrefix("half.json couldn’t be read: "), true)
        let list = h.dir.file("list.json")
        try TempDir.touch(list, "[]")
        XCTAssertEqual(state.subscribe(documentAt: list.path, as: nil),
                       AppState.sourceUnreadableError("list.json", "top level is not a JSON object"),
                       "what is wrong with the document, as it stands")
        var newer = CollectionDocumentSamples.dataTeam.encode()
        newer = try XCTUnwrap(newer.replacing(at: JSONPointer(["connectorControlCollection"]), with: .int(2)))
        let future = h.dir.file("future.json")
        try newer.serialized().write(to: future)
        XCTAssertEqual(state.subscribe(documentAt: future.path, as: nil), AppState.newerDocumentError)
        XCTAssertEqual(state.collectionNames, ["Analytics", "Default"], "a refused document creates nothing")
    }

    func testSubscribingToYourOwnPublishedCollectionIsRefused() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        // What publishing leaves behind: a local collection whose document carries
        // this origin. Reading it back in would make the app its own author.
        try seed(h, state, file: CollectionsFile(collections: [
            "Default": CollectionsFile.Entry(kind: .local, publish: CollectionsFile.PublishRecord(
                slug: "data-team", origin: "6f1c4a2e-1b8d-4b0e-9f0a-3c2d7e8a91e2", intent: .none)),
        ]))
        let url = h.dir.file("data-team.json")
        try h.writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertEqual(state.subscribe(documentAt: url.path, as: nil), AppState.ownCollectionError)
        XCTAssertEqual(state.collectionNames, ["Default"])
    }

    func testASourceChangeBecomesAPendingUpdateThatApplyLandsWithFilledValuesKept() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        // The user fills the token and turns dbt on.
        state.switchCollection(to: "Data team")
        var dbt = try XCTUnwrap(state.store.collections["Data team"]?.mcps["dbt"])
        dbt.config = dbt.config.replacing(at: JSONPointer(["env", "DBT_TOKEN"]), with: .string("tok"))!
        dbt.enabled = true
        XCTAssertNil(state.upsert(name: "dbt", entry: dbt, renamedFrom: "dbt"))
        XCTAssertTrue(state.pendingUpdates.isEmpty, "a filled marker is not a change to the collection")

        // The author changes dbt's args and deletes github. This test alone waits for the real
        // source watcher to deliver it; the others read the source through recomputePending.
        try h.writeDocument(changedSample(), at: url)
        try TempDir.bumpModificationDate(of: url)
        XCTAssertTrue(h.ui.pumpUntil({ state.pendingUpdates["Data team"] != nil }, timeout: 8))
        XCTAssertEqual(state.pendingUpdates["Data team"]?.summary(), "deletes github; changes dbt")
        XCTAssertEqual(h.notifier.sent.last?.body,
                       AppState.collectionUpdateNotificationBody("Data team", "deletes github; changes dbt"))
        let announced = h.notifier.sent.count
        state.recomputePending()
        XCTAssertEqual(h.notifier.sent.count, announced, "one document, one announcement")

        XCTAssertNil(state.applyPendingUpdate(for: "Data team"))
        let after = try XCTUnwrap(state.store.collections["Data team"]?.mcps["dbt"])
        XCTAssertEqual(after.config.value(at: JSONPointer(["args", "1"])), .string("@dbt/mcp@2"))
        XCTAssertEqual(after.config.value(at: JSONPointer(["env", "DBT_TOKEN"])), .string("tok"))
        XCTAssertTrue(after.enabled)
        XCTAssertNil(state.store.collections["Data team"]?.mcps["github"])
        XCTAssertTrue(state.pendingUpdates.isEmpty)
        XCTAssertEqual(try h.claudeServers()["dbt"]?.value(at: JSONPointer(["args", "1"])), .string("@dbt/mcp@2"),
                       "the active collection's update reaches Claude")
    }

    func testAnUnreadableSourceIsTransientUntilRefreshedByHand() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.subscribe(state, to: CollectionDocumentSamples.dataTeam, at: "t.json", as: "T")
        try Data("{half".utf8).write(to: url)
        state.recomputePending()   // the source watcher's own read, without waiting on the watcher
        XCTAssertTrue(state.sourceErrors.isEmpty, "the first failure schedules a retry instead of reporting")
        XCTAssertEqual(h.delays.pending.count, 1)
        XCTAssertEqual(h.delays.pending.first?.delay, 2)

        state.refreshSource(for: "T")
        XCTAssertNotNil(state.sourceErrors["T"])
        XCTAssertEqual(h.delays.pending.count, 1, "Refresh answers now instead of waiting again")

        // The half-written file lands in full: the next read clears the error and the collection
        // is back to having nothing to say.
        try h.writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        state.refreshSource(for: "T")
        XCTAssertTrue(state.sourceErrors.isEmpty)
        XCTAssertTrue(state.pendingUpdates.isEmpty)
    }

    func testLocateBindsAndTheRelativePathBindsAutomatically() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        // The document travels inside the store's own folder, as a shared master list does.
        let inStore = h.storeDir.appendingPathComponent("shared/data-team.json")
        try h.writeDocument(CollectionDocumentSamples.dataTeam, at: inStore)
        XCTAssertNil(state.subscribe(documentAt: inStore.path, as: nil))
        XCTAssertEqual(state.collectionsFile.collections["Data team"]?.relativeToStore, "shared/data-team.json")

        // Another machine: the sidecar travels with the store, this machine's bindings do not.
        try FileManager.default.removeItem(at: state.service.paths.collectionsCacheURL)
        state.reload()
        XCTAssertEqual(state.sourceBinding(of: "Data team")?.path, inStore.path, "found beside the store, with no prompt")

        // A document somewhere the relative path cannot reach is pointed at by hand.
        let elsewhere = h.dir.file("elsewhere/data-team.json")
        try h.writeDocument(CollectionDocumentSamples.dataTeam, at: elsewhere)
        XCTAssertNil(state.locateSource(for: "Data team", path: elsewhere.path))
        XCTAssertEqual(state.sourceBinding(of: "Data team")?.path, elsewhere.path)
        XCTAssertEqual(state.watchedSourceCollections, ["Data team"])
        XCTAssertTrue(state.pendingUpdates.isEmpty, "the same document in a new place changes nothing")
        XCTAssertNil(state.collectionBanner)
        XCTAssertEqual(state.locateSource(for: "Data team", path: h.dir.file("nope.json").path)?
            .hasPrefix("nope.json couldn’t be read: "), true)
        XCTAssertEqual(state.sourceBinding(of: "Data team")?.path, elsewhere.path, "a file we cannot read is not bound")
    }

    func testStopSyncingKeepsContentAndDropsTheBinding() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        let before = try XCTUnwrap(state.store.collections["Data team"]).mcps

        state.stopSyncing("Data team")
        XCTAssertEqual(state.kind(of: "Data team"), .local)
        XCTAssertNil(state.sourceBinding(of: "Data team"))
        XCTAssertNil(state.collectionsFile.collections["Data team"])
        XCTAssertEqual(state.store.collections["Data team"]?.mcps, before, "the connectors are the user's now")
        XCTAssertTrue(state.watchedSourceCollections.isEmpty)
        XCTAssertTrue(state.needs(of: "ledger", in: "Data team").isEmpty, "the hints travelled with the document")
        XCTAssertEqual(state.connectorCaution("ledger", in: "Data team"), AppState.needsValueCaution("server_path"),
                       "an unfilled marker is still text in the config, so the row still says so")

        // The author's next change reaches nobody: there is no binding left to read it through.
        try h.writeDocument(changedSample(), at: url)
        state.recomputePending()
        XCTAssertTrue(state.pendingUpdates.isEmpty)
    }

    func testDeletingASyncedCollectionLeavesTheFileAlone() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)

        XCTAssertNil(state.deleteCollection(named: "Data team"))
        XCTAssertEqual(state.collectionNames, ["Default"])
        XCTAssertNil(state.sourceBinding(of: "Data team"))
        XCTAssertTrue(state.watchedSourceCollections.isEmpty)
        XCTAssertTrue(FileManager.default.fileExists(atPath: url.path), "the source file is never ours to delete")
    }

    func testTheDirectoryTokenExpandsAgainstTheBoundFolderWhenApplied() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.subscribe(state, to: oneLocalConnector("x", command: "node", args: ["\(Placeholder.directoryToken)/srv.js"]), at: "tools/servers.json")
        state.switchCollection(to: "Tools")
        state.setEnabled("x", true)

        let directory = url.deletingLastPathComponent().path
        XCTAssertEqual(args(of: try XCTUnwrap(h.claudeServers()["x"]))?.first, directory + "/srv.js")
        XCTAssertEqual(state.store.collections["Tools"]?.mcps["x"]?.config.value(at: JSONPointer(["args", "0"])),
                       .string("\(Placeholder.directoryToken)/srv.js"),
                       "the store keeps the token, so the same list resolves on the next machine too")
        XCTAssertNil(state.connectorCaution("x", in: "Tools"))
        XCTAssertFalse(state.applyRetryNeeded)
        state.reload()
        XCTAssertEqual(args(of: try XCTUnwrap(h.claudeServers()["x"]))?.first, directory + "/srv.js",
                       "the expanded config is what we wrote, so a reload finds nothing to regenerate")
    }

    func testALocalConnectorAuthoredOnTheOtherPlatformIsFlagged() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let other: CollectionPlatform = CollectionPlatform.current == .mac ? .windows : .mac
        let url = try h.subscribe(state, to: oneLocalConnector("x", command: "node", args: ["srv.js"], platform: other), at: "tools.json")
        XCTAssertEqual(state.connectorCaution("x", in: "Tools"), AppState.authoredElsewhereCaution)

        try h.writeDocument(oneLocalConnector("x", command: "node", args: ["srv.js"]), at: url)
        state.refreshSource(for: "Tools")
        XCTAssertNil(state.connectorCaution("x", in: "Tools"), "a launcher from this platform needs no warning")
    }

    func testAnUnchangedSourceIsNotReRenderedButPendingIsReDerived() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        state.switchCollection(to: "Data team")
        let renders = state.sourceRenders

        // A local edit makes the collection differ from its source without the file moving.
        var dbt = try XCTUnwrap(state.store.collections["Data team"]?.mcps["dbt"])
        dbt.config = dbt.config.replacing(at: JSONPointer(["args", "1"]), with: .string("@dbt/mcp@local"))!
        XCTAssertNil(state.upsert(name: "dbt", entry: dbt, renamedFrom: "dbt"))
        XCTAssertEqual(state.pendingUpdates["Data team"]?.summary(), "changes dbt")
        XCTAssertEqual(state.sourceRenders, renders, "the same bytes are never decoded twice")

        // Another machine applies the source, and its master list arrives here.
        try h.editStoreOnDisk { store in
            let rendered = try XCTUnwrap(state.pendingDocument(for: "Data team"))
            let applied = CollectionApply.apply(rendered: rendered, current: store.collections["Data team"]?.mcps ?? [:],
                                                previousNeeds: state.collectionsFile.collections["Data team"]?.needs ?? [:])
            store.collections["Data team"] = Collection(mcps: applied.entries)
        }
        state.reload()
        XCTAssertTrue(state.pendingUpdates.isEmpty, "the list already matches the document; the banner must not outlive it")
        XCTAssertEqual(state.sourceRenders, renders, "and re-deriving it still costs no decode")
    }

    /// A retry waiting under the old name dies harmlessly when it fires, and the next failure under
    /// the new one starts a chain of its own: renaming a collection during a backoff must not turn
    /// automatic retries off.
    func testARenameDuringTheBackoffKeepsRetrying() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.subscribe(state, to: CollectionDocumentSamples.dataTeam, at: "t.json", as: "T")
        try Data("{half".utf8).write(to: url)
        state.recomputePending()   // the source watcher's own read, without waiting on the watcher
        XCTAssertFalse(h.delays.pending.isEmpty)

        XCTAssertNil(state.renameCollection("T", to: "U"))
        // Every retry that is due, the old name's included, until the chain has nothing left.
        for _ in 0..<10 where !h.delays.pending.isEmpty { h.delays.runNext() }
        XCTAssertNotNil(state.sourceErrors["U"], "the chain went on under the new name to its third failure")
        XCTAssertTrue(h.delays.pending.isEmpty)
    }

    func testTheRetryChainReportsOnlyTheThirdFailure() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.subscribe(state, to: CollectionDocumentSamples.dataTeam, at: "t.json", as: "T")
        try Data("{half".utf8).write(to: url)
        state.recomputePending()   // the source watcher's own read, without waiting on the watcher
        XCTAssertFalse(h.delays.pending.isEmpty)

        XCTAssertEqual(h.delays.pending.map(\.delay), [2])
        XCTAssertTrue(state.sourceErrors.isEmpty)
        h.delays.runNext()
        XCTAssertEqual(h.delays.pending.map(\.delay), [10])
        XCTAssertTrue(state.sourceErrors.isEmpty, "the second failure is still worth waiting out")
        h.delays.runNext()
        XCTAssertEqual(h.delays.pending.map(\.delay), [30])
        XCTAssertNotNil(state.sourceErrors["T"], "the third consecutive failure is the one to report")
        h.delays.runNext()
        XCTAssertTrue(h.delays.pending.isEmpty, "the backoff gives up after the third retry")
        XCTAssertNotNil(state.sourceErrors["T"])

        try h.writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        state.refreshSource(for: "T")
        XCTAssertTrue(state.sourceErrors.isEmpty)
    }

    func testAPendingUpdateFoundAtLaunchIsNotAnnouncedTwice() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let url = h.dir.file("data-team.json")
        let first = h.create()
        try h.writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(first.subscribe(documentAt: url.path, as: nil))
        first.dispose()

        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["github"] = nil
        try h.writeDocument(doc, at: url)
        h.notifier.clearSent()

        let second = h.create()
        XCTAssertEqual(second.pendingUpdates["Data team"]?.summary(), "deletes github")
        XCTAssertTrue(h.notifier.sent.isEmpty, "the banner already says it; a launch is not news")
        second.reload()
        XCTAssertTrue(h.notifier.sent.isEmpty, "and the reload behind it must not announce it either")
    }

    func testApplyingAnInactiveCollectionLeavesClaudesConfigAlone() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        XCTAssertEqual(state.activeCollection, "Default")
        let before = try h.claudeServers()

        try h.writeDocument(changedSample(), at: url)
        state.recomputePending()   // the source watcher's own read, without waiting on the watcher
        XCTAssertNotNil(state.pendingUpdates["Data team"])

        XCTAssertNil(state.applyPendingUpdate(for: "Data team"))
        XCTAssertNil(state.store.collections["Data team"]?.mcps["github"])
        XCTAssertEqual(try h.claudeServers(), before, "Claude runs the active collection, and that one did not change")
        XCTAssertFalse(state.needsClaudeRestart)
    }

    func testLocateAppliesTheExpandedPathAtOnce() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.subscribe(state, to: oneLocalConnector("x", command: "node", args: ["\(Placeholder.directoryToken)/srv.js"]), at: "tools/servers.json")
        // A machine that has the collection but not the file yet: the bindings never travel.
        try FileManager.default.removeItem(at: state.service.paths.collectionsCacheURL)
        state.reload()
        XCTAssertNil(state.sourceBinding(of: "Tools")?.path)
        state.switchCollection(to: "Tools")
        state.setEnabled("x", true)
        XCTAssertEqual(args(of: try XCTUnwrap(h.claudeServers()["x"]))?.first, "\(Placeholder.directoryToken)/srv.js",
                       "with nothing bound the token is written as it stands")
        XCTAssertEqual(state.connectorCaution("x", in: "Tools"), AppState.locateCaution)

        XCTAssertNil(state.locateSource(for: "Tools", path: url.path))
        XCTAssertEqual(args(of: try XCTUnwrap(h.claudeServers()["x"]))?.first,
                       url.deletingLastPathComponent().path + "/srv.js", "locating resolves it without another click")
        XCTAssertNil(state.connectorCaution("x", in: "Tools"))
    }

    func testStopSyncingBakesTheExpandedPathIn() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.subscribe(state, to: oneLocalConnector("x", command: "node", args: ["\(Placeholder.directoryToken)/srv.js"]), at: "tools/servers.json")
        state.switchCollection(to: "Tools")
        state.setEnabled("x", true)
        let expanded = url.deletingLastPathComponent().path + "/srv.js"
        XCTAssertEqual(args(of: try XCTUnwrap(h.claudeServers()["x"]))?.first, expanded)

        state.stopSyncing("Tools")
        XCTAssertEqual(state.store.collections["Tools"]?.mcps["x"]?.config.value(at: JSONPointer(["args", "0"])),
                       .string(expanded), "the folder it resolved to is the collection's own path now")
        XCTAssertEqual(args(of: try XCTUnwrap(h.claudeServers()["x"]))?.first, expanded,
                       "so what Claude runs does not change under the user")
        XCTAssertNil(state.connectorCaution("x", in: "Tools"))
        XCTAssertEqual(state.kind(of: "Tools"), .local)
    }

    // The cache records what a render excluded, and an excluded connector never shows as added.
    // Only the Windows build's cmd /c launcher ever excludes anything, so that test lives in the
    // mirror alone: windows/.../AppStateCollectionsTests.cs
    // AnExcludedConnectorIsRecordedInTheCacheAndNeverShowsAsAdded.

    func testACorruptSidecarAtLaunchKeepsTheCacheBindings() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        try seedStore(h, collections: ["Team"])
        try seed(h, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]),
                 cache: CollectionsLocalCache(synced: ["Team": bound("/shared/team.json")], published: [:]))
        let sidecar = h.storeDir.appendingPathComponent(CollectionsFile.fileName)
        try TempDir.touch(sidecar, "{not json")

        let state = h.create()
        XCTAssertEqual(state.sourceBinding(of: "Team")?.path, "/shared/team.json",
                       "a good cache outlives a sidecar a sync tool is halfway through writing")
        XCTAssertEqual(state.kind(of: "Team"), .local, "with nothing readable to vouch for it, nothing is synced")

        try CollectionsFile(collections: ["Team": synced(fileName: "team.json")]).save(to: sidecar, staging: nil)
        state.reload()
        XCTAssertEqual(state.kind(of: "Team"), .synced)
        XCTAssertEqual(state.sourceBinding(of: "Team")?.path, "/shared/team.json")
    }

    // MARK: - Publishing and export

    /// A folder to publish into, created so the guards read a real directory rather than a path.
    private func publishFolder(_ h: AppStateHarness, _ name: String = "pub") throws -> URL {
        let url = h.dir.file(name)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    /// A local connector with two environment values and an absolute path argument — what the
    /// Publish sheet's rows are built from, and what the intent strips or shares.
    private var ledgerConfig: JSONValue {
        .object([
            "command": .string("node"),
            "args": .array([.string("/Users/d/ledger/dist/index.js")]),
            "env": .object(["A": .string("sk-live-secret"), "B": .string("us")]),
        ])
    }

    private func newConnector(_ name: String) -> MCPEntry {
        MCPEntry(config: .object(["command": .string(name)]))
    }

    func testStartPublishingWritesTheDocumentAndRepublishesOnlyWhenTheContentChanges() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        let file = folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json")
        let first = try Data(contentsOf: file)
        XCTAssertTrue(state.isPublished(state.activeCollection))
        XCTAssertEqual(state.collectionsCache.published[state.activeCollection]?.folder, folder.path)

        // The clock moves between the two saves. What decides a rewrite is what the document
        // says, never when it was written, or every toggle would publish.
        h.now = h.now.addingTimeInterval(60)
        state.setEnabled("aws-mcp", false)
        XCTAssertEqual(try Data(contentsOf: file), first, "toggles never change the document")

        XCTAssertNil(state.upsert(name: "new", entry: newConnector("x"), renamedFrom: nil))
        let second = try Data(contentsOf: file)
        XCTAssertNotEqual(second, first)
        let document = try CollectionDocument.decode(second)
        XCTAssertNotNil(document.origin)
        XCTAssertEqual(document.name, state.activeCollection)
        XCTAssertEqual(document.exported, IsoTimestamp.string(from: h.now))
        XCTAssertEqual(document.connectors["new"]?.env, [:], "the added connector travels, with no env to share")
        XCTAssertNil(state.publishError)
    }

    func testASyncedCollectionCannotBePublished() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        let folder = try publishFolder(h)

        // A synced collection has an author elsewhere, and nothing in the window offers Publish
        // for one — its ⋯ menu has Refresh and Make Local Copy instead. The refusal is the
        // same silence locateSource gives a collection that is not synced.
        XCTAssertNil(state.startPublishing("Data team", to: folder.path, intent: .none))
        XCTAssertFalse(state.isPublished("Data team"))
        XCTAssertNil(state.collectionsFile.collections["Data team"]?.publish)
        XCTAssertNil(state.collectionsCache.published["Data team"])
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: folder.path), [],
                       "nothing was written for it")
        XCTAssertEqual(state.kind(of: "Data team"), .synced, "and it is still a synced collection")
        XCTAssertNil(state.publishError)
    }

    func testPublishGuards() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertEqual(state.startPublishing(state.activeCollection, to: h.storeDir.path, intent: .none),
                       AppState.publishIntoStoreError)
        XCTAssertEqual(state.startPublishing(state.activeCollection, to: h.backupsDir.appendingPathComponent("2026").path,
                                             intent: .none),
                       AppState.publishIntoStoreError, "a folder inside the backups folder is the backups folder")

        let folder = try publishFolder(h)
        let fileName = Slug.make(state.activeCollection) + ".json"
        try h.writeDocument(CollectionDocumentSamples.dataTeam, at: folder.appendingPathComponent(fileName))
        XCTAssertEqual(state.startPublishing(state.activeCollection, to: folder.path, intent: .none),
                       AppState.publishSlugTakenError(fileName))
        XCTAssertFalse(state.isPublished(state.activeCollection), "a refused publish records nothing")
    }

    func testPublishingAgainIntoTheFolderItAlreadyOwnsIsAllowed() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        // The document sitting there carries this collection's own origin, which is the whole
        // point of the guard: only somebody else's file is in the way.
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
    }

    func testPublishingIsRefusedWhileTheCollectionFileCannotBeRead() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        try TempDir.touch(h.storeDir.appendingPathComponent(CollectionsFile.fileName), "{half")
        state.reload()
        XCTAssertEqual(state.startPublishing(state.activeCollection, to: folder.path, intent: .none),
                       AppState.collectionsNotSavedNote, "a publish record that cannot be saved must not be created")
        XCTAssertFalse(FileManager.default.fileExists(
            atPath: folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json").path))
        XCTAssertFalse(state.isPublished(state.activeCollection))
    }

    func testAPublishFailureRaisesTheBannerAndClearsOnSuccess() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        let file = folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json")
        XCTAssertTrue(FileManager.default.fileExists(atPath: file.path))

        // A file where the folder belongs fails the write on both platforms and needs no
        // permission games. Deleting the folder would not: the writer creates it again.
        try FileManager.default.removeItem(at: folder)
        try TempDir.touch(folder, "not a folder")
        XCTAssertNil(state.upsert(name: "new", entry: newConnector("x"), renamedFrom: nil))
        XCTAssertEqual(state.publishError?.collection, state.activeCollection)
        guard case .publishFailed(let collection, _)? = state.collectionBanner else {
            return XCTFail("a failed publish outranks every other banner")
        }
        XCTAssertEqual(collection, state.activeCollection)

        try FileManager.default.removeItem(at: folder)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        XCTAssertNil(state.upsert(name: "another", entry: newConnector("y"), renamedFrom: nil))
        XCTAssertNil(state.publishError, "a write that succeeds clears the mark")
        let document = try CollectionDocument.decode(try Data(contentsOf: file))
        XCTAssertNotNil(document.connectors["new"], "the change the failed write held back still lands")
        XCTAssertNotNil(document.connectors["another"])
    }

    func testExportStripsSecretsAndPublishedDocumentsStripThemToo() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: ledgerConfig), renamedFrom: nil))
        let intent = PublishIntent(shareValues: ["ledger": ["B"]], pathMarks: [:],
                                   hints: ["ledger": ["A": "your ledger token"]])

        let exported = h.dir.file("out/ledger.json")
        XCTAssertNil(state.writeExport(for: state.activeCollection, intent: intent, to: exported.path))
        let exportedBytes = try Data(contentsOf: exported)
        let document = try CollectionDocument.decode(exportedBytes)
        XCTAssertEqual(document.connectors["ledger"]?.env["A"], .hint("your ledger token"),
                       "an unshared value travels as its hint")
        XCTAssertEqual(document.connectors["ledger"]?.env["B"], .value("us"))
        XCTAssertFalse(jsonText(exportedBytes, contains: "sk-live-secret"))
        XCTAssertNil(document.origin, "nothing published, nothing to identify it by")

        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: intent))
        let published = try Data(contentsOf: folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json"))
        XCTAssertFalse(jsonText(published, contains: "sk-live-secret"))
        XCTAssertEqual(try CollectionDocument.decode(published).connectors["ledger"]?.env["B"], .value("us"))
    }

    func testChangingWhatIsSharedRewritesTheDocument() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: ledgerConfig), renamedFrom: nil))
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        let file = folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json")
        XCTAssertEqual(try CollectionDocument.decode(try Data(contentsOf: file)).connectors["ledger"]?.env["B"], .hint(nil))

        let shared = PublishIntent(shareValues: ["ledger": ["B"]], pathMarks: [:], hints: [:])
        XCTAssertNil(state.updatePublishIntent(state.activeCollection, intent: shared))
        XCTAssertEqual(try CollectionDocument.decode(try Data(contentsOf: file)).connectors["ledger"]?.env["B"], .value("us"))
        XCTAssertEqual(state.collectionsFile.collections[state.activeCollection]?.publish?.intent,
                       shared, "what was ticked is remembered for the next write")
    }

    func testStopPublishingDropsTheRecordAndCanDeleteTheFile() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        let fileName = Slug.make(state.activeCollection) + ".json"
        let file = folder.appendingPathComponent(fileName)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))

        state.stopPublishing(state.activeCollection, deleteFile: false)
        XCTAssertFalse(state.isPublished(state.activeCollection))
        XCTAssertTrue(state.collectionsCache.published.isEmpty)
        let left = try Data(contentsOf: file)
        XCTAssertNil(state.upsert(name: "new", entry: newConnector("x"), renamedFrom: nil))
        XCTAssertEqual(try Data(contentsOf: file), left, "nothing is published once it has stopped")

        // The collection no longer holds the origin that vouched for the document left behind,
        // so publishing here again is refused exactly as somebody else's file would be. That is
        // why Stop Publishing offers to delete it.
        XCTAssertEqual(state.startPublishing(state.activeCollection, to: folder.path, intent: .none),
                       AppState.publishSlugTakenError(fileName))

        let second = try publishFolder(h, "pub2")
        XCTAssertNil(state.startPublishing(state.activeCollection, to: second.path, intent: .none))
        state.stopPublishing(state.activeCollection, deleteFile: true)
        XCTAssertFalse(FileManager.default.fileExists(atPath: second.appendingPathComponent(fileName).path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: file.path),
                      "only the folder it was publishing to is touched")
    }

    func testAChangeThatArrivesFromAnotherMachineIsPublishedOnTheNextLoad() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        let file = folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json")

        // The author's other machine added a connector to the shared master list.
        try h.editStoreOnDisk { store in
            store.collections[store.activeCollection]?.mcps["elsewhere"] = newConnector("z")
        }
        state.reload(trigger: .externalStoreAdoption)
        XCTAssertNotNil(try CollectionDocument.decode(try Data(contentsOf: file)).connectors["elsewhere"],
                        "the publishing machine carries another machine's change to the team")
    }

    func testSubscribingToWhatThisMachinePublishesIsRefused() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        let file = folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json")
        XCTAssertEqual(state.subscribe(documentAt: file.path, as: "Copy"), AppState.ownCollectionError)
        XCTAssertEqual(state.collectionNames, ["Default"])
    }

    // MARK: - A marked path never leaves as written

    private let markedPath = "/Users/d/ledger/dist/index.js"

    /// The active collection publishing `ledger` with its path argument marked, the way the
    /// Publish sheet records it: the pointer and the path it was made on.
    private func publishMarkedLedger(_ h: AppStateHarness, _ state: AppState,
                                     args: [String]) throws -> (folder: URL, file: URL) {
        let index = try XCTUnwrap(args.firstIndex(of: markedPath))
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array(args.map(JSONValue.string)),
        ])), renamedFrom: nil))
        let intent = PublishIntent(shareValues: [:], pathMarks: ["ledger": [JSONPointer(["args", String(index)]):
            .init(name: "server_path", hint: "your ledger clone", value: markedPath)]], hints: [:])
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: intent))
        return (folder, folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json"))
    }

    /// A change to the connector that did not come through its editor — the JSON view of another
    /// window, the author's other machine, an older app — so nothing re-keyed the marks.
    private func rewriteLedger(_ state: AppState, args: [String]) {
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array(args.map(JSONValue.string)),
        ])), renamedFrom: "ledger"))
    }

    private func ledgerArgs(in file: URL) throws -> [String] {
        let document = try CollectionDocument.decode(try Data(contentsOf: file))
        guard case .local(let local)? = document.connectors["ledger"]?.launcher else { throw AppStateHarness.HarnessError() }
        return local.args
    }

    func testAMarkMovedOutsideTheEditorFollowsItsPathByValue() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, file) = try publishMarkedLedger(h, state, args: [markedPath, "--quiet"])
        XCTAssertEqual(try ledgerArgs(in: file), ["${CC_NEEDS:server_path}", "--quiet"])

        rewriteLedger(state, args: ["--quiet", markedPath])
        XCTAssertNil(state.publishError)
        XCTAssertEqual(try ledgerArgs(in: file), ["--quiet", "${CC_NEEDS:server_path}"],
                       "the placeholder stays on the path, and the flag that took its place travels as written")
        XCTAssertFalse(try jsonFile(file, contains: markedPath))
    }

    func testAMarkThatLostItsPathFailsClosedUntilItIsMarkedAgain() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, file) = try publishMarkedLedger(h, state, args: [markedPath])
        let before = try Data(contentsOf: file)

        // Edited outside the editor, so the record still names the old path: nothing now says
        // which argument the author meant, and the new path must not go out as written.
        let edited = "/Users/d/ledger-v2/dist/index.js"
        rewriteLedger(state, args: ["--quiet", edited])
        XCTAssertEqual(state.publishError?.collection, state.activeCollection)
        XCTAssertEqual(state.publishError?.message, AppState.pathMarkMovedError("ledger"))
        // Blocked for review, not a failed write: another folder is no answer to a moved mark.
        XCTAssertEqual(state.publishError?.kind, .blockedForReview)
        guard case .publishBlocked(_, let message)? = state.collectionBanner else {
            return XCTFail("the blocked-publish banner shows")
        }
        XCTAssertEqual(message, AppState.pathMarkMovedError("ledger"))
        XCTAssertEqual(try Data(contentsOf: file), before, "no file is written: the old document, placeholder and all, stays")
        // Export… goes through the sheet, which holds the lost mark and will not write.
        let exported = h.dir.file("out/copy.json")
        XCTAssertEqual(PublishModel(state: state, collection: state.activeCollection).export(to: exported.path),
                       PublishModel.unresolvedMarkNote("ledger", "server_path"), "an export refuses too")
        XCTAssertFalse(FileManager.default.fileExists(atPath: exported.path))

        // Re-ticking in the Publish sheet records the path where it is now, and clears it.
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        let row = try XCTUnwrap(sheet.pathRows.firstIndex { $0.connector == "ledger" && $0.value == edited })
        XCTAssertFalse(sheet.pathRows[row].marked, "a mark that lost its argument ticks nothing")
        sheet.pathRows[row].marked = true
        XCTAssertEqual(sheet.pathRows[row].name, "server_path", "the tick that answers the lost mark takes its name")
        XCTAssertEqual(sheet.pathRows[row].hint, "your ledger clone", "and its hint")
        XCTAssertNil(sheet.publish())
        XCTAssertNil(state.publishError)
        XCTAssertEqual(try ledgerArgs(in: file), ["--quiet", "${CC_NEEDS:server_path}"])
        XCTAssertFalse(try jsonFile(file, contains: edited))
        XCTAssertEqual(state.collectionsFile.collections[state.activeCollection]?.publish?.intent.pathMarks["ledger"],
                       [JSONPointer(["args", "1"]): .init(name: "server_path", hint: "your ledger clone", value: edited)])
    }

    func testARenamedConnectorKeepsItsMarksAndARemovedOneLeavesNone() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, file) = try publishMarkedLedger(h, state, args: [markedPath])
        let entry = try XCTUnwrap(state.store.collections[state.activeCollection]?.mcps["ledger"])
        XCTAssertNil(state.upsert(name: "books", entry: entry, renamedFrom: "ledger"))
        XCTAssertNil(state.publishError)
        let intent = try XCTUnwrap(state.collectionsFile.collections[state.activeCollection]?.publish?.intent)
        XCTAssertNil(intent.pathMarks["ledger"])
        let marks = try XCTUnwrap(intent.pathMarks["books"])
        XCTAssertEqual(marks.count, 1)
        XCTAssertEqual(marks.values.first?.value, markedPath)
        let document = try CollectionDocument.decode(try Data(contentsOf: file))
        XCTAssertEqual(document.connectors["books"]?.needs.keys.sorted(), ["server_path"])

        state.delete(names: ["books"])
        XCTAssertNil(state.collectionsFile.collections[state.activeCollection]?.publish?.intent.pathMarks["books"],
                     "a connector added later under the same name was never ticked")
        XCTAssertNil(state.publishError)
    }

    func testAMarkWhoseConnectorWasRenamedElsewhereFailsClosed() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, file) = try publishMarkedLedger(h, state, args: [markedPath])
        let before = try Data(contentsOf: file)

        // An older app renamed it on another machine: the master list arrives, the record does
        // not follow, and the path would otherwise travel under the new name as written.
        try h.editStoreOnDisk { store in
            let entry = try XCTUnwrap(store.collections[store.activeCollection]?.mcps.removeValue(forKey: "ledger"))
            store.collections[store.activeCollection]?.mcps["books"] = entry
        }
        state.reload(trigger: .externalStoreAdoption)
        XCTAssertEqual(state.publishError?.message, AppState.pathMarkMovedError("ledger"))
        XCTAssertEqual(try Data(contentsOf: file), before)
    }

    func testAMovedMarkIsClassifiedAsBlockedAndEverythingElseAsAFailedWrite() {
        XCTAssertEqual(AppState.publishErrorKind(of: PublishIntentError.pathMarkMoved(connector: "ledger")),
                       .blockedForReview)
        XCTAssertEqual(AppState.publishErrorKind(of: CocoaError(.fileWriteNoPermission)), .writeFailed)
    }

    func testRenamingACollectionKeepsItsBlockedPublishBlocked() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        state.publishError = CollectionPublishError(collection: "Team", message: "moved", kind: .blockedForReview)
        XCTAssertNil(state.renameCollection("Team", to: "Crew"))
        XCTAssertEqual(state.publishError, CollectionPublishError(collection: "Crew", message: "moved", kind: .blockedForReview))
    }

    // MARK: - This machine's list of marked paths

    /// The record as the author's other machine rewrote it, landing here before its master list
    /// does: what `remove` or a row deletion there leaves in the sidecar.
    private func sidecarLandsFirst(_ h: AppStateHarness, _ state: AppState,
                                   rewriting edit: (PublishIntent) -> PublishIntent) throws {
        var file = state.collectionsFile
        var record = try XCTUnwrap(file.collections[state.activeCollection]?.publish)
        record.intent = edit(record.intent)
        file.collections[state.activeCollection]?.publish = record
        try file.save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        state.reload()   // a popover open between the two files
    }

    private func markedValues(_ state: AppState) -> Set<String>? {
        state.collectionsCache.published[state.activeCollection]?.markedValues
    }

    func testASidecarThatDropsAMarkBeforeItsRowIsGoneCannotPublishThePath() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, file) = try publishMarkedLedger(h, state, args: [markedPath, "--quiet"])
        XCTAssertEqual(markedValues(state), [markedPath], "what this write replaced with a placeholder")
        let before = try Data(contentsOf: file)

        // The other machine deleted the marked row: its sidecar arrives first, without the mark.
        try sidecarLandsFirst(h, state) { $0.replacingPathMarks(of: "ledger", with: [:]) }
        XCTAssertEqual(state.publishError?.message, AppState.keptPathCarriedError("ledger", FieldName.argument(1)),
                       "nothing moved: the path is kept back where it sits")
        XCTAssertEqual(try Data(contentsOf: file), before, "the path is still in the master list here, so nothing is written")
        XCTAssertFalse(try jsonFile(file, contains: markedPath))

        // Its master list follows, without the row: nothing left to keep back.
        try h.editStoreOnDisk { store in
            store.collections[store.activeCollection]?.mcps["ledger"] = MCPEntry(config: .object([
                "command": .string("node"), "args": .array([.string("--quiet")]),
            ]))
        }
        state.reload(trigger: .externalStoreAdoption)
        XCTAssertNil(state.publishError)
        XCTAssertEqual(try ledgerArgs(in: file), ["--quiet"])
        XCTAssertEqual(markedValues(state), [markedPath], "a publish nobody reviewed never forgets a path")
    }

    func testASidecarThatDropsAConnectorBeforeItIsGoneCannotPublishItsPath() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, file) = try publishMarkedLedger(h, state, args: [markedPath])
        let before = try Data(contentsOf: file)
        try sidecarLandsFirst(h, state) { $0.movingConnector("ledger", to: nil) }
        XCTAssertEqual(state.publishError?.message, AppState.keptPathCarriedError("ledger", FieldName.argument(1)))
        XCTAssertEqual(try Data(contentsOf: file), before)
        XCTAssertFalse(try jsonFile(file, contains: markedPath))
    }

    /// Removed on the other machine while this one was off: at launch Claude's config brings the
    /// connector back, and the sidecar no longer marks it. The list outlives the launch.
    func testAConnectorBroughtBackAtLaunchWithoutItsMarkIsNotPublished() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let first = h.create()
        let (_, file) = try publishMarkedLedger(h, first, args: [markedPath])
        first.apply()
        XCTAssertNotNil(try h.claudeServers()["ledger"], "Claude runs it, which is how it comes back")
        let before = try Data(contentsOf: file)
        var sidecar = first.collectionsFile
        var record = try XCTUnwrap(sidecar.collections[first.activeCollection]?.publish)
        record.intent = record.intent.movingConnector("ledger", to: nil)
        sidecar.collections[first.activeCollection]?.publish = record
        first.dispose()
        try h.editStoreOnDisk { store in
            store.collections[store.activeCollection]?.mcps.removeValue(forKey: "ledger")
        }
        try sidecar.save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)

        let relaunched = h.create()
        defer { relaunched.dispose() }
        XCTAssertNotNil(relaunched.store.collections[relaunched.activeCollection]?.mcps["ledger"],
                        "Claude's config brought it back")
        XCTAssertEqual(relaunched.publishError?.message, AppState.keptPathCarriedError("ledger", FieldName.argument(1)))
        XCTAssertEqual(try Data(contentsOf: file), before)
    }

    func testAMarkedPathInsideAnotherStringIsNotPublished() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, file) = try publishMarkedLedger(h, state, args: [markedPath])
        let before = try Data(contentsOf: file)
        // Still marked where it was, and now also inside a flag nothing marks.
        rewriteLedger(state, args: [markedPath, "--script=\(markedPath)"])
        XCTAssertEqual(state.publishError?.message, AppState.keptPathCarriedError("ledger", FieldName.argument(2)))
        XCTAssertEqual(try Data(contentsOf: file), before)
    }

    func testOnlyTheSheetsPublishTakesAPathOffTheList() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, file) = try publishMarkedLedger(h, state, args: [markedPath])
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        let row = try XCTUnwrap(sheet.pathRows.firstIndex { $0.value == markedPath })
        XCTAssertTrue(sheet.pathRows[row].marked)
        // Unticked, the path is listed as kept back and holds Publish, until the author releases it
        // after reading the preview: that is the reviewed answer.
        sheet.pathRows[row].marked = false
        XCTAssertTrue(jsonText(sheet.preview, contains: markedPath), "the preview shows the path as it will travel")
        XCTAssertEqual(sheet.keptPaths.map { "\($0.connector) \($0.field)" }, ["ledger local.args[0]"])
        XCTAssertFalse(sheet.canPublish)
        XCTAssertEqual(sheet.publish(), PublishModel.keptPathNote("ledger", FieldName.argument(1)))
        sheet.releaseKeptPath(markedPath)
        XCTAssertTrue(sheet.canPublish)
        XCTAssertNil(sheet.publish())
        XCTAssertNil(state.publishError)
        XCTAssertEqual(markedValues(state), [])
        XCTAssertEqual(try ledgerArgs(in: file), [markedPath])
    }

    func testTheSheetsExportRefusesACopyOfAPathItMarks() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(markedPath), .string("--script=\(markedPath)")]),
        ])), renamedFrom: nil))
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        let row = try XCTUnwrap(sheet.pathRows.firstIndex { $0.value == markedPath })
        sheet.pathRows[row].marked = true
        XCTAssertEqual(sheet.keptPaths.map { "\($0.connector) \($0.field)" }, ["ledger local.args[1]"])
        XCTAssertFalse(sheet.canExport)
        let out = h.dir.file("away/copy.json")
        XCTAssertEqual(sheet.export(to: out.path), PublishModel.keptPathNote("ledger", FieldName.argument(2)))
        XCTAssertFalse(FileManager.default.fileExists(atPath: out.path))
        XCTAssertTrue(jsonText(sheet.preview, contains: "--script=\(markedPath)"), "the preview shows where it sits")
    }

    /// Renamed on the other machine while this one was off, the rename carrying the marks along;
    /// at launch Claude's config brings the old name back from this machine's own last apply.
    func testAConnectorRenamedElsewhereWhileOffIsNotPublishedUnderItsOldName() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let first = h.create()
        let (_, file) = try publishMarkedLedger(h, first, args: [markedPath])
        first.apply()
        let before = try Data(contentsOf: file)
        var sidecar = first.collectionsFile
        var record = try XCTUnwrap(sidecar.collections[first.activeCollection]?.publish)
        record.intent = record.intent.movingConnector("ledger", to: "books")
        sidecar.collections[first.activeCollection]?.publish = record
        first.dispose()
        try h.editStoreOnDisk { store in
            let entry = try XCTUnwrap(store.collections[store.activeCollection]?.mcps.removeValue(forKey: "ledger"))
            store.collections[store.activeCollection]?.mcps["books"] = entry
        }
        try sidecar.save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)

        let relaunched = h.create()
        defer { relaunched.dispose() }
        XCTAssertNotNil(relaunched.store.collections[relaunched.activeCollection]?.mcps["ledger"], "the old name came back")
        XCTAssertEqual(relaunched.publishError?.message, AppState.keptPathCarriedError("ledger", FieldName.argument(1)))
        XCTAssertEqual(relaunched.publishError?.kind, .blockedForReview)
        XCTAssertEqual(try Data(contentsOf: file), before)
    }

    func testAMarkedPathCarriedInAnyOtherFieldIsNotPublished() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, file) = try publishMarkedLedger(h, state, args: [markedPath])
        let before = try Data(contentsOf: file)
        let remote = RemotePattern.encode(RemoteConfig(url: "https://mcp.example.com/", auth: .automatic,
                                                       extraArgs: ["--config", markedPath], passthroughEnv: [:], package: "mcp-remote"))
        let carriers: [(name: String, field: String, config: JSONValue)] = [
            ("in the command", FieldName.command, .object(["command": .string(markedPath + "/bin/start"), "args": .array([])])),
            ("in a remote's arguments", FieldName.argument(5), remote),
            ("in an additional field", FieldName.document("additional.cwd"), .object(["command": .string("node"), "args": .array([.string("x.js")]),
                                                                  "cwd": .string(markedPath)])),
        ]
        for carrier in carriers {
            XCTAssertNil(state.upsert(name: carrier.name, entry: MCPEntry(config: carrier.config), renamedFrom: nil))
            XCTAssertEqual(state.publishError?.message, AppState.keptPathCarriedError(carrier.name, carrier.field), carrier.name)
            XCTAssertEqual(state.publishError?.kind, .blockedForReview, carrier.name)
            XCTAssertEqual(try Data(contentsOf: file), before, carrier.name)
            state.delete(names: [carrier.name])
            XCTAssertNil(state.publishError, "with it gone there is nothing left to keep back")
        }
    }

    /// What a refusal and the Publish sheet call a field. Wherever the editor opens the connector
    /// in the local form they use its own words — the command, an argument counted from one, the
    /// value of a variable — and a hint belongs to the sheet whatever the form. Where the editor
    /// has no row of its own, the document's name is given as the document's, rather than one the
    /// author would go looking for and not find.
    func testARefusalAndTheSheetNameAFieldTheWayTheEditorShowsIt() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, file) = try publishMarkedLedger(h, state, args: [markedPath])
        let before = try Data(contentsOf: file)
        // An argument: the document counts from zero and the editor's rows from one.
        XCTAssertNil(state.upsert(name: "carrier", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string("--serve"), .string(markedPath)]),
        ])), renamedFrom: nil))
        XCTAssertEqual(state.publishError?.message, AppState.keptPathCarriedError("carrier", FieldName.argument(2)))
        XCTAssertEqual(state.publishError?.kind, .blockedForReview)

        // Everywhere else, where the sheet lists the path with a note of its own. An argument row
        // is answered by ticking it, so it is an entry of the sheet's rows rather than this list.
        XCTAssertNil(state.upsert(name: "carrier", entry: MCPEntry(config: .object([
            "command": .string(markedPath), "args": .array([.string("x.js")]),
            "env": .object(["LEDGER": .string(markedPath)]), "cwd": .string(markedPath),
        ])), renamedFrom: "carrier"))
        XCTAssertEqual(state.publishError?.message,
                       AppState.keptPathCarriedError("carrier", FieldName.document("additional.cwd")),
                       "the first place the walk reaches, and one no form of the editor shows")
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        sheet.envRows[try XCTUnwrap(sheet.envRows.firstIndex { $0.connector == "carrier" && $0.name == "LEDGER" })].share = true
        sheet.pathRows[try XCTUnwrap(sheet.pathRows.firstIndex { $0.connector == "ledger" })].hint = "like mine, \(markedPath)"
        let named: [(entry: String, field: String)] = [
            ("carrier additional.cwd", FieldName.document("additional.cwd")),
            ("carrier env.LEDGER.value", FieldName.envValue("LEDGER")),
            ("carrier local.command", FieldName.command),
            ("ledger needs.server_path.hint", FieldName.hint("server_path")),
        ]
        XCTAssertEqual(sheet.keptPaths.map { "\($0.connector) \($0.field)" }, named.map(\.entry))
        for (kept, want) in zip(sheet.keptPaths, named) {
            XCTAssertEqual(sheet.note(for: kept), PublishModel.keptPathNote(kept.connector, want.field), want.entry)
        }
        XCTAssertFalse(sheet.canPublish)
        XCTAssertEqual(sheet.publish(), PublishModel.keptPathNote("carrier", FieldName.document("additional.cwd")))
        XCTAssertEqual(try Data(contentsOf: file), before)
        XCTAssertFalse(try jsonFile(file, contains: markedPath))
    }

    // MARK: - Across collections

    /// Team (active, published) runs `ledger` with a marked path and `x` with the token; Clients,
    /// published too, runs `crm` beside the harness's connectors. Claude's file holds Team's.
    private func twoPublishedCollections(_ h: AppStateHarness, _ s: AppState) throws -> (team: String, teamFolder: String, clientsDoc: URL) {
        let team = s.activeCollection
        XCTAssertNil(s.upsert(name: "ledger", entry: MCPEntry(enabled: true, config: .object([
            "command": .string("node"), "args": .array([.string(markedPath)])])), renamedFrom: nil))
        XCTAssertNil(s.upsert(name: "x", entry: MCPEntry(enabled: true, config: .object([
            "command": .string("node"), "args": .array([.string("\(Placeholder.directoryToken)/tools/srv.js")])])), renamedFrom: nil))
        XCTAssertNil(s.startPublishing(team, to: try publishFolder(h, "pubTeam").path, intent: PublishIntent(
            shareValues: [:], pathMarks: ["ledger": [JSONPointer(["args", "0"]): .init(name: "server_path", hint: nil, value: markedPath)]],
            hints: [:]), reviewedValues: [markedPath]))
        XCTAssertNil(s.createCollection(named: "Clients"))
        s.delete(names: ["ledger"], in: "Clients")
        s.delete(names: ["x"], in: "Clients")
        XCTAssertNil(s.upsert(name: "crm", entry: MCPEntry(enabled: true, config: .object(["command": .string("crm-mcp")])),
                              renamedFrom: nil, in: "Clients"))
        let clients = try publishFolder(h, "pubClients")
        XCTAssertNil(s.startPublishing("Clients", to: clients.path, intent: .none, reviewedValues: []))
        s.switchCollection(to: team)
        let teamFolder = try XCTUnwrap(s.collectionsCache.published[team]?.folder)
        return (team, teamFolder, clients.appendingPathComponent(Slug.make("Clients") + ".json"))
    }

    /// The other machine made Clients active while this one was off. Claude's file still holds
    /// Team's connectors, and they are not Clients' to take in: nothing is ingested, and the active
    /// collection is applied over the file.
    func testARelaunchAfterTheActiveCollectionChangedElsewhereTakesNothingFromClaudesFile() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let first = h.create()
        let (team, teamFolder, clientsDoc) = try twoPublishedCollections(h, first)
        XCTAssertEqual(first.collectionsCache.lastAppliedCollection, team, "every apply records what Claude's file holds")
        first.dispose()
        try h.editStoreOnDisk { store in
            store.activeCollection = "Clients"
        }

        let relaunched = h.create()
        defer { relaunched.dispose() }
        XCTAssertNil(relaunched.store.collections["Clients"]?.mcps["ledger"])
        XCTAssertNil(relaunched.store.collections["Clients"]?.mcps["x"])
        XCTAssertFalse(try jsonFile(clientsDoc, contains: markedPath))
        XCTAssertFalse(try jsonFile(clientsDoc, contains: teamFolder))
        XCTAssertNil(try h.claudeServers()["ledger"], "Claude's file was written from the active collection")
        XCTAssertEqual(relaunched.collectionsCache.lastAppliedCollection, "Clients")
        XCTAssertNil(relaunched.publishError)
    }

    /// A file restored with no record of its collection goes into the active one, as a restore
    /// always did; the other collection's marked path and folder are still kept out of its document.
    func testARestoreWithNoRecordedCollectionStillKeepsAnotherCollectionsPathsBack() throws {
        let (h, s) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, teamFolder, clientsDoc) = try twoPublishedCollections(h, s)
        let before = try Data(contentsOf: clientsDoc)
        let backup = h.dir.file("backup.json")
        try FileManager.default.copyItem(at: h.claudeConfigURL, to: backup)
        s.switchCollection(to: "Clients")
        try s.restoreClaudeConfig(from: backup)
        XCTAssertEqual(s.activeCollection, "Clients")
        XCTAssertEqual(s.publishError?.collection, "Clients")
        XCTAssertEqual(s.publishError?.message, AppState.keptPathCarriedError("ledger", FieldName.argument(1)))
        XCTAssertEqual(s.publishError?.kind, .blockedForReview)
        XCTAssertEqual(try Data(contentsOf: clientsDoc), before)
        XCTAssertFalse(try jsonFile(clientsDoc, contains: markedPath))
        XCTAssertFalse(try jsonFile(clientsDoc, contains: teamFolder))
    }

    /// A backup records the collection Claude's file held, and its restore goes back there, which
    /// becomes the active collection again: never into whichever collection is active now.
    func testARestoreGoesBackIntoTheCollectionItsBackupWasTakenFrom() throws {
        let (h, s) = AppStateHarness.started()
        defer { h.dispose() }
        let (team, teamFolder, clientsDoc) = try twoPublishedCollections(h, s)
        s.switchCollection(to: "Clients")   // backs up Team's file first, recorded as Team's
        let backup = try XCTUnwrap(try s.service.backups.backups(series: "claude_desktop_config").first)
        XCTAssertEqual(BackupCollections.collection(of: backup, in: h.backupsDir), team)
        let clientsBefore = try Data(contentsOf: clientsDoc)

        try s.restoreClaudeConfig(from: backup)
        XCTAssertEqual(s.activeCollection, team)
        XCTAssertNil(s.store.collections["Clients"]?.mcps["ledger"], "nothing of Team's went into Clients")
        XCTAssertEqual(args(of: try XCTUnwrap(s.store.collections[team]?.mcps["x"]?.config)),
                       ["\(Placeholder.directoryToken)/tools/srv.js"], "Team keeps its token")
        XCTAssertEqual(try Data(contentsOf: clientsDoc), clientsBefore)
        XCTAssertFalse(try jsonFile(clientsDoc, contains: teamFolder))
        XCTAssertNil(s.publishError)
        XCTAssertEqual(s.collectionsCache.lastAppliedCollection, team)
    }

    func testARestoreOfABackupWhoseCollectionIsGoneIsRefused() throws {
        let (h, s) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(s.createCollection(named: "Gone"))
        // A collection of its own content: a backup identical to the newest belongs to whichever
        // collection last wrote it.
        XCTAssertNil(s.upsert(name: "gone-only", entry: MCPEntry(enabled: true, config: .object(["command": .string("g")])),
                              renamedFrom: nil, in: "Gone"))
        s.apply()
        s.switchCollection(to: "Default")   // backs up what Gone put in Claude's file, recorded as Gone's
        let backup = try XCTUnwrap(try s.service.backups.backups(series: "claude_desktop_config").first)
        XCTAssertEqual(BackupCollections.collection(of: backup, in: h.backupsDir), "Gone")
        XCTAssertNil(s.deleteCollection(named: "Gone"))
        let claudeBefore = try Data(contentsOf: h.claudeConfigURL)
        let storeBefore = s.store
        XCTAssertThrowsError(try s.restoreClaudeConfig(from: backup)) {
            XCTAssertEqual($0 as? RestoreError, .collectionGone("Gone"))
            XCTAssertEqual($0.localizedDescription, AppState.restoreCollectionGoneError("Gone"))
        }
        XCTAssertEqual(try Data(contentsOf: h.claudeConfigURL), claudeBefore, "nothing was restored")
        XCTAssertEqual(s.store, storeBefore)

        // The way back the message names: a collection of that name again, and the same backup goes in.
        XCTAssertTrue(AppState.restoreCollectionGoneError("Gone").contains("Create a collection named “Gone”"))
        XCTAssertNil(s.createCollection(named: "Gone"))
        try s.restoreClaudeConfig(from: backup)
        XCTAssertEqual(s.activeCollection, "Gone")
        XCTAssertNotNil(s.store.collections["Gone"]?.mcps["gone-only"])
    }

    /// A connector an installer wrote straight into Claude's config while the app was off, and the
    /// other machine switched collections meanwhile: the collection that was applied keeps its own,
    /// and the new name still comes in to the collection now active.
    func testALaunchAfterTheActiveCollectionChangedStillTakesInWhatIsNew() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let first = h.create()
        let team = first.activeCollection
        XCTAssertNil(first.upsert(name: "a", entry: MCPEntry(enabled: true, config: .object(["command": .string("a")])),
                                  renamedFrom: nil))
        XCTAssertNil(first.createCollection(named: "Second"))
        first.delete(names: ["a"], in: "Second")
        first.switchCollection(to: team)
        first.dispose()
        try h.editStoreOnDisk { store in
            store.activeCollection = "Second"
        }
        var servers = try h.claudeServers()
        servers["installer"] = .object(["command": .string("node"), "args": .array([.string("/opt/installer/srv.js")])])
        try h.writeClaudeServers(servers.map { ($0.key, $0.value) })

        let relaunched = h.create()
        defer { relaunched.dispose() }
        XCTAssertNotNil(relaunched.store.collections["Second"]?.mcps["installer"], "the hand-added connector came in")
        XCTAssertNotNil(try h.claudeServers()["installer"], "and Claude still runs it")
        XCTAssertNil(relaunched.store.collections["Second"]?.mcps["a"], "what the applied collection renders stays there")
        XCTAssertNil(relaunched.store.collections[team]?.mcps["installer"])
    }

    /// The collection Claude's file was last applied from has been deleted meanwhile, here or on
    /// the other machine. It renders nothing to leave alone, so the names the last apply wrote
    /// stand in for its render: those stay where they are and everything else comes in, which
    /// keeps the connector an installer wrote into the file.
    func testALaunchIngestKeepsWhatIsNewWhenTheCollectionItAppliedIsGone() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let first = h.create()
        let home = first.activeCollection
        XCTAssertNil(first.createCollection(named: "Second"))
        first.switchCollection(to: home)
        first.dispose()
        // The record names the collection Claude's file came from. The store syncs and this
        // machine's cache does not, so the collection can be gone from one and named by the other.
        let cacheURL = h.storeDir.appendingPathComponent(CollectionsLocalCache.fileName)
        var cache = CollectionsLocalCache.load(from: cacheURL)
        cache.lastAppliedCollection = "Second"
        try cache.save(to: cacheURL, staging: nil)
        try h.editStoreOnDisk { store in
            store.collections.removeValue(forKey: "Second")
            store.activeCollection = home
        }
        var servers = try h.claudeServers()
        servers["installer"] = .object(["command": .string("node"), "args": .array([.string("/opt/installer/srv.js")])])
        try h.writeClaudeServers(servers.map { ($0.key, $0.value) })

        let relaunched = h.create()
        defer { relaunched.dispose() }
        XCTAssertNotNil(relaunched.store.collections[home]?.mcps["installer"], "the hand-added connector came in")
        XCTAssertNotNil(try h.claudeServers()["installer"], "and Claude still runs it")
    }

    /// The same launch for a user who publishes nothing: what the collection that is gone rendered
    /// is not poured into the active one, which is the whole reason the names are recorded.
    func testALaunchIngestLeavesTheDeletedCollectionsOwnConnectorsAlone() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let first = h.create()
        let home = first.activeCollection
        XCTAssertNil(first.createCollection(named: "Team"))   // Team is active, so Claude's file holds Team
        XCTAssertNil(first.upsert(name: "t1", entry: MCPEntry(enabled: true, config: .object(["command": .string("t1")])),
                                  renamedFrom: nil, in: "Team"))
        first.apply()   // Claude's file now holds t1, and the record says Team wrote it
        XCTAssertNotNil(try h.claudeServers()["t1"])
        let before = try XCTUnwrap(first.store.collections[home]?.mcps.keys).sorted()
        first.dispose()
        // The other machine deletes Team. Claude's file still holds what Team rendered, and one
        // connector an installer wrote beside it while the app was off.
        try h.editStoreOnDisk { store in
            store.collections.removeValue(forKey: "Team")
            store.activeCollection = home
        }
        var servers = try h.claudeServers()
        servers["installer"] = .object(["command": .string("node"), "args": .array([.string("/opt/installer/srv.js")])])
        try h.writeClaudeServers(servers.map { ($0.key, $0.value) })

        let relaunched = h.create()
        defer { relaunched.dispose() }
        XCTAssertEqual(try XCTUnwrap(relaunched.store.collections[home]?.mcps.keys).sorted(),
                       (before + ["installer"]).sorted(),
                       "Team's own connectors stayed out, and the new one came in")
        XCTAssertNil(relaunched.store.collections[home]?.mcps["t1"])
    }

    /// The same launch where this machine publishes: the collection that is gone was published from
    /// the author's other machine with a path marked, so its connector reaching the collection
    /// published here would send that path as written. It is not taken in, and nothing is written.
    func testALaunchIngestDoesNotPublishADeletedCollectionsMarkedPath() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let first = h.create()
        XCTAssertNil(first.createCollection(named: "Team"))   // Team is active, so Claude's file holds Team
        XCTAssertNil(first.upsert(name: "ledger", entry: MCPEntry(enabled: true, config: .object([
            "command": .string("node"), "args": .array([.string(markedPath)]),
        ])), renamedFrom: nil, in: "Team"))
        first.apply()
        // Team is published from the author's other machine: the sidecar carries the mark and its value.
        var file = first.collectionsFile
        file.collections["Team"] = CollectionsFile.Entry(kind: .local, publish: CollectionsFile.PublishRecord(
            slug: "team", origin: "team-origin", intent: PublishIntent(
                shareValues: [:], pathMarks: ["ledger": [JSONPointer(["args", "0"]):
                    .init(name: "server_path", hint: nil, value: markedPath)]], hints: [:])))
        try file.save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        first.reload()
        let folder = try publishFolder(h, "pubDefault")
        XCTAssertNil(first.startPublishing("Default", to: folder.path, intent: .none, reviewedValues: []))
        let document = folder.appendingPathComponent(Slug.make("Default") + ".json")
        first.dispose()

        // The other machine deletes Team while this one is off, so nothing here records its marks
        // any more: the store, the sidecar and the binding all arrive without it.
        try h.editStoreOnDisk { store in
            store.collections.removeValue(forKey: "Team")
            store.activeCollection = "Default"
        }
        var after = CollectionsFile.load(from: h.storeDir.appendingPathComponent(CollectionsFile.fileName))
        after.collections.removeValue(forKey: "Team")
        try after.save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)

        let relaunched = h.create()
        defer { relaunched.dispose() }
        XCTAssertNil(relaunched.store.collections["Default"]?.mcps["ledger"],
                     "the deleted collection's own connector is not poured into the collection this machine publishes")
        XCTAssertNil(relaunched.publishError)
        XCTAssertFalse(try jsonFile(document, contains: markedPath))
    }

    /// Renaming a collection carries what names it outside the store: the record of what Claude's
    /// file holds, and every backup taken from it, which still restores into it under the new name.
    func testARenameCarriesTheRecordsOfWhereClaudesFileCameFrom() throws {
        let (h, s) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(s.createCollection(named: "Team"))
        XCTAssertNil(s.upsert(name: "team-only", entry: MCPEntry(enabled: true, config: .object(["command": .string("t")])),
                              renamedFrom: nil, in: "Team"))
        s.apply()
        s.switchCollection(to: "Default")   // backs up what Team put in Claude's file
        let backup = try XCTUnwrap(try s.service.backups.backups(series: "claude_desktop_config").first)
        XCTAssertNil(s.renameCollection("Team", to: "Team A"))
        XCTAssertEqual(BackupCollections.collection(of: backup, in: h.backupsDir), "Team A")
        XCTAssertNil(s.renameCollection("Default", to: "Main"))
        XCTAssertEqual(s.collectionsCache.lastAppliedCollection, "Main")
        try s.restoreClaudeConfig(from: backup)
        XCTAssertEqual(s.activeCollection, "Team A")
        XCTAssertNotNil(s.store.collections["Team A"]?.mcps["team-only"])
    }

    /// An own folder where the rewrite cannot reach — a remote connector's client id, which the
    /// command line carries inside a JSON blob — is a folder entry all the same: it says the sheet
    /// cannot write there, both answers say so, and the connector's editor is the way out.
    func testAnOwnFolderTheSheetCannotRewriteSaysWhereToWriteTheToken() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        let bound = try XCTUnwrap(state.collectionsCache.published[state.activeCollection]?.folder)
        let file = folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json")
        let before = try Data(contentsOf: file)
        XCTAssertNil(state.upsert(name: "svc", entry: MCPEntry(config: RemotePattern.encode(RemoteConfig(
            url: "https://mcp.example.com/", auth: .oauthClient(clientID: bound, clientSecret: "s", scopes: ""),
            extraArgs: [], passthroughEnv: [:], package: "mcp-remote"))), renamedFrom: nil))
        XCTAssertEqual(state.publishError?.message, AppState.publishFolderCarriedError("svc", FieldName.argument(5)))

        let sheet = PublishModel(state: state, collection: state.activeCollection)
        let kept = try XCTUnwrap(sheet.keptPaths.first)
        XCTAssertEqual(kept.kind, .folder, "a folder of this collection's own, wherever it sits")
        // The editor opens this connector in the local form, so the note names the argument the
        // author sees rather than the document's remote.auth.clientId.
        let note = PublishModel.publishFolderEditNote("svc", FieldName.argument(5))
        XCTAssertEqual(sheet.note(for: kept), note)
        XCTAssertEqual(sheet.useDirectoryToken(kept), note, "the sheet says it did nothing, and what does answer it")
        XCTAssertEqual(sheet.releaseKeptPath(kept.value), note)
        XCTAssertFalse(sheet.canPublish)
        XCTAssertEqual(sheet.publish(), note)
        XCTAssertEqual(try Data(contentsOf: file), before)
        XCTAssertFalse(try jsonFile(file, contains: bound))

        // The editor is the way out, and taking it clears the block.
        let editor = EditorModel(state: state, target: .existing(name: "svc", entry: try XCTUnwrap(state.store.mcps["svc"]),
                                                                 in: state.activeCollection), dialogs: h.dialogs)
        // The command line carries it JSON-escaped, which is why the sheet cannot write over it and
        // the author rewrites the argument itself.
        let carrying = try XCTUnwrap(editor.args.firstIndex { KeptValue.holds($0.value, bound) })
        editor.args[carrying].value = #"{"client_id":"${COLLECTION_DIR}","client_secret":"s"}"#
        XCTAssertTrue(editor.save())
        XCTAssertNil(state.publishError)
        XCTAssertFalse(try jsonFile(file, contains: bound))
        XCTAssertTrue(PublishModel(state: state, collection: state.activeCollection).canPublish)
    }

    /// Stop Publishing gives up the folder, not this machine's memory: the paths it kept back and
    /// the folders it published into still hold when the collection is published again.
    func testStopPublishingKeepsWhatMustNotTravel() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, firstDocument) = try publishMarkedLedger(h, state, args: [markedPath])
        let oldFolder = try XCTUnwrap(state.collectionsCache.published[state.activeCollection]?.folder)
        state.stopPublishing(state.activeCollection, deleteFile: true)
        XCTAssertNil(state.collectionsCache.published[state.activeCollection])
        XCTAssertEqual(state.collectionsCache.kept[state.activeCollection]?.markedValues, [markedPath])
        XCTAssertEqual(state.collectionsCache.kept[state.activeCollection]?.publishedFolders, [oldFolder])
        XCTAssertEqual(CollectionsLocalCache.load(from: h.storeDir.appendingPathComponent(CollectionsLocalCache.fileName))
                        .kept[state.activeCollection]?.markedValues, [markedPath], "and it is on disk")

        // Published again, into another folder: the mark is still this machine's to keep back, and
        // so is the folder the collection has left.
        rewriteLedger(state, args: [markedPath, "--root", oldFolder])
        let second = try publishFolder(h, "again")
        XCTAssertEqual(state.startPublishing(state.activeCollection, to: second.path, intent: .none),
                       AppState.keptPathCarriedError("ledger", FieldName.argument(1)))
        let document = second.appendingPathComponent(Slug.make(state.activeCollection) + ".json")
        XCTAssertFalse(FileManager.default.fileExists(atPath: document.path))
        XCTAssertEqual(state.collectionsCache.published[state.activeCollection]?.markedValues, [markedPath],
                       "the binding took the list back")
        XCTAssertTrue(state.collectionsCache.published[state.activeCollection]?.publishedFolders.contains(oldFolder) ?? false)
        XCTAssertNil(state.collectionsCache.kept[state.activeCollection], "and the record is spent")
        XCTAssertFalse(FileManager.default.fileExists(atPath: firstDocument.path), "Stop Publishing took the old document")
    }

    /// Deleting a published collection gives up the binding, and keeps what Stop Publishing keeps:
    /// deleting it is not the author's word that its paths may travel, and the connector that
    /// carried one is still in another collection, or comes back by a copy, an import or an ingest.
    func testDeletingAPublishedCollectionKeepsWhatItKeptBack() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let home = state.activeCollection
        XCTAssertNil(state.createCollection(named: "Team"))          // active, a copy of home
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(markedPath)]),
        ])), renamedFrom: nil, in: "Team"))
        let teamFolder = try publishFolder(h, "pubTeam")
        XCTAssertNil(state.startPublishing("Team", to: teamFolder.path, intent: PublishIntent(
            shareValues: [:], pathMarks: ["ledger": [JSONPointer(["args", "0"]):
                .init(name: "server_path", hint: nil, value: markedPath)]], hints: [:]), reviewedValues: [markedPath]))
        let teamBound = try XCTUnwrap(state.collectionsCache.published["Team"]?.folder)
        state.switchCollection(to: home)
        XCTAssertNil(state.deleteCollection(named: "Team"))
        XCTAssertEqual(state.collectionsCache.kept["Team"]?.markedValues, [markedPath])
        XCTAssertEqual(state.collectionsCache.kept["Team"]?.publishedFolders, [teamBound])
        XCTAssertEqual(CollectionsLocalCache.load(from: h.storeDir.appendingPathComponent(CollectionsLocalCache.fileName))
                        .kept["Team"]?.markedValues, [markedPath], "and it is on disk")

        // home publishes already, so the connector moving across is an ordinary save: no sheet,
        // no preview, and the only thing between the path and the document is the union.
        let folder = try publishFolder(h, "pubHome")
        XCTAssertNil(state.startPublishing(home, to: folder.path, intent: .none, reviewedValues: []))
        let document = folder.appendingPathComponent(Slug.make(home) + ".json")
        let before = try Data(contentsOf: document)
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(markedPath)]),
        ])), renamedFrom: nil, in: home))
        XCTAssertEqual(state.publishError?.message, AppState.keptPathCarriedError("ledger", FieldName.argument(1)))
        XCTAssertEqual(state.publishError?.kind, .blockedForReview)
        XCTAssertEqual(try Data(contentsOf: document), before, "the document in the folder is left as it was")
        XCTAssertFalse(try jsonFile(document, contains: markedPath))
    }

    /// A published collection deleted, or stopped, on the author's other machine arrives as a
    /// store and a sidecar without it. Its binding goes with them, and what it kept back does not:
    /// the load that drops the binding leaves the same record a delete made here leaves.
    func testAPublishedCollectionDeletedOnAnotherMachineKeepsWhatItKeptBack() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let home = state.activeCollection
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(markedPath)]),
        ])), renamedFrom: nil, in: "Team"))
        let teamFolder = try publishFolder(h, "pubTeam")
        XCTAssertNil(state.startPublishing("Team", to: teamFolder.path, intent: PublishIntent(
            shareValues: [:], pathMarks: ["ledger": [JSONPointer(["args", "0"]):
                .init(name: "server_path", hint: nil, value: markedPath)]], hints: [:]), reviewedValues: [markedPath]))
        state.switchCollection(to: home)
        let folder = try publishFolder(h, "pubHome")
        XCTAssertNil(state.startPublishing(home, to: folder.path, intent: .none, reviewedValues: []))
        let document = folder.appendingPathComponent(Slug.make(home) + ".json")
        XCTAssertTrue(state.keptBack(for: home).values.contains(markedPath), "the mark is known before the delete")

        // The other machine deletes Team: the master list and the sidecar arrive without it.
        try h.editStoreOnDisk { store in
            store.collections.removeValue(forKey: "Team")
            store.activeCollection = home
        }
        var file = state.collectionsFile
        file.collections.removeValue(forKey: "Team")
        try file.save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        state.reload()
        XCTAssertNil(state.collectionsCache.published["Team"], "the sidecar no longer vouches for the binding")
        XCTAssertEqual(state.collectionsCache.kept["Team"]?.markedValues, [markedPath], "and what it kept back stayed")

        let before = try Data(contentsOf: document)
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(markedPath)]),
        ])), renamedFrom: nil, in: home))
        XCTAssertEqual(state.publishError?.message, AppState.keptPathCarriedError("ledger", FieldName.argument(1)))
        XCTAssertEqual(state.publishError?.kind, .blockedForReview)
        XCTAssertEqual(try Data(contentsOf: document), before)
        XCTAssertFalse(try jsonFile(document, contains: markedPath))
    }

    /// A collection made with a deleted one's name is a different collection, and will publish
    /// under an origin of its own. The paths the old one kept back are still the author's, and
    /// still refused; its folders are another collection's here, released rather than written over,
    /// since ${COLLECTION_DIR} in this collection's document would stand for somewhere else.
    func testACollectionMadeWithADeletedOnesNameDoesNotInheritItsFolders() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let home = state.activeCollection
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(markedPath)]),
        ])), renamedFrom: nil, in: "Team"))
        let teamFolder = try publishFolder(h, "pubTeam")
        XCTAssertNil(state.startPublishing("Team", to: teamFolder.path, intent: PublishIntent(
            shareValues: [:], pathMarks: ["ledger": [JSONPointer(["args", "0"]):
                .init(name: "server_path", hint: nil, value: markedPath)]], hints: [:]), reviewedValues: [markedPath]))
        let bound = try XCTUnwrap(state.collectionsCache.published["Team"]?.folder)
        XCTAssertEqual(state.collectionsCache.published["Team"]?.origin,
                       state.collectionsFile.collections["Team"]?.publish?.origin,
                       "the binding carries the origin it publishes under, and every write keeps it")
        state.switchCollection(to: home)

        // Stopped, the collection is the same one: the folder it published into is still its own,
        // and ${COLLECTION_DIR} is the answer to a connector that carries it.
        state.stopPublishing("Team", deleteFile: false)
        XCTAssertTrue(state.keptBack(for: "Team").folders.contains(bound))

        XCTAssertNil(state.deleteCollection(named: "Team"))
        XCTAssertNil(state.createCollection(named: "Team"))   // the way back the refused restore names
        let kept = state.keptBack(for: "Team")
        XCTAssertFalse(kept.folders.contains(bound), "a different collection: never released is not the rule for it")
        XCTAssertTrue(kept.values.contains(bound), "it is still a folder this machine binds, and it is releasable")
        XCTAssertTrue(kept.values.contains(markedPath), "and the path the old one marked is still the author's")
    }

    /// Team published here with a path marked, then deleted: what every test below starts from.
    /// Returns the folder it published into.
    private func publishThenDeleteTeam(_ h: AppStateHarness, _ state: AppState) throws -> URL {
        let home = state.activeCollection
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(markedPath)]),
        ])), renamedFrom: nil, in: "Team"))
        let folder = try publishFolder(h, "pubTeam")
        XCTAssertNil(state.startPublishing("Team", to: folder.path, intent: PublishIntent(
            shareValues: [:], pathMarks: ["ledger": [JSONPointer(["args", "0"]):
                .init(name: "server_path", hint: nil, value: markedPath)]], hints: [:]), reviewedValues: [markedPath]))
        state.switchCollection(to: home)
        XCTAssertNil(state.deleteCollection(named: "Team"))
        return folder
    }

    /// A record belongs to the collection that published it, and that collection leaving the store
    /// is what ends the claim — wherever it leaves from. A collection of the same name arriving
    /// from the author's other machine is as much a different collection as one made here.
    func testACollectionOfTheSameNameArrivingFromAnotherMachineInheritsNoFolders() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishThenDeleteTeam(h, state)
        // The other machine makes a collection called Team again, and the store syncs here.
        try h.editStoreOnDisk { store in
            XCTAssertNil(store.addCollection(named: "Team", copyingCurrent: false))
            store.activeCollection = state.activeCollection
        }
        state.reload()
        XCTAssertNotNil(state.store.collections["Team"], "Team arrived")
        let kept = state.keptBack(for: "Team")
        XCTAssertFalse(kept.folders.contains(folder.path), "it never published there, so the token stands for nothing")
        XCTAssertTrue(kept.values.contains(folder.path), "it is a folder this machine binds, and releasable")
        XCTAssertTrue(kept.values.contains(markedPath), "and the path the old Team marked is still the author's")
    }

    /// A collection that only stopped publishing never left the store, so the folders it published
    /// into are still its own: the token stands for them, and publishing again takes them back.
    func testAStoppedCollectionKeepsItsOwnFoldersThroughTheNextPublish() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let home = state.activeCollection
        XCTAssertNil(state.startPublishing(home, to: try publishFolder(h, "pub1").path, intent: .none, reviewedValues: []))
        let old = try XCTUnwrap(state.collectionsCache.published[home]?.folder)
        state.stopPublishing(home, deleteFile: false)
        XCTAssertTrue(state.keptBack(for: home).folders.contains(old),
                      "it never left the store, so the folder it published into is still its own")
        let second = try publishFolder(h, "pub2")
        XCTAssertNil(state.startPublishing(home, to: second.path, intent: .none, reviewedValues: []))
        XCTAssertTrue(state.collectionsCache.published[home]?.publishedFolders.contains(old) ?? false,
                      "and the binding takes the folders back with the record")
        XCTAssertTrue(state.keptBack(for: home).folders.contains(old))
        XCTAssertNil(state.collectionsCache.kept[home], "the record is spent")
    }

    /// The release the sheet offers for a re-used name holds. A record whose collection has gone
    /// takes its folders with it, so a new collection of that name does not get them back through
    /// its own binding: the author's answer stands, the first publish writes, and every save after
    /// it is an ordinary one.
    func testAReleasedFolderStaysReleasedForACollectionMadeWithADeletedOnesName() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishThenDeleteTeam(h, state)
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.upsert(name: "tool", entry: MCPEntry(config: .object([
            "command": .string(folder.path + "/bin/tool"),
        ])), renamedFrom: nil, in: "Team"))
        let second = try publishFolder(h, "pubTeam2")
        let sheet = PublishModel(state: state, collection: "Team")
        sheet.folder = second.path
        let entry = try XCTUnwrap(sheet.keptPaths.first { $0.value == folder.path })
        XCTAssertEqual(entry.kind, .path, "the old Team's folder, not this one's")
        XCTAssertNil(sheet.releaseKeptPath(folder.path), "Release is the answer the sheet offers")
        XCTAssertTrue(sheet.canPublish)
        XCTAssertNil(sheet.publish(), "and publishing holds to it")
        let document = second.appendingPathComponent(Slug.make("Team") + ".json")
        XCTAssertTrue(try jsonFile(document, contains: folder.path), "released, it travels as written")
        XCTAssertFalse(state.collectionsCache.published["Team"]?.publishedFolders.contains(folder.path) ?? true,
                       "and the old folder is not this collection's own, so it is not refused again")
        XCTAssertTrue(state.collectionsCache.published["Team"]?.releasedValues.contains(folder.path) ?? false)

        // An ordinary save after it: the answer the author gave still stands, with no banner. Before
        // this round the folder came back as the new binding's own and every save failed from here.
        let before = try Data(contentsOf: document)
        XCTAssertNil(state.upsert(name: "other", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string("/tmp/other.js")]),
        ])), renamedFrom: nil, in: "Team"))
        XCTAssertNil(state.publishError)
        XCTAssertNotEqual(try Data(contentsOf: document), before, "and the save reached the folder")
    }

    /// A folder this machine published a collection into is its own to keep back for as long as
    /// the folder exists, whichever collection bears the name now. The record a deleted collection
    /// left belongs to none, so a new collection of its name publishing elsewhere leaves it where
    /// it is: the old folder is refused as a kept path, not taken as the new collection's own and
    /// not dropped with the record.
    func testADepartedCollectionsFolderIsStillKeptBackOnceItsNamePublishesAgain() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishThenDeleteTeam(h, state)
        XCTAssertNil(state.createCollection(named: "Team"))
        let second = try publishFolder(h, "pubTeam2")
        XCTAssertNil(state.startPublishing("Team", to: second.path, intent: .none, reviewedValues: []))
        let record = try XCTUnwrap(state.collectionsCache.kept["Team"], "the old Team's record outlives the new one's publish")
        XCTAssertEqual(record.publishedFolders, [folder.path])
        XCTAssertNil(record.origin, "and belongs to no collection")
        let kept = state.keptBack(for: "Team")
        XCTAssertTrue(kept.values.contains(folder.path), "a path this machine keeps back")
        XCTAssertFalse(kept.folders.contains(folder.path), "not a folder of the new Team's own")

        // The old folder comes back in a connector: an ingest, a restore or a hand edit. Before
        // this round the record went with the first publish, folders and all, and the folder
        // travelled with no banner.
        XCTAssertNil(state.upsert(name: "tool", entry: MCPEntry(config: .object([
            "command": .string(folder.path + "/bin/tool"),
        ])), renamedFrom: nil, in: "Team"))
        XCTAssertEqual(state.publishError?.kind, .blockedForReview)
        XCTAssertFalse(try jsonFile(second.appendingPathComponent(Slug.make("Team") + ".json"), contains: folder.path))
    }

    /// The release the sheet offers for a departed collection's folder holds through the new
    /// collection's own Stop and publish. Stopping merges the record it left into the new
    /// collection's, and the departed folder must not come out the other side as the new
    /// collection's own: it stays apart, still refused, still releasable, and released it travels.
    func testADepartedCollectionsFolderStaysReleasableAfterTheNameStopsAndPublishesAgain() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishThenDeleteTeam(h, state)
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.startPublishing("Team", to: try publishFolder(h, "pubTeam2").path, intent: .none, reviewedValues: []))
        state.stopPublishing("Team", deleteFile: false)
        let third = try publishFolder(h, "pubTeam3")
        XCTAssertNil(state.startPublishing("Team", to: third.path, intent: .none, reviewedValues: []))
        XCTAssertEqual(state.collectionsCache.kept["Team"]?.departedFolders, [folder.path],
                       "the record left behind holds the old Team's folder apart from the new one's")
        XCTAssertFalse(state.collectionsCache.published["Team"]?.publishedFolders.contains(folder.path) ?? true,
                       "and the binding did not take it")
        let kept = state.keptBack(for: "Team")
        XCTAssertTrue(kept.values.contains(folder.path))
        XCTAssertFalse(kept.folders.contains(folder.path))

        XCTAssertNil(state.upsert(name: "tool", entry: MCPEntry(config: .object([
            "command": .string(folder.path + "/bin/tool"),
        ])), renamedFrom: nil, in: "Team"))
        XCTAssertEqual(state.publishError?.kind, .blockedForReview)
        let sheet = PublishModel(state: state, collection: "Team")
        let entry = try XCTUnwrap(sheet.keptPaths.first { $0.value == folder.path })
        XCTAssertEqual(entry.kind, .path, "the old Team's folder, still releasable")
        XCTAssertNil(sheet.releaseKeptPath(folder.path))
        XCTAssertTrue(sheet.canPublish)
        XCTAssertNil(sheet.publish())
        let document = third.appendingPathComponent(Slug.make("Team") + ".json")
        XCTAssertTrue(try jsonFile(document, contains: folder.path), "released, it travels as written")
        XCTAssertNil(state.upsert(name: "other", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string("/tmp/other.js")]),
        ])), renamedFrom: nil, in: "Team"))
        XCTAssertNil(state.publishError, "and a later save is an ordinary one")
    }

    /// A rename lands on a name only a departed collection can have left a record under, the store
    /// refusing a live one's. That record is this machine's memory of the folder the departed
    /// collection published into, and the renamed collection's own record merges with it rather
    /// than writing over it: the folder is departed to the collection now bearing the name, and
    /// stays kept back from every document this machine publishes.
    func testARenameOntoADepartedCollectionsNameKeepsTheFolderItLeft() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let home = state.activeCollection
        // Squad published and stopped, so its record is its own; Team published and was deleted,
        // so its record is a departed collection's.
        XCTAssertNil(state.createCollection(named: "Squad"))
        let squadFolder = try publishFolder(h, "pubSquad")
        XCTAssertNil(state.startPublishing("Squad", to: squadFolder.path, intent: .none, reviewedValues: []))
        let squadOrigin = try XCTUnwrap(state.collectionsFile.collections["Squad"]?.publish?.origin)
        state.stopPublishing("Squad", deleteFile: false)
        state.switchCollection(to: home)
        let folder = try publishThenDeleteTeam(h, state)
        let homeFolder = try publishFolder(h, "pubHome")
        XCTAssertNil(state.startPublishing(home, to: homeFolder.path, intent: .none, reviewedValues: []))
        XCTAssertTrue(state.keptBack(for: home).values.contains(folder.path), "kept back before the rename")

        XCTAssertNil(state.renameCollection("Squad", to: "Team"))
        let record = try XCTUnwrap(state.collectionsCache.kept["Team"])
        XCTAssertEqual(record.publishedFolders, [squadFolder.path], "the renamed collection's own folder")
        XCTAssertEqual(record.origin, squadOrigin, "under the origin it published under")
        XCTAssertEqual(record.departedFolders, [folder.path], "and the old Team's folder, departed to it")
        XCTAssertNil(state.collectionsCache.kept["Squad"])
        XCTAssertTrue(state.keptBack(for: home).values.contains(folder.path), "and kept back after it")
        // The old folder comes back in a connector of the collection this machine publishes.
        // Before this round the rename wrote one record over the other, and the folder travelled.
        XCTAssertNil(state.upsert(name: "tool", entry: MCPEntry(config: .object([
            "command": .string(folder.path + "/bin/tool"),
        ])), renamedFrom: nil, in: home))
        XCTAssertEqual(state.publishError?.kind, .blockedForReview)
        XCTAssertFalse(try jsonFile(homeFolder.appendingPathComponent(Slug.make(home) + ".json"), contains: folder.path))
    }

    /// The merged record is the renamed collection's own: it published under the origin the
    /// record carries, so publishing again takes its folders back as its own, while the departed
    /// collection's folder stays what it was to it — a path kept back, releasable, never the
    /// token's.
    func testACollectionRenamedOntoADepartedNameStillOwnsItsFoldersWhenItPublishesAgain() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let home = state.activeCollection
        XCTAssertNil(state.createCollection(named: "Squad"))
        let squadFolder = try publishFolder(h, "pubSquad")
        XCTAssertNil(state.startPublishing("Squad", to: squadFolder.path, intent: .none, reviewedValues: []))
        state.stopPublishing("Squad", deleteFile: false)
        state.switchCollection(to: home)
        let folder = try publishThenDeleteTeam(h, state)
        XCTAssertNil(state.renameCollection("Squad", to: "Team"))

        XCTAssertNil(state.startPublishing("Team", to: try publishFolder(h, "pubTeam2").path, intent: .none, reviewedValues: []))
        XCTAssertTrue(state.collectionsCache.published["Team"]?.publishedFolders.contains(squadFolder.path) ?? false,
                      "the binding takes its own old folder back")
        let kept = state.keptBack(for: "Team")
        XCTAssertTrue(kept.folders.contains(squadFolder.path), "its own, which the token stands for")
        XCTAssertTrue(kept.values.contains(folder.path), "the departed collection's, a path kept back")
        XCTAssertFalse(kept.folders.contains(folder.path))
        XCTAssertEqual(state.collectionsCache.kept["Team"]?.departedFolders, [folder.path], "and it stays behind for the next Stop")
    }

    /// A collection the author publishes from their other machine marks its paths in the sidecar,
    /// which syncs with the master list. Those marks are this machine's to keep back too, so a copy
    /// of that connector reaching a collection published here is refused, with no binding involved.
    func testAPathMarkedForACollectionPublishedFromAnotherMachineIsKeptBackHere() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let home = state.activeCollection
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(markedPath)]),
        ])), renamedFrom: nil, in: "Team"))
        state.switchCollection(to: home)
        var file = state.collectionsFile
        file.collections["Team"] = CollectionsFile.Entry(kind: .local, publish: CollectionsFile.PublishRecord(
            slug: "team", origin: "team-origin", intent: PublishIntent(
                shareValues: [:], pathMarks: ["ledger": [JSONPointer(["args", "0"]):
                    .init(name: "server_path", hint: nil, value: markedPath)]], hints: [:])))
        try file.save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        state.reload()
        XCTAssertTrue(state.isPublished("Team"))
        XCTAssertNil(state.collectionsCache.published["Team"], "the folder is the other machine's, not this one's")

        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(home, to: folder.path, intent: .none, reviewedValues: []))
        let document = folder.appendingPathComponent(Slug.make(home) + ".json")
        let before = try Data(contentsOf: document)
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(markedPath)]),
        ])), renamedFrom: nil, in: home))
        XCTAssertEqual(state.publishError?.message, AppState.keptPathCarriedError("ledger", FieldName.argument(1)))
        XCTAssertEqual(state.publishError?.kind, .blockedForReview)
        XCTAssertEqual(try Data(contentsOf: document), before)
        XCTAssertFalse(try jsonFile(document, contains: markedPath))
    }

    /// A copy of the marked path in an `additional` field is kept back, and the refusal says where
    /// it sits rather than that the mark moved.
    func testACopyOfAMarkedPathSaysWhereItSits() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, file) = try publishMarkedLedger(h, state, args: [markedPath])
        let before = try Data(contentsOf: file)
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(markedPath)]), "description": .string("runs \(markedPath)"),
        ])), renamedFrom: "ledger"))
        XCTAssertEqual(state.publishError?.message, AppState.keptPathCarriedError("ledger", FieldName.document("additional.description")))
        XCTAssertEqual(try Data(contentsOf: file), before)
    }

    // MARK: - The directory token on the publishing machine

    func testThePublishFolderStandsForTheDirectoryTokenOnlyWhileThisMachinePublishes() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let token = "\(Placeholder.directoryToken)/tools/srv.js"
        XCTAssertNil(state.upsert(name: "x", entry: MCPEntry(enabled: true, config: .object([
            "command": .string("node"), "args": .array([.string(token)]),
        ])), renamedFrom: nil))
        state.apply()
        XCTAssertEqual(state.connectorCaution("x", in: state.activeCollection), AppState.unpublishedDirectoryCaution)
        XCTAssertEqual(args(of: try XCTUnwrap(h.claudeServers()["x"])), [token], "no folder is guessed at")

        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        let published = try XCTUnwrap(state.collectionsCache.published[state.activeCollection]?.folder)
        XCTAssertEqual(args(of: try XCTUnwrap(h.claudeServers()["x"])), [published + "/tools/srv.js"],
                       "Claude runs the tool shipped beside the document, from the moment publishing starts")
        XCTAssertNil(state.connectorCaution("x", in: state.activeCollection))
        XCTAssertEqual(args(of: try XCTUnwrap(state.store.collections[state.activeCollection]?.mcps["x"]?.config)), [token],
                       "the store keeps the token")
        let document = folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json")
        XCTAssertTrue(try jsonFile(document, contains: token), "each subscriber resolves it against their own copy")
        XCTAssertFalse(try jsonFile(document, contains: published), "the author's folder never travels")
        state.reload()
        XCTAssertEqual(args(of: try XCTUnwrap(h.claudeServers()["x"])), [published + "/tools/srv.js"],
                       "what was written is what a reload renders, so nothing is regenerated")

        state.stopPublishing(state.activeCollection, deleteFile: false)
        XCTAssertEqual(state.connectorCaution("x", in: state.activeCollection), AppState.unpublishedDirectoryCaution)
        XCTAssertEqual(args(of: try XCTUnwrap(h.claudeServers()["x"])), [token])
    }

    func testACollectionPublishedFromAnotherMachineHasNoFolderHere() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "x", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string("\(Placeholder.directoryToken)/srv.js")]),
        ])), renamedFrom: nil))
        try seed(h, state, file: CollectionsFile(collections: [state.activeCollection: published(slug: "default")]))
        XCTAssertTrue(state.isPublished(state.activeCollection))
        XCTAssertNil(state.collectionDirectory(of: state.activeCollection))
        XCTAssertEqual(state.connectorCaution("x", in: state.activeCollection), AppState.unpublishedDirectoryCaution)
    }

    /// A published collection with an enabled connector that runs a tool from its folder, applied,
    /// so Claude's file holds the folder where the store holds the token. Returns the document and
    /// the folder as this machine records it.
    private func publishTokenConnector(_ h: AppStateHarness, _ state: AppState) throws -> (document: URL, folder: String) {
        XCTAssertNil(state.upsert(name: "x", entry: MCPEntry(enabled: true, config: .object([
            "command": .string("node"), "args": .array([.string("\(Placeholder.directoryToken)/tools/x.js")]),
        ])), renamedFrom: nil))
        state.apply()
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        let bound = try XCTUnwrap(state.collectionsCache.published[state.activeCollection]?.folder)
        XCTAssertEqual(args(of: try XCTUnwrap(h.claudeServers()["x"])), [bound + "/tools/x.js"])
        return (folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json"), bound)
    }

    func testRestoringClaudesConfigKeepsTheTokenInAPublishedCollection() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (document, folder) = try publishTokenConnector(h, state)
        // Every apply backs Claude's file up first, so any backup taken now holds the folder.
        let backup = h.dir.file("backup.json")
        try FileManager.default.copyItem(at: h.claudeConfigURL, to: backup)

        try state.restoreClaudeConfig(from: backup)
        XCTAssertEqual(args(of: try XCTUnwrap(state.store.collections[state.activeCollection]?.mcps["x"]?.config)),
                       ["\(Placeholder.directoryToken)/tools/x.js"], "the store keeps its token")
        XCTAssertTrue(try jsonFile(document, contains: Placeholder.directoryToken))
        XCTAssertFalse(try jsonFile(document, contains: folder))

        XCTAssertNil(state.publishError)

        // Removed since the backup was taken, it comes back as the backup has it: there is no store
        // copy to keep, and rewriting what came in could rewrite a genuine edit. The folder is then
        // kept back from the document, for the author to answer in Publish….
        let before = try Data(contentsOf: document)
        state.delete(names: ["x"])
        XCTAssertNotEqual(try Data(contentsOf: document), before)
        let withoutX = try Data(contentsOf: document)
        try state.restoreClaudeConfig(from: backup)
        XCTAssertEqual(args(of: try XCTUnwrap(state.store.collections[state.activeCollection]?.mcps["x"]?.config)),
                       [folder + "/tools/x.js"])
        XCTAssertEqual(state.publishError?.message, AppState.publishFolderCarriedError("x", FieldName.argument(1)))
        XCTAssertEqual(state.publishError?.kind, .blockedForReview)
        XCTAssertEqual(try Data(contentsOf: document), withoutX)
        XCTAssertFalse(try jsonFile(document, contains: folder))
    }

    /// Published from another machine: the record is in the sidecar, but this machine has no
    /// binding and so no folder to recognise. What the backup holds is adopted as written.
    func testARestoreKeepsNothingForACollectionPublishedFromAnotherMachine() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let tokened: JSONValue = .object(["command": .string("node"), "args": .array([.string("\(Placeholder.directoryToken)/tools/x.js")])])
        XCTAssertNil(state.upsert(name: "x", entry: MCPEntry(enabled: true, config: tokened), renamedFrom: nil))
        try seed(h, state, file: CollectionsFile(collections: [state.activeCollection: published(slug: "default")]))
        XCTAssertTrue(state.isPublished(state.activeCollection))
        let backup = h.dir.file("backup.json")
        try Data(#"{"mcpServers": {"x": {"command": "node", "args": ["/Users/d/elsewhere/tools/x.js"]}}}"#.utf8).write(to: backup)
        try state.restoreClaudeConfig(from: backup)
        XCTAssertEqual(args(of: try XCTUnwrap(state.store.collections[state.activeCollection]?.mcps["x"]?.config)),
                       ["/Users/d/elsewhere/tools/x.js"])
    }

    /// Removed on the other machine while this one was off: at launch Claude's config brings the
    /// connector back with this machine's folder in it. The store has no copy to keep, so it
    /// arrives as written, and the folder is kept back from the document.
    func testALaunchIngestThatBringsThePublishFolderBackIsNotPublished() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let first = h.create()
        let (document, folder) = try publishTokenConnector(h, first)
        first.dispose()
        try h.editStoreOnDisk { store in
            store.collections[store.activeCollection]?.mcps.removeValue(forKey: "x")
        }

        let before = try Data(contentsOf: document)
        let relaunched = h.create()
        defer { relaunched.dispose() }
        XCTAssertEqual(args(of: try XCTUnwrap(relaunched.store.collections[relaunched.activeCollection]?.mcps["x"]?.config)),
                       [folder + "/tools/x.js"], "ingested as Claude's file has it")
        XCTAssertEqual(relaunched.publishError?.message, AppState.publishFolderCarriedError("x", FieldName.argument(1)))
        XCTAssertEqual(relaunched.publishError?.kind, .blockedForReview)
        XCTAssertEqual(try Data(contentsOf: document), before)
        XCTAssertFalse(try jsonFile(document, contains: folder))
    }

    func testThisMachinesPublishFolderWrittenOutIsNotPublished() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        let bound = try XCTUnwrap(state.collectionsCache.published[state.activeCollection]?.folder)
        let file = folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json")
        let before = try Data(contentsOf: file)

        // A sibling folder that merely begins with its name is somebody else's path, and travels.
        XCTAssertNil(state.upsert(name: "sibling", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(bound + "-tools/x.js")]),
        ])), renamedFrom: nil))
        XCTAssertNil(state.publishError)
        let withSibling = try Data(contentsOf: file)
        XCTAssertNotEqual(withSibling, before)

        XCTAssertNil(state.upsert(name: "typed", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(bound + "/tools/x.js")]),
        ])), renamedFrom: nil))
        XCTAssertEqual(state.publishError?.message, AppState.publishFolderCarriedError("typed", FieldName.argument(1)))
        XCTAssertEqual(state.publishError?.kind, .blockedForReview, "answered in the Publish sheet, not another folder")
        XCTAssertEqual(try Data(contentsOf: file), withSibling)

        // The sheet lists the folder where it sits, and holds Export until it is answered.
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        XCTAssertEqual(sheet.keptPaths.map { "\($0.connector) \($0.field) \($0.value) \($0.kind)" },
                       ["typed local.args[0] \(bound) folder"])
        let out = h.dir.file("away/copy.json")
        XCTAssertEqual(sheet.export(to: out.path), PublishModel.publishFolderNote("typed", FieldName.argument(1)))
        XCTAssertFalse(FileManager.default.fileExists(atPath: out.path))
    }

    /// A folder written out where no row reaches it: the refusal names the field, the sheet lists
    /// it and holds Publish, and Use ${COLLECTION_DIR} writes the token back into the connector
    /// itself, which Claude's config and the document then follow.
    func testUseDirectoryTokenWritesTheTokenWhereTheFolderSits() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        let bound = try XCTUnwrap(state.collectionsCache.published[state.activeCollection]?.folder)
        let file = folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json")
        XCTAssertNil(state.upsert(name: "tool", entry: MCPEntry(enabled: true, config: .object([
            "command": .string(bound + "/bin/tool"), "env": .object(["PATH_EXTRA": .string(bound + ":/opt/lib")]),
        ])), renamedFrom: nil))
        state.apply()
        XCTAssertEqual(state.publishError?.message, AppState.publishFolderCarriedError("tool", FieldName.command))
        let before = try Data(contentsOf: file)

        let sheet = PublishModel(state: state, collection: state.activeCollection)
        sheet.envRows[try XCTUnwrap(sheet.envRows.firstIndex { $0.name == "PATH_EXTRA" })].share = true
        XCTAssertEqual(sheet.keptPaths.map { "\($0.connector) \($0.field) \($0.kind)" },
                       ["tool env.PATH_EXTRA.value folder", "tool local.command folder"])
        XCTAssertFalse(sheet.canPublish)
        XCTAssertEqual(sheet.publish(), PublishModel.publishFolderNote("tool", FieldName.envValue("PATH_EXTRA")))
        XCTAssertEqual(try Data(contentsOf: file), before)

        for kept in sheet.keptPaths { XCTAssertNil(sheet.useDirectoryToken(kept)) }
        let stored = try XCTUnwrap(state.store.collections[state.activeCollection]?.mcps["tool"]?.config)
        XCTAssertEqual(stored, .object(["command": .string("\(Placeholder.directoryToken)/bin/tool"),
                                        "env": .object(["PATH_EXTRA": .string("\(Placeholder.directoryToken):/opt/lib")])]))
        XCTAssertEqual(try h.claudeServers()["tool"], .object(["command": .string(bound + "/bin/tool"),
                                                               "env": .object(["PATH_EXTRA": .string(bound + ":/opt/lib")])]),
                       "Claude still runs the folder, which the token stands for here")
        XCTAssertEqual(sheet.envRows.first { $0.name == "PATH_EXTRA" }?.value, "\(Placeholder.directoryToken):/opt/lib")
        XCTAssertEqual(sheet.keptPaths, [])
        XCTAssertTrue(sheet.canPublish)
        XCTAssertNil(sheet.publish())
        XCTAssertNil(state.publishError)
        XCTAssertFalse(try jsonFile(file, contains: bound))
        XCTAssertTrue(try jsonFile(file, contains: Placeholder.directoryToken))
    }

    /// The publish folder copied beside a tick is listed as a copy of a marked path, whose note ends
    /// "or release it". Release refuses the folder all the same, so its refusal must not repeat
    /// that advice: it says what a folder entry says, where the token goes.
    func testARefusedReleaseOfTheFolderNeverSuggestsReleasingIt() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        let bound = try XCTUnwrap(state.collectionsCache.published[state.activeCollection]?.folder)
        XCTAssertNil(state.upsert(name: "tool", entry: MCPEntry(enabled: true, config: .object([
            "command": .string("node"), "args": .array([.string(bound), .string(bound)]),
        ])), renamedFrom: nil))

        let sheet = PublishModel(state: state, collection: state.activeCollection)
        XCTAssertEqual(sheet.pathRows.map(\.value), [bound, bound])
        sheet.pathRows[0].marked = true
        let kept = try XCTUnwrap(sheet.keptPaths.first)
        XCTAssertEqual(kept.kind, .path, "the unticked copy of a ticked path")
        XCTAssertEqual(sheet.note(for: kept), PublishModel.otherFolderNote("tool", FieldName.argument(2), state.activeCollection))

        XCTAssertEqual(sheet.releaseKeptPath(bound), PublishModel.publishFolderNote("tool", FieldName.argument(2)))
        XCTAssertEqual(sheet.pathRows.map(\.marked), [true, false], "the refused release changed nothing")
    }

    /// Release is no answer for a folder of the collection's own, whatever the view offers: the
    /// entry stays, Publish and Export stay held, and the folder reaches no document. Only writing
    /// the token takes it out of the preview.
    func testReleasingAFolderEntryIsRefused() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        let bound = try XCTUnwrap(state.collectionsCache.published[state.activeCollection]?.folder)
        let file = folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json")
        XCTAssertNil(state.upsert(name: "tool", entry: MCPEntry(enabled: true, config: .object(["command": .string(bound + "/bin/tool")])),
                                  renamedFrom: nil))
        let before = try Data(contentsOf: file)
        let note = PublishModel.publishFolderNote("tool", FieldName.command)

        let sheet = PublishModel(state: state, collection: state.activeCollection)
        let kept = try XCTUnwrap(sheet.keptPaths.first)
        XCTAssertEqual(kept.kind, .folder)
        let preview = sheet.preview
        XCTAssertEqual(sheet.releaseKeptPath(kept.value), note)
        XCTAssertEqual(sheet.keptPaths, [kept])
        XCTAssertEqual(sheet.preview, preview, "the refused release changed nothing")
        XCTAssertFalse(sheet.canPublish)
        XCTAssertFalse(sheet.canExport)
        XCTAssertEqual(sheet.publish(), note)
        let out = h.dir.file("away/copy.json")
        XCTAssertEqual(sheet.export(to: out.path), note)
        XCTAssertFalse(FileManager.default.fileExists(atPath: out.path))
        XCTAssertEqual(try Data(contentsOf: file), before)
        XCTAssertFalse(try jsonFile(file, contains: bound))

        // Nor does the state let it go when asked directly, or when the list says so.
        XCTAssertEqual(state.writeExport(for: state.activeCollection, intent: sheet.intent, to: out.path, released: [bound]),
                       AppState.publishFolderCarriedError("tool", FieldName.command))
        XCTAssertFalse(FileManager.default.fileExists(atPath: out.path))
        XCTAssertEqual(state.updatePublishIntent(state.activeCollection, intent: sheet.intent, reviewedValues: [], releasedValues: [bound]),
                       AppState.publishFolderCarriedError("tool", FieldName.command))
        XCTAssertFalse(try jsonFile(file, contains: bound))

        XCTAssertTrue(jsonText(sheet.preview, contains: bound))
        XCTAssertNil(sheet.useDirectoryToken(kept))
        XCTAssertFalse(jsonText(sheet.preview, contains: bound))
        XCTAssertTrue(sheet.canPublish)
    }

    /// The folder in the author's own hint is the sheet's to rewrite: the token goes into the hint.
    func testUseDirectoryTokenRewritesAHintInTheSheet() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        XCTAssertNil(state.upsert(name: "svc", entry: MCPEntry(config: .object([
            "command": .string("svc"), "env": .object(["TOKEN": .string("sk-1")])])), renamedFrom: nil))
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        let bound = try XCTUnwrap(state.collectionsCache.published[state.activeCollection]?.folder)
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        let row = try XCTUnwrap(sheet.envRows.firstIndex { $0.connector == "svc" && $0.name == "TOKEN" })
        sheet.envRows[row].hint = "see \(bound)/README"
        let kept = try XCTUnwrap(sheet.keptPaths.first)
        XCTAssertEqual("\(kept.connector) \(kept.field) \(kept.kind)", "svc env.TOKEN.hint folder")
        XCTAssertNil(sheet.useDirectoryToken(kept))
        XCTAssertEqual(sheet.envRows[row].hint, "see \(Placeholder.directoryToken)/README")
        XCTAssertEqual(sheet.keptPaths, [])
        XCTAssertNil(sheet.publish())
    }

    /// The author moved the publish folder, then restored a backup taken before the move: the store
    /// keeps its token, since the backup renders as the store does with the folder of the day, and
    /// the earlier folder written out anywhere else is kept back as the current one is.
    func testAnEarlierPublishFolderIsTheCollectionsOwnToo() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let token = "\(Placeholder.directoryToken)/tools/srv.js"
        XCTAssertNil(state.upsert(name: "x", entry: MCPEntry(enabled: true, config: .object([
            "command": .string("node"), "args": .array([.string(token)])])), renamedFrom: nil))
        state.apply()
        XCTAssertNil(state.startPublishing(state.activeCollection, to: try publishFolder(h, "pub1").path, intent: .none, reviewedValues: []))
        let oldFolder = try XCTUnwrap(state.collectionsCache.published[state.activeCollection]?.folder)
        let backup = h.dir.file("backup.json")
        try FileManager.default.copyItem(at: h.claudeConfigURL, to: backup)
        let second = try publishFolder(h, "pub2")
        XCTAssertNil(state.changePublishFolder(state.activeCollection, to: second.path))
        let newFolder = try XCTUnwrap(state.collectionsCache.published[state.activeCollection]?.folder)
        XCTAssertEqual(state.collectionsCache.published[state.activeCollection]?.publishedFolders, [oldFolder, newFolder])
        let file = second.appendingPathComponent(Slug.make(state.activeCollection) + ".json")

        try state.restoreClaudeConfig(from: backup)
        XCTAssertEqual(args(of: try XCTUnwrap(state.store.collections[state.activeCollection]?.mcps["x"]?.config)), [token],
                       "the backup renders as the store does with the folder it had then")
        XCTAssertNil(state.publishError)
        XCTAssertFalse(try jsonFile(file, contains: oldFolder))

        XCTAssertNil(state.upsert(name: "old", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string("--root"), .string(oldFolder)])])), renamedFrom: nil))
        XCTAssertEqual(state.publishError?.message, AppState.publishFolderCarriedError("old", FieldName.argument(2)))
        XCTAssertFalse(try jsonFile(file, contains: oldFolder))
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        let kept = try XCTUnwrap(sheet.keptPaths.first)
        XCTAssertEqual("\(kept.field) \(kept.value) \(kept.kind)", "local.args[1] \(oldFolder) folder")
        XCTAssertNil(sheet.useDirectoryToken(kept))
        XCTAssertNil(state.publishError)
        XCTAssertFalse(try jsonFile(file, contains: oldFolder))
    }

    /// The folder followed by a list separator is still the folder; followed by what continues a
    /// file name it is another name.
    func testThePublishFolderIsFoundBesideAnySeparator() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: .none))
        let bound = try XCTUnwrap(state.collectionsCache.published[state.activeCollection]?.folder)
        let file = folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json")
        for written in ["\(bound):/opt/lib", "\(bound);/opt/lib", "\(bound),x", "x \(bound)"] {
            XCTAssertNil(state.upsert(name: "py", entry: MCPEntry(config: .object([
                "command": .string("python3"), "args": .array([.string("--path"), .string(written)])])), renamedFrom: nil))
            XCTAssertEqual(state.publishError?.message, AppState.publishFolderCarriedError("py", FieldName.argument(2)), written)
            XCTAssertFalse(try jsonFile(file, contains: bound), written)
            state.delete(names: ["py"])
        }
        for other in ["\(bound).bak", "\(bound)_old/x", "\(bound)é/x"] {
            XCTAssertNil(state.upsert(name: "py", entry: MCPEntry(config: .object([
                "command": .string("python3"), "args": .array([.string("--path"), .string(other)])])), renamedFrom: nil))
            XCTAssertNil(state.publishError, other)
            state.delete(names: ["py"])
        }
    }

    /// Another collection's publish folder is a path this machine keeps back, not this one's own:
    /// the token would stand for the wrong folder, so its answer is Release.
    func testAnotherCollectionsPublishFolderIsReleasedNotRewritten() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let team = state.activeCollection
        XCTAssertNil(state.startPublishing(team, to: try publishFolder(h, "pubTeam").path, intent: .none))
        let teamFolder = try XCTUnwrap(state.collectionsCache.published[team]?.folder)
        XCTAssertNil(state.createCollection(named: "Clients"))
        XCTAssertNil(state.upsert(name: "shared", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(teamFolder + "/tools/x.js")])])), renamedFrom: nil, in: "Clients"))
        let clients = try publishFolder(h, "pubClients")
        XCTAssertEqual(state.startPublishing("Clients", to: clients.path, intent: .none, reviewedValues: []),
                       AppState.keptPathCarriedError("shared", FieldName.argument(1)))
        let sheet = PublishModel(state: state, collection: "Clients")
        let kept = try XCTUnwrap(sheet.keptPaths.first { $0.connector == "shared" })
        XCTAssertEqual(kept.kind, .path)
        XCTAssertEqual(sheet.note(for: kept), PublishModel.otherFolderNote("shared", FieldName.argument(1), team),
                       "whose folder it is, before the author sends it")
        XCTAssertEqual(sheet.useDirectoryToken(kept), sheet.note(for: kept), "the token stands for no folder here")
        XCTAssertEqual(sheet.releaseKeptPath(kept.value), nil)
        XCTAssertNil(sheet.publish())
        XCTAssertTrue(try jsonFile(clients.appendingPathComponent(Slug.make("Clients") + ".json"), contains: teamFolder),
                      "the author's explicit choice")
    }

    // MARK: - Import as copies

    /// The design's sample plus a connector whose path is written against the document's own
    /// folder, so one import exercises markers, shared values and the directory token at once.
    private var importableDocument: CollectionDocument {
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["ledger"] = .init(
            launcher: .local(.init(command: "node", args: ["${COLLECTION_DIR}/dist/index.js"], platform: .current)))
        return doc
    }

    func testImportCopiesArriveDisabledWithProvenanceAndExpandedToken() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("shared/data-team.json")
        try h.writeDocument(importableDocument, at: url)

        XCTAssertNil(state.importCopies(documentAt: url.path, into: "Default", choices: [:]))
        let mcps = try XCTUnwrap(state.store.collections["Default"]).mcps
        XCTAssertEqual(mcps.keys.sorted(), ["aws-mcp", "dbt", "github", "ledger", "notion", "scoutbook", "service-now"])
        for name in ["dbt", "github", "ledger", "notion"] {
            XCTAssertEqual(mcps[name]?.enabled, false, "an imported copy arrives off")
        }
        XCTAssertEqual(mcps["ledger"]?.config.value(at: JSONPointer(["args", "0"])),
                       .string(url.standardizedFileURL.deletingLastPathComponent().path + "/dist/index.js"),
                       "the directory token is expanded once, against the folder the document sits in")
        XCTAssertEqual(mcps["dbt"]?.config.value(at: JSONPointer(["env", "DBT_TOKEN"])), .string("${CC_NEEDS:DBT_TOKEN}"))
        XCTAssertEqual(mcps["dbt"]?.config.value(at: JSONPointer(["env", "DBT_REGION"])), .string("us"))
        XCTAssertEqual(state.kind(of: "Default"), .local, "copies leave no link to the file")
        XCTAssertTrue(state.collectionsCache.synced.isEmpty)
        XCTAssertTrue(state.pendingUpdates.isEmpty)
        XCTAssertEqual(state.collectionsFile.collections["Default"]?.provenance["dbt"],
                       CollectionsFile.Provenance(from: "Data team", author: "Acme Data Platform", date: state.today))
        XCTAssertEqual(state.connectorCaution("dbt", in: "Default"), AppState.needsValueCaution("DBT_TOKEN"))
        XCTAssertEqual(try h.claudeServers().keys.sorted(), ["aws-mcp", "scoutbook", "service-now"],
                       "nothing that arrives off reaches Claude")
        XCTAssertEqual(state.importCopies(documentAt: url.path, into: "Nowhere", choices: [:]), nil,
                       "a collection that does not exist is a no-op, as switching to one is")
    }

    func testReplaceKeepsAFilledValueAndKeepBothSuffixes() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("shared/data-team.json")
        try h.writeDocument(importableDocument, at: url)
        XCTAssertNil(state.importCopies(documentAt: url.path, into: "Default", choices: [:]))

        // The user fills the token and turns dbt on.
        var dbt = try XCTUnwrap(state.store.collections["Default"]?.mcps["dbt"])
        dbt.config = try XCTUnwrap(dbt.config.replacing(at: JSONPointer(["env", "DBT_TOKEN"]), with: .string("tok")))
        dbt.enabled = true
        XCTAssertNil(state.upsert(name: "dbt", entry: dbt, renamedFrom: "dbt"))

        // A day later the author ships a new dbt, and the same document is imported again.
        h.now = h.now.addingTimeInterval(24 * 60 * 60)
        var doc = importableDocument
        doc.connectors["dbt"]?.launcher = .local(.init(command: "npx", args: ["-y", "@dbt/mcp@2"], platform: .current))
        try h.writeDocument(doc, at: url)
        XCTAssertNil(state.importCopies(documentAt: url.path, into: "Default",
                                        choices: ["dbt": .replace, "github": .keepBoth, "notion": .skip, "ledger": .skip]))
        let mcps = try XCTUnwrap(state.store.collections["Default"]).mcps
        XCTAssertEqual(mcps["dbt"]?.config.value(at: JSONPointer(["args", "1"])), .string("@dbt/mcp@2"))
        XCTAssertEqual(mcps["dbt"]?.config.value(at: JSONPointer(["env", "DBT_TOKEN"])), .string("tok"),
                       "Replace keeps what the user filled in")
        XCTAssertEqual(mcps["dbt"]?.enabled, true, "replacing a connector that was on leaves it on")
        XCTAssertNotNil(mcps["github 2"], "Keep both lands beside what is already there")
        XCTAssertEqual(mcps["github 2"]?.enabled, false)
        XCTAssertEqual(mcps.keys.filter { $0.hasPrefix("notion") }.sorted(), ["notion"], "Skip leaves it alone")
        XCTAssertEqual(state.collectionsFile.collections["Default"]?.provenance["dbt"]?.date, "2026-09-05")
        XCTAssertEqual(state.collectionsFile.collections["Default"]?.provenance["github 2"]?.from, "Data team")
        XCTAssertEqual(try h.claudeServers()["dbt"]?.value(at: JSONPointer(["args", "1"])), .string("@dbt/mcp@2"),
                       "a replaced connector that was on reaches Claude")

        // A third import with Keep both again numbers on from the highest suffix taken.
        XCTAssertNil(state.importCopies(documentAt: url.path, into: "Default",
                                        choices: ["github": .keepBoth, "dbt": .skip, "notion": .skip, "ledger": .skip]))
        XCTAssertNotNil(state.store.collections["Default"]?.mcps["github 3"])
    }

    func testMakeLocalCopyIntoALocalCollection() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.subscribe(state, to: importableDocument, at: "shared/data-team.json")
        XCTAssertNil(state.createCollection(named: "Personal"))

        XCTAssertNil(state.makeLocalCopy(of: ["dbt", "ledger"], from: "Data team", into: "Personal"))
        let mcps = try XCTUnwrap(state.store.collections["Personal"]).mcps
        XCTAssertEqual(mcps["dbt"]?.enabled, false)
        XCTAssertEqual(mcps["dbt"]?.config.value(at: JSONPointer(["env", "DBT_TOKEN"])), .string("${CC_NEEDS:DBT_TOKEN}"),
                       "a copy is what it was: an unfilled marker stays unfilled")
        XCTAssertEqual(mcps["ledger"]?.config.value(at: JSONPointer(["args", "0"])),
                       .string(url.standardizedFileURL.deletingLastPathComponent().path + "/dist/index.js"),
                       "a local collection has no document to resolve the directory token against later")
        XCTAssertEqual(state.collectionsFile.collections["Personal"]?.provenance["dbt"],
                       CollectionsFile.Provenance(from: "Data team", author: nil, date: IsoTimestamp.localDate(from: h.now)))
        XCTAssertEqual(state.kind(of: "Data team"), .synced, "the source is untouched")
        XCTAssertEqual(state.store.collections["Data team"]?.mcps.count, 4)

        XCTAssertNil(state.makeLocalCopy(of: ["dbt"], from: "Data team", into: "Personal"))
        XCTAssertNotNil(state.store.collections["Personal"]?.mcps["dbt 2"])

        XCTAssertEqual(state.makeLocalCopy(of: ["dbt"], from: "Personal", into: "Data team"),
                       AppState.targetMustBeLocalError)
        XCTAssertNil(state.store.collections["Data team"]?.mcps["dbt 2"])
    }

    func testMakeLocalCopyOfAWholeSyncedCollection() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.subscribe(state, to: importableDocument, at: "shared/data-team.json")

        XCTAssertNil(state.makeLocalCopyOfCollection("Data team", named: "Data team copy"))
        XCTAssertEqual(state.kind(of: "Data team copy"), .local)
        XCTAssertEqual(state.activeCollection, "Default", "the copy is all off, so switching to it would empty Claude's config")
        let copied = try XCTUnwrap(state.store.collections["Data team copy"]).mcps
        XCTAssertEqual(copied.keys.sorted(), ["dbt", "github", "ledger", "notion"])
        XCTAssertTrue(copied.values.allSatisfy { !$0.enabled })
        XCTAssertEqual(copied["ledger"]?.config.value(at: JSONPointer(["args", "0"])),
                       .string(url.standardizedFileURL.deletingLastPathComponent().path + "/dist/index.js"))
        for name in copied.keys {
            XCTAssertEqual(state.collectionsFile.collections["Data team copy"]?.provenance[name]?.from, "Data team")
        }
        XCTAssertNil(state.collectionsCache.synced["Data team copy"], "a copy follows nothing")
        XCTAssertEqual(state.kind(of: "Data team"), .synced)

        XCTAssertNotNil(state.makeLocalCopyOfCollection("Data team", named: "Data team copy"), "a name already taken is refused")
        XCTAssertNil(state.makeLocalCopyOfCollection("Nowhere", named: "Ghost"))
        XCTAssertFalse(state.collectionNames.contains("Ghost"))
    }

    /// A copy whose name the target already holds: the author's answer decides. Replace takes the
    /// target's entry over, skip copies nothing, and the default is still to land beside it.
    func testCopyingAConnectorHonoursTheCollisionChoice() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Source"))
        XCTAssertNil(state.upsert(name: "github", entry: MCPEntry(config: .object([
            "command": .string("/new/github"),
        ])), renamedFrom: nil, in: "Source"))
        XCTAssertNil(state.upsert(name: "github", entry: MCPEntry(config: .object([
            "command": .string("/old/github"),
        ])), renamedFrom: nil, in: "Default"))

        XCTAssertNil(state.makeLocalCopy(of: ["github"], from: "Source", into: "Default",
                                         choices: ["github": .skip]))
        XCTAssertEqual(state.store.collections["Default"]?.mcps.keys.filter { $0.hasPrefix("github") }.sorted(),
                       ["github"], "skip copies nothing")
        XCTAssertEqual(state.store.collections["Default"]?.mcps["github"]?.config,
                       .object(["command": .string("/old/github")]), "and leaves the target alone")

        XCTAssertNil(state.makeLocalCopy(of: ["github"], from: "Source", into: "Default",
                                         choices: ["github": .replace]))
        XCTAssertEqual(state.store.collections["Default"]?.mcps.keys.filter { $0.hasPrefix("github") }.sorted(),
                       ["github"], "replace makes no second entry")
        XCTAssertEqual(state.store.collections["Default"]?.mcps["github"]?.config,
                       .object(["command": .string("/new/github")]), "and takes the source's config")
        XCTAssertEqual(state.store.collections["Default"]?.mcps["github"]?.enabled, false,
                       "a replaced copy is still off")

        XCTAssertNil(state.makeLocalCopy(of: ["github"], from: "Source", into: "Default"))
        XCTAssertEqual(state.store.collections["Default"]?.mcps.keys.filter { $0.hasPrefix("github") }.sorted(),
                       ["github", "github 2"], "and with no choice it still lands beside")
    }

    /// Removing several connectors is one write, not one per connector: a loop would rotate a
    /// backup and republish for each.
    func testRemovingSeveralConnectorsPersistsOnce() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        for name in ["alpha", "beta", "gamma"] {
            XCTAssertNil(state.upsert(name: name, entry: MCPEntry(config: .object([
                "command": .string("/bin/" + name),
            ])), renamedFrom: nil, in: "Default"))
        }
        let before = try backupCount(h, series: "mcps")

        state.delete(names: ["alpha", "gamma"], in: "Default")

        XCTAssertNil(state.store.collections["Default"]?.mcps["alpha"], "alpha went")
        XCTAssertNil(state.store.collections["Default"]?.mcps["gamma"], "and gamma")
        XCTAssertNotNil(state.store.collections["Default"]?.mcps["beta"], "beta stayed")
        XCTAssertEqual(try backupCount(h, series: "mcps"), before + 1,
                       "one write, so one backup rotation")
    }

    /// Each removed connector's publish ticks and path marks go with it, and the others' stay.
    func testRemovingSeveralConnectorsTakesTheirPublishTicksAndMarksWithThem() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        for name in ["alpha", "beta", "gamma"] {
            XCTAssertNil(state.upsert(name: name, entry: MCPEntry(config: .object([
                "command": .string("/bin/" + name), "args": .array([.string("/Users/d/\(name)/index.js")]),
                "env": .object(["A": .string("us")]),
            ])), renamedFrom: nil, in: "Default"))
        }
        func mark(_ name: String) -> [JSONPointer: PublishIntent.PathMark] {
            [JSONPointer(["args", "0"]): .init(name: name + "_path", hint: nil, value: "/Users/d/\(name)/index.js")]
        }
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing("Default", to: folder.path, intent: PublishIntent(
            shareValues: ["alpha": ["A"], "beta": ["A"], "gamma": ["A"]],
            pathMarks: ["alpha": mark("alpha"), "beta": mark("beta"), "gamma": mark("gamma")], hints: [:])))

        state.delete(names: ["alpha", "gamma"], in: "Default")

        XCTAssertEqual(state.collectionsFile.collections["Default"]?.publish?.intent,
                       PublishIntent(shareValues: ["beta": ["A"]], pathMarks: ["beta": mark("beta")], hints: [:]))
        XCTAssertNil(state.publishError)
    }

    /// A name the collection does not hold is skipped rather than failing, and removing nothing
    /// writes nothing.
    func testRemovingNoConnectorsWritesNothing() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "alpha", entry: MCPEntry(config: .object([
            "command": .string("/bin/alpha"),
        ])), renamedFrom: nil, in: "Default"))
        let before = try backupCount(h, series: "mcps")

        state.delete(names: [], in: "Default")
        XCTAssertEqual(try backupCount(h, series: "mcps"), before, "nothing to do, nothing written")

        state.delete(names: ["nosuch"], in: "Default")
        XCTAssertNotNil(state.store.collections["Default"]?.mcps["alpha"], "a name it never held is skipped")
        XCTAssertEqual(try backupCount(h, series: "mcps"), before, "and skipping it writes nothing either")
    }

    /// How many files the named backup series holds, for proving a write happened once.
    private func backupCount(_ h: AppStateHarness, series: String) throws -> Int {
        let fm = FileManager.default
        guard fm.fileExists(atPath: h.backupsDir.path) else { return 0 }
        return try fm.contentsOfDirectory(atPath: h.backupsDir.path).filter { $0.hasPrefix(series) }.count
    }
}
