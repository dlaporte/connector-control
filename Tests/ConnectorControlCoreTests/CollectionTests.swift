import XCTest
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/CollectionTests.cs
final class CollectionTests: XCTestCase {
    private func entry(_ url: String) -> MCPEntry {
        MCPEntry(config: RemotePattern.make(url: url))
    }

    // MARK: - Decoding

    func testV2RoundTripPreservesTwoCollections() throws {
        let store = MasterStore(
            activeCollection: "Work",
            collections: [
                "Work": Collection(mcps: ["a": entry("https://a.example/mcp")]),
                "Personal": Collection(mcps: ["b": entry("https://b.example/mcp")]),
            ])
        let data = try JSONEncoder().encode(store)
        let decoded = try JSONDecoder().decode(MasterStore.self, from: data)
        XCTAssertEqual(decoded, store)
        XCTAssertEqual(decoded.activeCollection, "Work")
        XCTAssertEqual(decoded.collections["Work"]?.mcps.keys.sorted(), ["a"])
        XCTAssertEqual(decoded.collections["Personal"]?.mcps.keys.sorted(), ["b"])
    }

    // MARK: - mcps accessor scoping

    func testMcpsAccessorReadsAndWritesOnlyActiveCollection() {
        var store = MasterStore(
            activeCollection: "Work",
            collections: [
                "Work": Collection(mcps: ["a": entry("https://a.example/mcp")]),
                "Personal": Collection(mcps: ["b": entry("https://b.example/mcp")]),
            ])
        store.mcps["c"] = entry("https://c.example/mcp")
        XCTAssertEqual(store.collections["Work"]?.mcps.keys.sorted(), ["a", "c"])
        XCTAssertEqual(store.collections["Personal"]?.mcps.keys.sorted(), ["b"], "untouched")
    }

    // MARK: - Collection management

    func testAddCollectionCopyingCurrent() {
        var store = MasterStore.single(["a": entry("https://a.example/mcp")])
        let error = store.addCollection(named: "Copy", copyingCurrent: true)
        XCTAssertNil(error)
        XCTAssertEqual(store.activeCollection, "Copy")
        XCTAssertEqual(store.collections["Copy"]?.mcps.keys.sorted(), ["a"])
        XCTAssertEqual(store.collections["Default"]?.mcps.keys.sorted(), ["a"], "original untouched")
    }

    func testAddCollectionEmptyStartsBlank() {
        var store = MasterStore.single(["a": entry("https://a.example/mcp")])
        let error = store.addCollection(named: "Fresh", copyingCurrent: false)
        XCTAssertNil(error)
        XCTAssertEqual(store.collections["Fresh"]?.mcps, [:])
    }

    func testAddCollectionWithoutActivatingLeavesTheActiveOne() {
        var store = MasterStore.single(["a": entry("https://a.example/mcp")])
        XCTAssertNil(store.addCollection(named: "Fresh", copyingCurrent: false, activating: false))
        XCTAssertEqual(store.collections["Fresh"]?.mcps, [:])
        XCTAssertEqual(store.activeCollection, "Default")
    }

    func testAddCollectionRejectsEmptyName() {
        var store = MasterStore.empty
        XCTAssertEqual(store.addCollection(named: "   ", copyingCurrent: false), "Name must not be empty.")
    }

    func testAddCollectionRejectsDuplicateName() {
        var store = MasterStore.empty
        XCTAssertEqual(store.addCollection(named: "Default", copyingCurrent: false),
                       "A collection named “Default” already exists.")
    }

    func testRenameActiveCollection() {
        var store = MasterStore.empty
        let error = store.renameCollection(store.activeCollection, to: "Main")
        XCTAssertNil(error)
        XCTAssertEqual(store.activeCollection, "Main")
        XCTAssertEqual(Array(store.collections.keys), ["Main"])
    }

    func testRenameActiveCollectionRejectsCollision() {
        var store = MasterStore(
            activeCollection: "Work",
            collections: ["Work": Collection(), "Personal": Collection()])
        XCTAssertEqual(store.renameCollection(store.activeCollection, to: "Personal"), "A collection named “Personal” already exists.")
        XCTAssertEqual(store.activeCollection, "Work", "unchanged on error")
    }

    func testRenameActiveCollectionRejectsEmptyName() {
        var store = MasterStore.empty
        XCTAssertNotNil(store.renameCollection(store.activeCollection, to: "  "))
    }

    func testDeleteActiveCollectionSwitchesToFirstRemaining() {
        var store = MasterStore(
            activeCollection: "Work",
            collections: ["Work": Collection(), "Alpha": Collection(), "Zeta": Collection()])
        let error = store.deleteCollection(named: store.activeCollection)
        XCTAssertNil(error)
        XCTAssertEqual(store.activeCollection, "Alpha")
        XCTAssertNil(store.collections["Work"])
    }

    func testDeleteActiveCollectionRejectsLastCollection() {
        var store = MasterStore.empty
        XCTAssertEqual(store.deleteCollection(named: store.activeCollection), "Can’t delete the last collection.")
        XCTAssertEqual(store.collections.count, 1)
    }

    func testSwitchCollection() {
        var store = MasterStore(
            activeCollection: "Work",
            collections: ["Work": Collection(), "Personal": Collection()])
        XCTAssertNil(store.switchCollection(to: "Personal"))
        XCTAssertEqual(store.activeCollection, "Personal")
    }

    func testSwitchCollectionRejectsUnknownName() {
        var store = MasterStore.empty
        XCTAssertEqual(store.switchCollection(to: "Nope"), "No collection named “Nope”.")
        XCTAssertEqual(store.activeCollection, "Default")
    }

    func testErrorMessagesUseTypographicPunctuationLikeTheMacApp() {
        var store = MasterStore.empty
        let duplicate = store.addCollection(named: "Default", copyingCurrent: false)!
        XCTAssertEqual(duplicate[duplicate.index(before: duplicate.range(of: "Default")!.lowerBound)], "“")
        XCTAssertEqual(duplicate[duplicate.range(of: "Default")!.upperBound], "”")
        XCTAssertTrue(store.deleteCollection(named: store.activeCollection)!.contains("’"))
    }
}
