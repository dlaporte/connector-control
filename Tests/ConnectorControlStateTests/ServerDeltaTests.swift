import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

final class ServerDeltaTests: XCTestCase {
    private func server(_ command: String) -> JSONValue { .object(["command": .string(command)]) }

    func testClassifiesAddedRemovedAndChangedByName() {
        let before = ["a": server("npx"), "b": server("uvx"), "c": server("node")]
        let after = ["b": server("uvx"), "c": server("deno"), "d": server("npx")]
        let delta = ServerDelta(from: before, to: after)
        XCTAssertEqual(delta.added, ["d"])
        XCTAssertEqual(delta.removed, ["a"])
        XCTAssertEqual(delta.changed, ["c"])
        XCTAssertFalse(delta.isEmpty)
        XCTAssertEqual(delta.summary(), "adds d; removes a; changes c")
    }

    func testIdenticalSetsAreEmpty() {
        let servers = ["a": server("npx")]
        XCTAssertTrue(ServerDelta(from: servers, to: servers).isEmpty)
        XCTAssertEqual(ServerDelta(from: servers, to: servers).summary(), "")
    }

    func testLongListsAreCappedAndSorted() {
        let after = Dictionary(uniqueKeysWithValues: ["f", "e", "d", "c", "b", "a"].map { ($0, server("npx")) })
        let delta = ServerDelta(from: [:], to: after)
        XCTAssertEqual(delta.summary(), "adds a, b, c, d and 2 more")
        XCTAssertEqual(delta.summary(limit: 6), "adds a, b, c, d, e, f")
    }
}
