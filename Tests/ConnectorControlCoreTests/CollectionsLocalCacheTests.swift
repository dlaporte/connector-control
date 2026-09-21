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
        published: ["Consulting": .init(folder: "/Users/d/Acme/mcp", lastWrittenHash: nil)])

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

    func testAnUnknownVersionDecodesAsMalformed() {
        XCTAssertThrowsError(try CollectionsLocalCache.decode(.object(["version": .int(9), "synced": .object([:]), "published": .object([:])])))
    }
}
