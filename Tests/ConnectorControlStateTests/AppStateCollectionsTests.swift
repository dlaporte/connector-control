import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/AppStateCollectionsTests.cs. The sidecar, the
/// machine-local cache and the named collection actions, against the real on-disk layout the
/// harness builds.
///
/// Nothing subscribes or publishes yet, so a synced or published collection is set up the way
/// those flows will leave it — the two files on disk — and read back through a real reload.
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
        XCTAssertEqual(state.activeCollection, "Team", "a new collection becomes the active one, as the chip menu has always done")
        XCTAssertNil(state.deleteCollection(named: "Team"))
        XCTAssertEqual(state.collectionNames, ["Default"])
        XCTAssertEqual(state.deleteCollection(named: state.activeCollection), AppState.lastLocalCollectionError)
    }

    func testCreateCopiesTheActiveCollectionAndReportsItsErrors() {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertEqual(state.createCollection(named: "Default"), "A collection named \u{201C}Default\u{201D} already exists.")
        XCTAssertEqual(state.createCollection(named: "   "), AppState.nameEmptyError)
        XCTAssertEqual(state.collectionNames, ["Default"])
        XCTAssertNil(state.createCollection(named: "Work"))
        XCTAssertEqual(state.sortedNames, ["aws-mcp", "scoutbook", "service-now"], "a COPY of the active collection")
        XCTAssertEqual(h.settings.lastApplyDate, h.now)
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
        let url = h.dir.file("data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
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
        let url = h.dir.file("data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
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
        let url = h.dir.file("data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
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

    /// The bytes an author's machine would have written, at a path this machine can read.
    private func writeDocument(_ doc: CollectionDocument, at url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try doc.serialized().write(to: url)
    }

    /// One local connector, authored on `platform`, so a test can pin what a launcher from the
    /// other platform (or a directory token) does without carrying the four-connector sample.
    private func oneLocalConnector(_ name: String, command: String, args: [String],
                                   platform: CollectionPlatform = .current) -> CollectionDocument {
        CollectionDocument(
            name: "Tools", author: nil, origin: "o-tools", exported: "2026-09-21T14:02:11Z",
            connectors: [name: .init(launcher: .local(.init(command: command, args: args, platform: platform)))])
    }

    func testSubscribeCreatesADisabledReadOnlyMirror() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
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
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: "Analytics"), "the caller's name beats the document's")
        XCTAssertEqual(state.kind(of: "Analytics"), .synced)

        let missing = h.dir.file("gone.json")
        XCTAssertNotNil(state.subscribe(documentAt: missing.path, as: nil))
        let half = h.dir.file("half.json")
        try TempDir.touch(half, "{half")
        XCTAssertEqual(state.subscribe(documentAt: half.path, as: nil)?.hasPrefix("half.json couldn’t be read: "), true)
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
        // What the publishing task will leave behind: a local collection whose document carries
        // this origin. Reading it back in would make the app its own author.
        try seed(h, state, file: CollectionsFile(collections: [
            "Default": CollectionsFile.Entry(kind: .local, publish: CollectionsFile.PublishRecord(
                slug: "data-team", origin: "6f1c4a2e-1b8d-4b0e-9f0a-3c2d7e8a91e2", intent: .none)),
        ]))
        let url = h.dir.file("data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertEqual(state.subscribe(documentAt: url.path, as: nil), AppState.ownCollectionError)
        XCTAssertEqual(state.collectionNames, ["Default"])
    }

    func testASourceChangeBecomesAPendingUpdateThatApplyLandsWithFilledValuesKept() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
        // The user fills the token and turns dbt on.
        state.switchCollection(to: "Data team")
        var dbt = try XCTUnwrap(state.store.collections["Data team"]?.mcps["dbt"])
        dbt.config = dbt.config.replacing(at: JSONPointer(["env", "DBT_TOKEN"]), with: .string("tok"))!
        dbt.enabled = true
        // Task 6 gives upsert a collection argument; until then the edit lands in the active one.
        XCTAssertNil(state.upsert(name: "dbt", entry: dbt, renamedFrom: "dbt"))
        XCTAssertTrue(state.pendingUpdates.isEmpty, "a filled marker is not a change to the collection")

        // The author changes dbt's args and removes github.
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["github"] = nil
        doc.connectors["dbt"]?.launcher = .local(.init(command: "npx", args: ["-y", "@dbt/mcp@2"], platform: .mac))
        try writeDocument(doc, at: url)
        try TempDir.bumpModificationDate(of: url)
        XCTAssertTrue(h.ui.pumpUntil({ state.pendingUpdates["Data team"] != nil }, timeout: 8))
        XCTAssertEqual(state.pendingUpdates["Data team"]?.summary(), "removes github; changes dbt")
        XCTAssertEqual(h.notifier.sent.last?.body,
                       AppState.collectionUpdateNotificationBody("Data team", "removes github; changes dbt"))
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
        let url = h.dir.file("t.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: "T"))
        try Data("{half".utf8).write(to: url)
        try TempDir.bumpModificationDate(of: url)
        _ = h.ui.pumpUntil({ !h.delays.pending.isEmpty }, timeout: 8)
        XCTAssertTrue(state.sourceErrors.isEmpty, "the first failure schedules a retry instead of reporting")
        XCTAssertEqual(h.delays.pending.count, 1)
        XCTAssertEqual(h.delays.pending.first?.delay, 2)

        state.refreshSource(for: "T")
        XCTAssertNotNil(state.sourceErrors["T"])
        XCTAssertEqual(h.delays.pending.count, 1, "Refresh answers now instead of waiting again")

        // The half-written file lands in full: the next read clears the error and the collection
        // is back to having nothing to say.
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        state.refreshSource(for: "T")
        XCTAssertTrue(state.sourceErrors.isEmpty)
        XCTAssertTrue(state.pendingUpdates.isEmpty)
    }

    func testLocateBindsAndTheRelativePathBindsAutomatically() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        // The document travels inside the store's own folder, as a shared master list does.
        let inStore = h.storeDir.appendingPathComponent("shared/data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: inStore)
        XCTAssertNil(state.subscribe(documentAt: inStore.path, as: nil))
        XCTAssertEqual(state.collectionsFile.collections["Data team"]?.relativeToStore, "shared/data-team.json")

        // Another machine: the sidecar travels with the store, this machine's bindings do not.
        try FileManager.default.removeItem(at: state.service.paths.collectionsCacheURL)
        state.reload()
        XCTAssertEqual(state.sourceBinding(of: "Data team")?.path, inStore.path, "found beside the store, with no prompt")

        // A document somewhere the relative path cannot reach is pointed at by hand.
        let elsewhere = h.dir.file("elsewhere/data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: elsewhere)
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
        let url = h.dir.file("data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
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

        // The author's next change reaches nobody: there is no binding left to watch.
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["github"] = nil
        try writeDocument(doc, at: url)
        try TempDir.bumpModificationDate(of: url)
        _ = h.ui.pumpUntil({ false }, timeout: 1.0)
        XCTAssertTrue(state.pendingUpdates.isEmpty)
    }

    func testDeletingASyncedCollectionLeavesTheFileAlone() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))

        XCTAssertNil(state.deleteCollection(named: "Data team"))
        XCTAssertEqual(state.collectionNames, ["Default"])
        XCTAssertNil(state.sourceBinding(of: "Data team"))
        XCTAssertTrue(state.watchedSourceCollections.isEmpty)
        XCTAssertTrue(FileManager.default.fileExists(atPath: url.path), "the source file is never ours to delete")
    }

    func testTheDirectoryTokenExpandsAgainstTheBoundFolderWhenApplied() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("tools/servers.json")
        try writeDocument(oneLocalConnector("x", command: "node", args: ["\(Placeholder.directoryToken)/srv.js"]), at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
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
        let url = h.dir.file("tools.json")
        try writeDocument(oneLocalConnector("x", command: "node", args: ["srv.js"], platform: other), at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
        XCTAssertEqual(state.connectorCaution("x", in: "Tools"), AppState.authoredElsewhereCaution)

        try writeDocument(oneLocalConnector("x", command: "node", args: ["srv.js"]), at: url)
        state.refreshSource(for: "Tools")
        XCTAssertNil(state.connectorCaution("x", in: "Tools"), "a launcher from this platform needs no warning")
    }

    func testAnUnchangedSourceIsNotReRenderedButPendingIsReDerived() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
        state.switchCollection(to: "Data team")
        let renders = state.sourceRenders

        // A local edit makes the collection differ from its source without the file moving.
        var dbt = try XCTUnwrap(state.store.collections["Data team"]?.mcps["dbt"])
        dbt.config = dbt.config.replacing(at: JSONPointer(["args", "1"]), with: .string("@dbt/mcp@local"))!
        XCTAssertNil(state.upsert(name: "dbt", entry: dbt, renamedFrom: "dbt"))
        XCTAssertEqual(state.pendingUpdates["Data team"]?.summary(), "changes dbt")
        XCTAssertEqual(state.sourceRenders, renders, "the same bytes are never decoded twice")

        // Another machine applies the source, and its master list arrives here.
        var store = try h.storeOnDisk()
        let rendered = try XCTUnwrap(state.pendingDocument(for: "Data team"))
        let applied = CollectionApply.apply(rendered: rendered, current: store.collections["Data team"]?.mcps ?? [:],
                                            previousNeeds: state.collectionsFile.collections["Data team"]?.needs ?? [:])
        store.collections["Data team"] = Collection(mcps: applied.entries)
        try MasterStoreIO.save(store, to: h.masterStoreURL)
        state.reload()
        XCTAssertTrue(state.pendingUpdates.isEmpty, "the list already matches the document; the banner must not outlive it")
        XCTAssertEqual(state.sourceRenders, renders, "and re-deriving it still costs no decode")
    }

    func testTheRetryChainReportsOnlyTheThirdFailure() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("t.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: "T"))
        try Data("{half".utf8).write(to: url)
        try TempDir.bumpModificationDate(of: url)
        XCTAssertTrue(h.ui.pumpUntil({ !h.delays.pending.isEmpty }, timeout: 8))

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

        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        state.refreshSource(for: "T")
        XCTAssertTrue(state.sourceErrors.isEmpty)
    }

    func testAPendingUpdateFoundAtLaunchIsNotAnnouncedTwice() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let url = h.dir.file("data-team.json")
        let first = h.create()
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(first.subscribe(documentAt: url.path, as: nil))
        first.dispose()

        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["github"] = nil
        try writeDocument(doc, at: url)
        h.notifier.clearSent()

        let second = h.create()
        XCTAssertEqual(second.pendingUpdates["Data team"]?.summary(), "removes github")
        XCTAssertTrue(h.notifier.sent.isEmpty, "the banner already says it; a launch is not news")
        second.reload()
        XCTAssertTrue(h.notifier.sent.isEmpty, "and the reload behind it must not announce it either")
    }

    func testApplyingAnInactiveCollectionLeavesClaudesConfigAlone() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
        XCTAssertEqual(state.activeCollection, "Default")
        let before = try h.claudeServers()

        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["github"] = nil
        try writeDocument(doc, at: url)
        try TempDir.bumpModificationDate(of: url)
        XCTAssertTrue(h.ui.pumpUntil({ state.pendingUpdates["Data team"] != nil }, timeout: 8))

        XCTAssertNil(state.applyPendingUpdate(for: "Data team"))
        XCTAssertNil(state.store.collections["Data team"]?.mcps["github"])
        XCTAssertEqual(try h.claudeServers(), before, "Claude runs the active collection, and that one did not change")
        XCTAssertFalse(state.needsClaudeRestart)
    }

    func testLocateAppliesTheExpandedPathAtOnce() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("tools/servers.json")
        try writeDocument(oneLocalConnector("x", command: "node", args: ["\(Placeholder.directoryToken)/srv.js"]), at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
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
        let url = h.dir.file("tools/servers.json")
        try writeDocument(oneLocalConnector("x", command: "node", args: ["\(Placeholder.directoryToken)/srv.js"]), at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
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
        XCTAssertNil(document.connectors["new"]?.env.keys.first, "a disabled connector still travels")
        XCTAssertNil(state.publishError)
    }

    func testASyncedCollectionCannotBePublished() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
        let folder = try publishFolder(h)

        // A synced collection has an author elsewhere, and nothing in the window offers Publish
        // for one — the toolbar swaps it for Refresh and Make Local Copy. The refusal is the
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
        try writeDocument(CollectionDocumentSamples.dataTeam, at: folder.appendingPathComponent(fileName))
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

        XCTAssertNil(state.updatePublishIntent(state.activeCollection,
                                               intent: PublishIntent(shareValues: ["ledger": ["B"]], pathMarks: [:], hints: [:])))
        XCTAssertEqual(try CollectionDocument.decode(try Data(contentsOf: file)).connectors["ledger"]?.env["B"], .value("us"))
        XCTAssertEqual(state.collectionsFile.collections[state.activeCollection]?.publish?.intent.shareValues,
                       ["ledger": ["B"]], "what was ticked is remembered for the next write")
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
        var store = try h.storeOnDisk()
        store.collections[store.activeCollection]?.mcps["elsewhere"] = newConnector("z")
        try MasterStoreIO.save(store, to: h.masterStoreURL)
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
                       PublishModel.unresolvedMarkNote("ledger"), "an export refuses too")
        XCTAssertFalse(FileManager.default.fileExists(atPath: exported.path))

        // Re-ticking in the Publish sheet records the path where it is now, and clears it.
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        let row = try XCTUnwrap(sheet.pathRows.firstIndex { $0.connector == "ledger" && $0.value == edited })
        XCTAssertFalse(sheet.pathRows[row].marked, "a mark that lost its argument ticks nothing")
        XCTAssertEqual(sheet.pathRows[row].name, "server_path", "the row that could be the lost path carries its name")
        XCTAssertEqual(sheet.pathRows[row].hint, "your ledger clone", "and its hint")
        sheet.pathRows[row].marked = true
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
        XCTAssertEqual(intent.pathMarks["books"]?.values.first?.value, markedPath)
        let document = try CollectionDocument.decode(try Data(contentsOf: file))
        XCTAssertEqual(document.connectors["books"]?.needs.keys.sorted(), ["server_path"])

        state.remove(name: "books")
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
        var store = try h.storeOnDisk()
        let entry = try XCTUnwrap(store.collections[store.activeCollection]?.mcps.removeValue(forKey: "ledger"))
        store.collections[store.activeCollection]?.mcps["books"] = entry
        try MasterStoreIO.save(store, to: h.masterStoreURL)
        state.reload(trigger: .externalStoreAdoption)
        XCTAssertEqual(state.publishError?.message, AppState.pathMarkMovedError("ledger"))
        XCTAssertEqual(try Data(contentsOf: file), before)
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
        XCTAssertEqual(state.publishError?.message, AppState.pathMarkMovedError("ledger"))
        XCTAssertEqual(try Data(contentsOf: file), before, "the path is still in the master list here, so nothing is written")
        XCTAssertFalse(try jsonFile(file, contains: markedPath))

        // Its master list follows, without the row: nothing left to keep back.
        var store = try h.storeOnDisk()
        store.collections[store.activeCollection]?.mcps["ledger"] = MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string("--quiet")]),
        ]))
        try MasterStoreIO.save(store, to: h.masterStoreURL)
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
        XCTAssertEqual(state.publishError?.message, AppState.pathMarkMovedError("ledger"))
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
        var store = try h.storeOnDisk()
        store.collections[store.activeCollection]?.mcps.removeValue(forKey: "ledger")
        try MasterStoreIO.save(store, to: h.masterStoreURL)
        try sidecar.save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)

        let relaunched = h.create()
        defer { relaunched.dispose() }
        XCTAssertNotNil(relaunched.store.collections[relaunched.activeCollection]?.mcps["ledger"],
                        "Claude's config brought it back")
        XCTAssertEqual(relaunched.publishError?.message, AppState.pathMarkMovedError("ledger"))
        XCTAssertEqual(try Data(contentsOf: file), before)
    }

    func testAMarkedPathInsideAnotherStringIsNotPublished() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, file) = try publishMarkedLedger(h, state, args: [markedPath])
        let before = try Data(contentsOf: file)
        // Still marked where it was, and now also inside a flag nothing marks.
        rewriteLedger(state, args: [markedPath, "--config=\(markedPath).config"])
        XCTAssertEqual(state.publishError?.message, AppState.pathMarkMovedError("ledger"))
        XCTAssertEqual(try Data(contentsOf: file), before)
    }

    func testOnlyTheSheetsPublishTakesAPathOffTheList() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let (_, file) = try publishMarkedLedger(h, state, args: [markedPath])
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        let row = try XCTUnwrap(sheet.pathRows.firstIndex { $0.value == markedPath })
        XCTAssertTrue(sheet.pathRows[row].marked)
        // The author unticks it, reads the preview, and presses Publish: that is the reviewed answer.
        sheet.pathRows[row].marked = false
        XCTAssertTrue(jsonText(sheet.preview, contains: markedPath), "the preview shows the path as it will travel")
        XCTAssertNil(sheet.publish())
        XCTAssertNil(state.publishError)
        XCTAssertEqual(markedValues(state), [])
        XCTAssertEqual(try ledgerArgs(in: file), [markedPath])
    }

    func testTheSheetsExportRefusesACopyOfAPathItMarks() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(markedPath), .string("--config=\(markedPath).config")]),
        ])), renamedFrom: nil))
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        let row = try XCTUnwrap(sheet.pathRows.firstIndex { $0.value == markedPath })
        sheet.pathRows[row].marked = true
        let out = h.dir.file("away/copy.json")
        XCTAssertEqual(sheet.export(to: out.path), AppState.pathMarkMovedError("ledger"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: out.path))
        XCTAssertEqual(sheet.preview, AppState.pathMarkMovedError("ledger"), "the preview says why rather than show it")
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
        var store = try h.storeOnDisk()
        let entry = try XCTUnwrap(store.collections[store.activeCollection]?.mcps.removeValue(forKey: "ledger"))
        store.collections[store.activeCollection]?.mcps["books"] = entry
        try MasterStoreIO.save(store, to: h.masterStoreURL)
        try sidecar.save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)

        let relaunched = h.create()
        defer { relaunched.dispose() }
        XCTAssertNotNil(relaunched.store.collections[relaunched.activeCollection]?.mcps["ledger"], "the old name came back")
        XCTAssertEqual(relaunched.publishError?.message, AppState.pathMarkMovedError("ledger"))
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
        let carriers: [(name: String, config: JSONValue)] = [
            ("in the command", .object(["command": .string(markedPath + "/bin/start"), "args": .array([])])),
            ("in a remote's arguments", remote),
            ("in an additional field", .object(["command": .string("node"), "args": .array([.string("x.js")]),
                                                "cwd": .string(markedPath)])),
        ]
        for carrier in carriers {
            XCTAssertNil(state.upsert(name: carrier.name, entry: MCPEntry(config: carrier.config), renamedFrom: nil))
            XCTAssertEqual(state.publishError?.message, AppState.pathMarkMovedError(carrier.name), carrier.name)
            XCTAssertEqual(state.publishError?.kind, .blockedForReview, carrier.name)
            XCTAssertEqual(try Data(contentsOf: file), before, carrier.name)
            state.remove(name: carrier.name)
            XCTAssertNil(state.publishError, "with it gone there is nothing left to keep back")
        }
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

        // Removed since the backup was taken, the connector comes back with the folder collapsed.
        state.remove(name: "x")
        try state.restoreClaudeConfig(from: backup)
        XCTAssertEqual(args(of: try XCTUnwrap(state.store.collections[state.activeCollection]?.mcps["x"]?.config)),
                       ["\(Placeholder.directoryToken)/tools/x.js"])
        XCTAssertFalse(try jsonFile(document, contains: folder))
        XCTAssertNil(state.publishError)
    }

    /// Removed on the other machine while this one was off: at launch Claude's config brings the
    /// connector back with this machine's folder in it, which is written back as the token.
    func testALaunchIngestTakesTheTokenBackForThePublishFolder() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let first = h.create()
        let (document, folder) = try publishTokenConnector(h, first)
        first.dispose()
        var store = try h.storeOnDisk()
        store.collections[store.activeCollection]?.mcps.removeValue(forKey: "x")
        try MasterStoreIO.save(store, to: h.masterStoreURL)

        let relaunched = h.create()
        defer { relaunched.dispose() }
        XCTAssertEqual(args(of: try XCTUnwrap(relaunched.store.collections[relaunched.activeCollection]?.mcps["x"]?.config)),
                       ["\(Placeholder.directoryToken)/tools/x.js"], "ingested with the token, not the folder")
        XCTAssertFalse(try jsonFile(document, contains: folder))
        XCTAssertNil(relaunched.publishError)
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
        XCTAssertEqual(state.publishError?.message, AppState.publishFolderCarriedError("typed"))
        XCTAssertEqual(state.publishError?.kind, .blockedForReview, "answered in the Publish sheet, not another folder")
        XCTAssertEqual(try Data(contentsOf: file), withSibling)

        let sheet = PublishModel(state: state, collection: state.activeCollection)
        let out = h.dir.file("away/copy.json")
        XCTAssertEqual(sheet.export(to: out.path), AppState.publishFolderCarriedError("typed"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: out.path))
        XCTAssertEqual(sheet.preview, AppState.publishFolderCarriedError("typed"))
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
        try writeDocument(importableDocument, at: url)

        XCTAssertNil(state.importCopies(documentAt: url.path, into: "Default", choices: [:], date: "2026-09-21"))
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
                       CollectionsFile.Provenance(from: "Data team", author: "Acme Data Platform", date: "2026-09-21"))
        XCTAssertEqual(state.connectorCaution("dbt", in: "Default"), AppState.needsValueCaution("DBT_TOKEN"))
        XCTAssertEqual(try h.claudeServers().keys.sorted(), ["aws-mcp", "scoutbook", "service-now"],
                       "nothing that arrives off reaches Claude")
        XCTAssertEqual(state.importCopies(documentAt: url.path, into: "Nowhere", choices: [:], date: "2026-09-21"), nil,
                       "a collection that does not exist is a no-op, as switching to one is")
    }

    func testReplaceKeepsAFilledValueAndKeepBothSuffixes() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("shared/data-team.json")
        try writeDocument(importableDocument, at: url)
        XCTAssertNil(state.importCopies(documentAt: url.path, into: "Default", choices: [:], date: "2026-09-21"))

        // The user fills the token and turns dbt on.
        var dbt = try XCTUnwrap(state.store.collections["Default"]?.mcps["dbt"])
        dbt.config = try XCTUnwrap(dbt.config.replacing(at: JSONPointer(["env", "DBT_TOKEN"]), with: .string("tok")))
        dbt.enabled = true
        XCTAssertNil(state.upsert(name: "dbt", entry: dbt, renamedFrom: "dbt"))

        // The author ships a new dbt, and the same document is imported again.
        var doc = importableDocument
        doc.connectors["dbt"]?.launcher = .local(.init(command: "npx", args: ["-y", "@dbt/mcp@2"], platform: .current))
        try writeDocument(doc, at: url)
        XCTAssertNil(state.importCopies(documentAt: url.path, into: "Default",
                                        choices: ["dbt": .replace, "github": .keepBoth, "notion": .skip, "ledger": .skip],
                                        date: "2026-09-22"))
        let mcps = try XCTUnwrap(state.store.collections["Default"]).mcps
        XCTAssertEqual(mcps["dbt"]?.config.value(at: JSONPointer(["args", "1"])), .string("@dbt/mcp@2"))
        XCTAssertEqual(mcps["dbt"]?.config.value(at: JSONPointer(["env", "DBT_TOKEN"])), .string("tok"),
                       "Replace keeps what the user filled in")
        XCTAssertEqual(mcps["dbt"]?.enabled, true, "replacing a connector that was on leaves it on")
        XCTAssertNotNil(mcps["github 2"], "Keep both lands beside what is already there")
        XCTAssertEqual(mcps["github 2"]?.enabled, false)
        XCTAssertEqual(mcps.keys.filter { $0.hasPrefix("notion") }.sorted(), ["notion"], "Skip leaves it alone")
        XCTAssertEqual(state.collectionsFile.collections["Default"]?.provenance["dbt"]?.date, "2026-09-22")
        XCTAssertEqual(state.collectionsFile.collections["Default"]?.provenance["github 2"]?.from, "Data team")
        XCTAssertEqual(try h.claudeServers()["dbt"]?.value(at: JSONPointer(["args", "1"])), .string("@dbt/mcp@2"),
                       "a replaced connector that was on reaches Claude")

        // A third import with Keep both again numbers on from the highest suffix taken.
        XCTAssertNil(state.importCopies(documentAt: url.path, into: "Default",
                                        choices: ["github": .keepBoth, "dbt": .skip, "notion": .skip, "ledger": .skip],
                                        date: "2026-09-23"))
        XCTAssertNotNil(state.store.collections["Default"]?.mcps["github 3"])
    }

    func testMakeLocalCopyIntoALocalCollection() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("shared/data-team.json")
        try writeDocument(importableDocument, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
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
        let url = h.dir.file("shared/data-team.json")
        try writeDocument(importableDocument, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))

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
}
