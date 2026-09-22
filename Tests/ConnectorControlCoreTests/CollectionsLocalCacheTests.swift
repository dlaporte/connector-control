import XCTest
import ConnectorControlTestSupport
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/CollectionsLocalCacheTests.cs
final class CollectionsLocalCacheTests: XCTestCase {
    var tempDir: TempDir!
    override func setUpWithError() throws { tempDir = TempDir(prefix: "cache") }
    override func tearDownWithError() throws { tempDir.dispose() }

    static let sample = CollectionsLocalCache(
        synced: ["Data team": .init(path: "/Users/d/Acme/mcp/data-team.json", lastHash: "sha256:00", excluded: ["ledger": "reason"])],
        published: ["Consulting": .init(folder: "/Users/d/Acme/mcp", lastWrittenHash: nil,
                                        publishedFolders: ["/Users/d/Acme/mcp"])])

    func testRoundTripThroughDisk() throws {
        let url = tempDir.file("collections-local.json")
        try Self.sample.save(to: url, staging: nil)
        XCTAssertEqual(CollectionsLocalCache.load(from: url), Self.sample)
        XCTAssertEqual(CollectionsLocalCache.load(from: tempDir.file("missing.json")), CollectionsLocalCache(synced: [:], published: [:]))
    }

    func testReconcilePrunesBindingsWhoseCollectionChangedKind() {
        let file = CollectionsFile(collections: [
            "Data team": .init(kind: .local, fileName: nil, relativeToStore: nil, origin: nil, needs: [:], publish: nil, provenance: [:]),
        ])
        let pruned = Self.sample.reconciled(with: file)
        XCTAssertTrue(pruned.synced.isEmpty, "Data team is local now, so its source binding is gone")
        XCTAssertTrue(pruned.published.isEmpty, "Consulting has no publish record in the sidecar")
    }

    /// The two bindings the sidecar still vouches for survive reconciliation untouched.
    func testReconcileKeepsBindingsTheSidecarStillVouchesFor() {
        let file = CollectionsFile(collections: [
            "Data team": .init(kind: .synced, fileName: "data-team.json", relativeToStore: nil, origin: nil, needs: [:], publish: nil, provenance: [:]),
            "Consulting": .init(kind: .local, fileName: nil, relativeToStore: nil, origin: nil, needs: [:],
                                publish: .init(slug: "consulting", origin: "0c9b7d1e", intent: .none), provenance: [:]),
        ])
        XCTAssertEqual(Self.sample.reconciled(with: file), Self.sample)
    }

    /// The list of marked paths round-trips sorted, is left out while empty, and a cache written
    /// before it was kept loads with an empty one.
    func testMarkedValuesRoundTripAndAreEmptyInAnOlderCache() throws {
        let marked = CollectionsLocalCache(synced: [:], published: [
            "Consulting": .init(folder: "/Users/d/Acme/mcp", lastWrittenHash: "sha256:01",
                                markedValues: ["/Users/d/ledger.js", "/Users/d/b.js"],
                                publishedFolders: ["/Users/d/Acme/mcp"]),
        ])
        XCTAssertEqual(try CollectionsLocalCache.decode(marked.encode()), marked)
        XCTAssertEqual(marked.encode().value(at: JSONPointer(["published", "Consulting", "markedValues"])),
                       .array([.string("/Users/d/b.js"), .string("/Users/d/ledger.js")]))
        XCTAssertNil(Self.sample.encode().value(at: JSONPointer(["published", "Consulting", "markedValues"])))

        let older = try JSONValue.parse(Data("""
            {"version": 1, "synced": {}, "published": {"Consulting": {"folder": "/Users/d/Acme/mcp"}}}
            """.utf8))
        XCTAssertEqual(try CollectionsLocalCache.decode(older).published["Consulting"]?.markedValues, [])
    }

    func testReleasedValuesAndTheLastAppliedCollectionRoundTripAndAreAbsentInAnOlderCache() throws {
        let cache = CollectionsLocalCache(synced: [:], published: [
            "Consulting": .init(folder: "/Users/d/Acme/mcp", lastWrittenHash: nil, markedValues: ["/a"], releasedValues: ["/b"],
                                publishedFolders: ["/Users/d/old", "/Users/d/Acme/mcp"]),
        ], lastAppliedCollection: "Consulting")
        XCTAssertEqual(try CollectionsLocalCache.decode(cache.encode()), cache)
        XCTAssertEqual(cache.reconciled(with: CollectionsFile(collections: [:])).lastAppliedCollection, "Consulting",
                       "which collection Claude's file holds is not a binding to prune")
        let older = try CollectionsLocalCache.decode(Self.sample.encode())
        XCTAssertNil(older.lastAppliedCollection)
        XCTAssertEqual(older.published["Consulting"]?.releasedValues, [])
    }

    func testWhatAStoppedPublishLeftBehindRoundTripsAndOutlivesThePrune() throws {
        let cache = CollectionsLocalCache(synced: [:], published: [:], kept: [
            "Consulting": .init(markedValues: ["/a"], releasedValues: ["/b"], publishedFolders: ["/Users/d/old"]),
            "Empty": .init(),
        ])
        let decoded = try CollectionsLocalCache.decode(cache.encode())
        XCTAssertEqual(decoded.kept["Consulting"], cache.kept["Consulting"])
        XCTAssertNil(decoded.kept["Empty"], "a record with nothing to say is not written")
        XCTAssertEqual(decoded.reconciled(with: CollectionsFile(collections: [:])).kept, decoded.kept,
                       "no sidecar vouches for it, and it is kept all the same")
        let older = try JSONValue.parse(Data("""
            {"version": 1, "synced": {}, "published": {"Consulting": {"folder": "/Users/d/Acme/mcp"}}}
            """.utf8))
        XCTAssertEqual(try CollectionsLocalCache.decode(older).kept, [:])
        XCTAssertEqual(try CollectionsLocalCache.decode(older).published["Consulting"]?.publishedFolders,
                       ["/Users/d/Acme/mcp"], "a binding knows it publishes into the folder it names")
    }

    /// A publish binding the sidecar no longer vouches for is a collection deleted, or stopped, on
    /// another machine. What it kept back outlives it, exactly as Stop Publishing here leaves it.
    func testReconcileKeepsWhatADroppedPublishBindingKeptBack() {
        let cache = CollectionsLocalCache(synced: [:], published: [
            "Consulting": .init(folder: "/Users/d/new", lastWrittenHash: nil, markedValues: ["/a"],
                                releasedValues: ["/b"], publishedFolders: ["/Users/d/old"], origin: "0c9b7d1e"),
        ], kept: ["Consulting": .init(markedValues: ["/earlier"])])
        let pruned = cache.reconciled(with: CollectionsFile(collections: [:]))
        XCTAssertTrue(pruned.published.isEmpty, "the sidecar no longer vouches for it")
        XCTAssertEqual(pruned.kept["Consulting"],
                       .init(markedValues: ["/a", "/earlier"], releasedValues: ["/b"],
                             publishedFolders: ["/Users/d/old", "/Users/d/new"], origin: "0c9b7d1e"),
                       "the binding's lists, its folder among them, merged with what was already remembered")
        XCTAssertEqual(pruned.reconciled(with: CollectionsFile(collections: [:])), pruned, "and folding it again changes nothing")
    }

    /// The origin a binding publishes under, and the names the last apply wrote, round-trip; a
    /// cache written before either was kept has neither, and an apply that rendered nothing
    /// records an empty list, which is not the same as no record at all.
    func testTheOriginAndTheNamesTheLastApplyWroteRoundTrip() throws {
        let cache = CollectionsLocalCache(
            synced: [:],
            published: ["Consulting": .init(folder: "/Users/d/Acme/mcp", lastWrittenHash: nil,
                                            publishedFolders: ["/Users/d/Acme/mcp"], origin: "0c9b7d1e")],
            kept: ["Gone": .init(publishedFolders: ["/Users/d/old"], origin: "5f2a")],
            lastAppliedCollection: "Consulting", lastAppliedNames: ["ledger", "scoutbook"])
        XCTAssertEqual(try CollectionsLocalCache.decode(cache.encode()), cache)
        let older = try CollectionsLocalCache.decode(Self.sample.encode())
        XCTAssertNil(older.lastAppliedNames, "a cache written before they were recorded names nothing")
        XCTAssertNil(older.published["Consulting"]?.origin)
        var empty = cache
        empty.lastAppliedNames = []
        XCTAssertEqual(try CollectionsLocalCache.decode(empty.encode()).lastAppliedNames, [],
                       "an apply that rendered nothing is a record of nothing, not the absence of one")
    }

    /// The folders of the collections that bore a record's name and left round-trip beside its
    /// own, and are enough on their own for the record to be written; a record written before they
    /// were kept apart reads with none.
    func testDepartedFoldersRoundTripAndAreEmptyInAnOlderCache() throws {
        let cache = CollectionsLocalCache(synced: [:], published: [:],
                                          kept: ["Team": .init(departedFolders: ["/Users/d/old"], origin: "5f2a")])
        XCTAssertEqual(try CollectionsLocalCache.decode(cache.encode()).kept["Team"], cache.kept["Team"],
                       "a record holding only what departed collections left has something to say")
        XCTAssertEqual(cache.encode().value(at: JSONPointer(["kept", "Team", "departedFolders"])),
                       .array([.string("/Users/d/old")]))
        let older = try JSONValue.parse(Data("""
            {"version": 1, "synced": {}, "published": {}, "kept": {"Team": {"publishedFolders": ["/Users/d/old"]}}}
            """.utf8))
        XCTAssertEqual(try CollectionsLocalCache.decode(older).kept["Team"]?.departedFolders, [])
    }

    /// An earlier record's folders are the binding's own only where the two published under one
    /// origin. A record of another origin, or of none — a collection that left the store — belongs
    /// to another collection, and its folders are kept apart as departed so the next publish does
    /// not take them as its own.
    func testRememberingKeepsAnotherCollectionsFoldersApartFromTheBindingsOwn() {
        let binding = CollectionsLocalCache.PublishBinding(folder: "/Users/d/new", lastWrittenHash: nil,
                                                           publishedFolders: ["/Users/d/new"], origin: "0c9b7d1e")
        let departed = CollectionsLocalCache.KeptRecord(publishedFolders: ["/Users/d/old"], departedFolders: ["/Users/d/older"])
        XCTAssertEqual(CollectionsLocalCache.KeptRecord.remembering(binding, after: departed),
                       .init(publishedFolders: ["/Users/d/new"], departedFolders: ["/Users/d/old", "/Users/d/older"],
                             origin: "0c9b7d1e"),
                       "a record with no origin belongs to no collection here")
        let another = CollectionsLocalCache.KeptRecord(publishedFolders: ["/Users/d/old"], origin: "5f2a")
        XCTAssertEqual(CollectionsLocalCache.KeptRecord.remembering(binding, after: another),
                       .init(publishedFolders: ["/Users/d/new"], departedFolders: ["/Users/d/old"], origin: "0c9b7d1e"),
                       "and one of another origin belongs to another collection")
    }

    /// A record of the binding's own origin is the same collection's, stopped before: its folders
    /// merge into the binding's own, and what it held as departed stays departed. A binding and a
    /// record written before origins were kept, with none on either side, are read the same way.
    func testRememberingMergesTheFoldersOfARecordOfTheSameOrigin() {
        let binding = CollectionsLocalCache.PublishBinding(folder: "/Users/d/new", lastWrittenHash: nil,
                                                           publishedFolders: ["/Users/d/new"], origin: "0c9b7d1e")
        let own = CollectionsLocalCache.KeptRecord(publishedFolders: ["/Users/d/old"], departedFolders: ["/Users/d/older"],
                                                   origin: "0c9b7d1e")
        XCTAssertEqual(CollectionsLocalCache.KeptRecord.remembering(binding, after: own),
                       .init(publishedFolders: ["/Users/d/old", "/Users/d/new"], departedFolders: ["/Users/d/older"],
                             origin: "0c9b7d1e"))
        let legacy = CollectionsLocalCache.PublishBinding(folder: "/Users/d/new", lastWrittenHash: nil)
        XCTAssertEqual(CollectionsLocalCache.KeptRecord.remembering(legacy, after: .init(publishedFolders: ["/Users/d/old"])),
                       .init(publishedFolders: ["/Users/d/old", "/Users/d/new"]))
    }

    /// A collection that never published moves no record, so a rename onto a name a departed
    /// collection left a record under leaves that record as it found it, belonging to none: the
    /// same reading a collection made with the name gets.
    func testRenamedWithNothingMovingLeavesTheDisplacedRecordAsItIs() {
        let displaced = CollectionsLocalCache.KeptRecord(markedValues: ["/a"], publishedFolders: ["/Users/d/old"],
                                                         departedFolders: ["/Users/d/older"])
        XCTAssertEqual(CollectionsLocalCache.KeptRecord.renamed(nil, over: displaced), displaced)
    }

    /// A live collection's name is refused, so a record displaced by a rename is a departed
    /// collection's: its paths are inherited, as a re-used name inherits them, and its folders,
    /// own and departed alike, are departed to the collection now bearing the name, whose own
    /// folders and origin the merged record keeps.
    func testRenamedFilesTheDisplacedRecordsFoldersAsDeparted() {
        let moving = CollectionsLocalCache.KeptRecord(markedValues: ["/c"], releasedValues: ["/d"],
                                                      publishedFolders: ["/Users/d/squad"], departedFolders: ["/Users/d/gone"],
                                                      origin: "0c9b7d1e")
        let displaced = CollectionsLocalCache.KeptRecord(markedValues: ["/a"], releasedValues: ["/b"],
                                                         publishedFolders: ["/Users/d/old"], departedFolders: ["/Users/d/older"])
        XCTAssertEqual(CollectionsLocalCache.KeptRecord.renamed(moving, over: displaced),
                       .init(markedValues: ["/a", "/c"], releasedValues: ["/b", "/d"], publishedFolders: ["/Users/d/squad"],
                             departedFolders: ["/Users/d/gone", "/Users/d/old", "/Users/d/older"], origin: "0c9b7d1e"))
    }

    func testAnUnknownVersionDecodesAsMalformed() {
        XCTAssertThrowsError(try CollectionsLocalCache.decode(.object(["version": .int(9), "synced": .object([:]), "published": .object([:])])))
    }
}
