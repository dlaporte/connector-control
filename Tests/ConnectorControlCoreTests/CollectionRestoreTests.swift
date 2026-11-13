import XCTest
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/CollectionRestoreTests.cs
final class CollectionRestoreTests: XCTestCase {
    private func entry(_ command: String, on: Bool = true, view: EditView = .form) -> MCPEntry {
        MCPEntry(enabled: on, config: .object(["command": .string(command)]), lastEditView: view)
    }

    func testEachConnectorComesBackChangesGoesOrStays() {
        let current = Collection(mcps: [
            "same": entry("s"),
            "config": entry("old"),
            "switch": entry("w", on: true),
            "added-since": entry("a"),
        ])
        let restored = Collection(mcps: [
            "same": entry("s"),
            "config": entry("new", on: false),
            "switch": entry("w", on: false),
            "gone-since": entry("g", on: true),
        ])
        let changes = CollectionRestore.changes(current: current, restored: restored)
        XCTAssertEqual(changes.byName, [
            "config": .changes(on: false),
            "switch": .changes(on: false),
            "gone-since": .comesBack(on: true),
            "added-since": .goes,
        ])
        XCTAssertEqual(changes.unchanged, 1)
        XCTAssertFalse(changes.isEmpty)
    }

    /// A collection that is gone has nothing to compare with: everything comes back, on or off as it was.
    func testEverythingComesBackIntoACollectionThatIsGone() {
        let restored = Collection(mcps: ["a": entry("a", on: true), "b": entry("b", on: false)])
        let changes = CollectionRestore.changes(current: nil, restored: restored)
        XCTAssertEqual(changes.byName, ["a": .comesBack(on: true), "b": .comesBack(on: false)])
        XCTAssertEqual(changes.unchanged, 0)
    }

    /// Which view the editor last showed is not something a restore is asked to put back.
    func testOnlyTheConfigAndTheSwitchMakeAConnectorDiffer() {
        let current = Collection(mcps: ["a": entry("a", view: .json)])
        let restored = Collection(mcps: ["a": entry("a", view: .form)])
        let changes = CollectionRestore.changes(current: current, restored: restored)
        XCTAssertTrue(changes.isEmpty)
        XCTAssertEqual(changes.unchanged, 1)
    }

    /// Names compare as the store compares them: another spelling of a name is the same connector.
    func testAnotherSpellingOfANameIsTheSameConnector() {
        let current = Collection(mcps: ["caf\u{E9}": entry("c")])
        let restored = Collection(mcps: ["cafe\u{301}": entry("c")])
        XCTAssertEqual(CollectionRestore.changes(current: current, restored: restored),
                       CollectionRestore.changes(current: restored, restored: restored))
        XCTAssertTrue(CollectionRestore.changes(current: current, restored: restored).isEmpty)
    }

    /// A config's own text is compared scalar by scalar: a value composed another way is a change.
    func testAConfigComposedAnotherWayIsAChange() {
        let current = Collection(mcps: ["a": entry("caf\u{E9}")])
        let restored = Collection(mcps: ["a": entry("cafe\u{301}")])
        XCTAssertEqual(CollectionRestore.changes(current: current, restored: restored).byName, ["a": .changes(on: true)])
    }
}
