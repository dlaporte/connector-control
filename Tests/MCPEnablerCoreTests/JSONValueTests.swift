import XCTest
@testable import MCPEnablerCore

final class JSONValueTests: XCTestCase {
    func testParseAndSerializeRoundTrip() throws {
        let json = #"{"a": 1, "b": "two", "c": [true, null, 2.5], "d": {"e": []}}"#
        let value = try JSONValue.parse(Data(json.utf8))
        XCTAssertEqual(value, .object([
            "a": .int(1),
            "b": .string("two"),
            "c": .array([.bool(true), .null, .double(2.5)]),
            "d": .object(["e": .array([])]),
        ]))
        let reparsed = try JSONValue.parse(try value.serialized())
        XCTAssertEqual(reparsed, value)
    }

    func testIntStaysIntThroughSerialization() throws {
        let data = try JSONValue.object(["n": .int(3)]).serialized()
        XCTAssertTrue(String(decoding: data, as: UTF8.self).contains("\"n\" : 3"))
    }

    func testAnyValueRoundTrip() throws {
        let any: [String: Any] = ["s": "x", "i": 7, "d": 1.5, "b": true,
                                  "n": NSNull(), "a": [1, "y"], "o": ["k": false]]
        let value = try JSONValue(any: any)
        let back = try XCTUnwrap(value.anyValue as? [String: Any])
        let data = try JSONSerialization.data(withJSONObject: back, options: [.sortedKeys])
        let expected = try JSONSerialization.data(
            withJSONObject: any, options: [.sortedKeys])
        XCTAssertEqual(data, expected)
    }

    func testBoolIsNotConfusedWithInt() throws {
        let value = try JSONValue(any: ["t": true, "one": 1])
        XCTAssertEqual(value, .object(["t": .bool(true), "one": .int(1)]))
    }

    func testWholeValuedFloatsCanonicalizeToInt() throws {
        let value = try JSONValue(any: ["x": 2.0, "y": 2.5])
        XCTAssertEqual(value, .object(["x": .int(2), "y": .double(2.5)]))

        // Parse round-trip stability: re-parsing a serialized whole-valued
        // float must not flip it back and forth.
        let parsed = try JSONValue.parse(Data(#"{"x": 2.0}"#.utf8))
        let reparsed = try JSONValue.parse(try parsed.serialized())
        XCTAssertEqual(reparsed, parsed)
    }

    func testTypeName() {
        XCTAssertEqual(JSONValue.object([:]).typeName, "object")
        XCTAssertEqual(JSONValue.array([]).typeName, "array")
        XCTAssertEqual(JSONValue.string("").typeName, "string")
    }
}
