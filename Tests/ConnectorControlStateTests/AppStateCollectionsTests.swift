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
        state.publishError = (collection: "Team", message: "no room")

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
        state.publishError = (collection: "Default", message: "the folder is read-only")
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
}
