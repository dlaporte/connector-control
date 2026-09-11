import XCTest
@testable import ConnectorControlCore

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
        let value = JSONValue(any: any)
        let back = try XCTUnwrap(value.anyValue as? [String: Any])
        let data = try JSONSerialization.data(withJSONObject: back, options: [.sortedKeys])
        let expected = try JSONSerialization.data(
            withJSONObject: any, options: [.sortedKeys])
        XCTAssertEqual(data, expected)
    }

    func testBoolIsNotConfusedWithInt() throws {
        let value = JSONValue(any: ["t": true, "one": 1])
        XCTAssertEqual(value, .object(["t": .bool(true), "one": .int(1)]))
    }

    func testWholeValuedFloatsCanonicalizeToInt() throws {
        let value = JSONValue(any: ["x": 2.0, "y": 2.5])
        XCTAssertEqual(value, .object(["x": .int(2), "y": .double(2.5)]))

        // Every spelling of a whole-valued number, parsed through JSONValue.parse
        // (JSONDecoder), lands on the same .int — not just the plain "2.0" case.
        for (literal, expectedInt) in [("2.0", 2), ("1e2", 100), ("100.000", 100), ("-0", 0)] {
            let parsed = try JSONValue.parse(Data(#"{"x": \#(literal)}"#.utf8))
            XCTAssertEqual(parsed, .object(["x": .int(expectedInt)]), "literal \(literal)")
        }

        // Parse round-trip stability: re-parsing a serialized whole-valued
        // float must not flip it back and forth.
        let parsed = try JSONValue.parse(Data(#"{"x": 2.0}"#.utf8))
        let reparsed = try JSONValue.parse(try parsed.serialized())
        XCTAssertEqual(reparsed, parsed)
    }

    func testTypeName() {
        XCTAssertEqual(JSONValue.null.typeName, "null")
        XCTAssertEqual(JSONValue.bool(true).typeName, "boolean")
        XCTAssertEqual(JSONValue.int(1).typeName, "number")
        XCTAssertEqual(JSONValue.double(1.5).typeName, "number")
        XCTAssertEqual(JSONValue.string("").typeName, "string")
        XCTAssertEqual(JSONValue.array([]).typeName, "array")
        XCTAssertEqual(JSONValue.object([:]).typeName, "object")
    }

    func testIntegersBeyondInt64BecomeDoubles() throws {
        let value = JSONValue(any: ["n": 99_999_999_999_999_999_999.0])
        XCTAssertEqual(value, .object(["n": .double(99_999_999_999_999_999_999.0)]))

        let parsed = try JSONValue.parse(Data(#"{"n": 99999999999999999999}"#.utf8))
        guard case .object(let o) = parsed, case .double? = o["n"] else {
            return XCTFail("expected a double, got \(parsed)")
        }
    }

    func testIntAndDoubleWithSameValueAreDifferentKinds() {
        XCTAssertNotEqual(JSONValue.int(2), JSONValue.double(2.0))
    }

    /// Unlike `JSONSerialization` (last wins) and Windows' `System.Text.Json`
    /// (also last wins by default), Foundation's `JSONDecoder` keeps the
    /// FIRST value for a duplicate key — verified empirically, not assumed.
    func testFirstDuplicateKeyWins() throws {
        let value = try JSONValue.parse(Data(#"{"a": 1, "a": 2}"#.utf8))
        XCTAssertEqual(value, .object(["a": .int(1)]))
    }

    func testInvalidDocumentsThrow() {
        XCTAssertThrowsError(try JSONValue.parse(Data()))
        XCTAssertThrowsError(try JSONValue.parse(Data("{".utf8)))
        XCTAssertThrowsError(try JSONValue.parse(Data(#"{"a": 1"#.utf8)))
    }

    /// A file saved by an editor that writes a BOM (common on Windows) must
    /// still parse — Foundation's JSONDecoder skips it.
    func testUtf8BomIsAccepted() throws {
        var data = Data([0xEF, 0xBB, 0xBF])
        data.append(Data(#"{"a": 1}"#.utf8))
        XCTAssertEqual(try JSONValue.parse(data), .object(["a": .int(1)]))
    }

    func testTopLevelScalarsParse() throws {
        XCTAssertEqual(try JSONValue.parse(Data("42".utf8)), .int(42))
        XCTAssertEqual(try JSONValue.parse(Data(#""hello""#.utf8)), .string("hello"))
        XCTAssertEqual(try JSONValue.parse(Data("true".utf8)), .bool(true))
    }
}
