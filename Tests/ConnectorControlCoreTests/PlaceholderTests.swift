import XCTest
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/PlaceholderTests.cs
final class PlaceholderTests: XCTestCase {
    func testMarkerSyntax() {
        XCTAssertEqual(Placeholder.marker("DBT_TOKEN"), "${CC_NEEDS:DBT_TOKEN}")
        XCTAssertTrue(Placeholder.isValidName("server_path2"))
        XCTAssertFalse(Placeholder.isValidName("bad-name"))
        XCTAssertFalse(Placeholder.isValidName(""))
    }
    func testFindsNamesInOrderWithoutDuplicates() {
        XCTAssertEqual(Placeholder.names(in: "Bearer ${CC_NEEDS:token} ${CC_NEEDS:token} ${CC_NEEDS:x}"), ["token", "x"])
        XCTAssertEqual(Placeholder.names(in: "${CC_NEEDS:bad-name} plain"), [])
        XCTAssertTrue(Placeholder.containsMarker("a ${CC_NEEDS:b} c"))
        XCTAssertFalse(Placeholder.containsMarker("${COLLECTION_DIR}/x"))
    }
    func testMarkersInAConfigReportPointers() {
        let config: JSONValue = .object([
            "args": .array([.string("${CC_NEEDS:server_path}")]),
            "env": .object(["AUTH_HEADER": .string("Bearer ${CC_NEEDS:token}"), "PLAIN": .string("v")]),
        ])
        let found = Placeholder.markers(in: config)
        XCTAssertEqual(found.map { $0.pointer.description }, ["/args/0", "/env/AUTH_HEADER"])
        XCTAssertEqual(found.map { $0.names }, [["server_path"], ["token"]])
    }

    /// Flattened in the order the leaves are walked, so a sentence built from these names reads
    /// the same wherever it is built and however often a name appears.
    func testUnfilledNamesFlattenTheMarkersInFirstAppearanceOrder() {
        let config: JSONValue = .object([
            "args": .array([.string("${CC_NEEDS:zulu}"), .string("${CC_NEEDS:zulu} ${CC_NEEDS:alpha}")]),
            "env": .object(["A": .string("${CC_NEEDS:alpha}"), "B": .string("plain")]),
        ])
        XCTAssertEqual(Placeholder.unfilledNames(in: config), ["zulu", "alpha"])
        XCTAssertEqual(Placeholder.unfilledNames(in: .object(["a": .string("none")])), [])
    }
    func testExpandsTheDirectoryTokenInEveryStringLeaf() {
        let config: JSONValue = .object([
            "command": .string("node"),
            "args": .array([.string("${COLLECTION_DIR}/../servers/x.js")]),
            "env": .object(["ROOT": .string("${COLLECTION_DIR}")]),
        ])
        XCTAssertTrue(Placeholder.usesDirectoryToken(config))
        let expanded = Placeholder.expandDirectoryToken(in: config, directory: "/Users/d/Acme/mcp")
        XCTAssertEqual(expanded.value(at: JSONPointer(["args", "0"])), .string("/Users/d/Acme/mcp/../servers/x.js"))
        XCTAssertEqual(expanded.value(at: JSONPointer(["env", "ROOT"])), .string("/Users/d/Acme/mcp"))
        XCTAssertFalse(Placeholder.usesDirectoryToken(expanded))
    }
}
