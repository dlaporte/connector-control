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
}
