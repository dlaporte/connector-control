import XCTest
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/JsonPointerTests.cs
final class JSONPointerTests: XCTestCase {
    private let config: JSONValue = .object([
        "command": .string("node"),
        "args": .array([.string("-y"), .string("server.js")]),
        "env": .object(["A": .string("1"), "B/C": .string("2")]),
    ])

    func testParsesAndPrintsWithEscapes() throws {
        let p = try XCTUnwrap(JSONPointer(string: "/env/B~1C"))
        XCTAssertEqual(p.segments, ["env", "B/C"])
        XCTAssertEqual(p.description, "/env/B~1C")
        XCTAssertNil(JSONPointer(string: "args/1"))
        XCTAssertEqual(JSONPointer(string: "")?.segments, [])
    }
    func testLooksUpKeysAndIndexes() throws {
        XCTAssertEqual(config.value(at: try XCTUnwrap(JSONPointer(string: "/args/1"))), .string("server.js"))
        XCTAssertEqual(config.value(at: try XCTUnwrap(JSONPointer(string: "/env/B~1C"))), .string("2"))
        XCTAssertNil(config.value(at: try XCTUnwrap(JSONPointer(string: "/args/7"))))
        XCTAssertNil(config.value(at: try XCTUnwrap(JSONPointer(string: "/command/x"))))
    }
    func testReplacesALeafAndLeavesTheRestAlone() throws {
        let replaced = try XCTUnwrap(config.replacing(at: try XCTUnwrap(JSONPointer(string: "/args/1")), with: .string("x.js")))
        XCTAssertEqual(replaced.value(at: JSONPointer(["args", "1"])), .string("x.js"))
        XCTAssertEqual(replaced.value(at: JSONPointer(["command"])), .string("node"))
        XCTAssertNil(config.replacing(at: JSONPointer(["args", "9"]), with: .string("x")))
    }
    func testStringLeavesAreDepthFirstWithSortedKeys() {
        XCTAssertEqual(config.stringLeaves.map { $0.pointer.description }, ["/args/0", "/args/1", "/command", "/env/A", "/env/B~1C"])
    }
}
