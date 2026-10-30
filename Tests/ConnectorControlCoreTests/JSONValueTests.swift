import XCTest
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/JsonValueTests.cs
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
        // (JSONDecoder), lands on the same .int - not just the plain "2.0" case.
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

    /// A number written with a fraction or an exponent that is a whole number reads as that integer
    /// exactly, past where a double is exact; the 64-bit extremes read as doubles, as `JSONDecoder` gives
    /// them. The Windows mirror reads the token itself to give the same.
    func testWholeNumbersWithAFractionOrExponentReadExactly() throws {
        let cases: [(String, JSONValue)] = [
            ("9007199254740993.0", .int(9_007_199_254_740_993)),
            ("123456789012345678.0", .int(123_456_789_012_345_678)),
            ("1234567890123456780e-1", .int(123_456_789_012_345_678)),
            ("-9223372036854775807.0", .int(-9_223_372_036_854_775_807)),
            ("-9223372036854775808.0", .double(-9_223_372_036_854_775_808.0)),
            ("9223372036854775807.0", .double(9_223_372_036_854_775_808.0)),
            ("1e19", .double(1e19)),
        ]
        for (text, expected) in cases {
            XCTAssertEqual(try JSONValue.parse(Data("[\(text)]".utf8)), .array([expected]), text)
        }
    }

    func testIntAndDoubleWithSameValueAreDifferentKinds() {
        XCTAssertNotEqual(JSONValue.int(2), JSONValue.double(2.0))
    }

    /// Unlike `JSONSerialization` (last wins) and Windows' `System.Text.Json`
    /// (also last wins by default), Foundation's `JSONDecoder` keeps the
    /// FIRST value for a duplicate key - verified empirically, not assumed.
    func testFirstDuplicateKeyWins() throws {
        let value = try JSONValue.parse(Data(#"{"a": 1, "a": 2}"#.utf8))
        XCTAssertEqual(value, .object(["a": .int(1)]))
    }

    func testInvalidDocumentsThrow() {
        XCTAssertThrowsError(try JSONValue.parse(Data()))
        XCTAssertThrowsError(try JSONValue.parse(Data("{".utf8)))
        XCTAssertThrowsError(try JSONValue.parse(Data(#"{"a": 1"#.utf8)))
    }

    func testAnEscapedLoneSurrogateThrows() {
        XCTAssertThrowsError(try JSONValue.parse(Data(#""\ud800""#.utf8)))
        XCTAssertThrowsError(try JSONValue.parse(Data(#"{"\udc00":1}"#.utf8)))
    }

    /// A file saved by an editor that writes a BOM (common on Windows) must
    /// still parse - Foundation's JSONDecoder skips it.
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

    /// A comma before a closing bracket or brace is refused, as the Windows reader refuses it, where
    /// `JSONDecoder` reads one; a comma or bracket inside a string is only text.
    func testATrailingCommaIsRefused() {
        for text in ["[1,]", #"{"a":1,}"#, "[1 , \n\t\r ]", #"[[1,],2]"#, #"{"a":{"b":[],},"c":1}"#] {
            XCTAssertThrowsError(try JSONValue.parse(Data(text.utf8)), text)
        }
        XCTAssertEqual(try JSONValue.parse(Data(#"["a,]",",}"]"#.utf8)), .array([.string("a,]"), .string(",}")]))
        XCTAssertEqual(try JSONValue.parse(Data(#"{"a":"x\",}"}"#.utf8)), .object(["a": .string(#"x",}"#)]))
        XCTAssertEqual(try JSONValue.parse(Data("[1, 2]".utf8)), .array([.int(1), .int(2)]))
    }

    /// Arrays and objects nest at most 64 deep, as the Windows reader allows, so a document reads
    /// the same on both platforms; brackets inside a string do not count.
    func testNestingDeeperThan64IsRefused() {
        func nested(_ depth: Int) -> Data {
            Data((String(repeating: "[", count: depth) + String(repeating: "]", count: depth)).utf8)
        }
        XCTAssertNoThrow(try JSONValue.parse(nested(64)))
        XCTAssertThrowsError(try JSONValue.parse(nested(65)))
        XCTAssertThrowsError(try JSONValue.parse(nested(70)))
        XCTAssertNoThrow(try JSONValue.parse(Data(("[\"" + String(repeating: "[", count: 70) + "\"]").utf8)))
    }

    /// Text is compared scalar by scalar, as Windows compares it ordinally: é as one character and
    /// as e and a combining accent are different values, in a string and in a key alike, and a set
    /// holds both.
    func testTextComposedAnotherWayIsAnotherValue() {
        let composed = "caf\u{E9}", decomposed = "cafe\u{301}"
        XCTAssertNotEqual(JSONValue.string(composed), .string(decomposed))
        XCTAssertNotEqual(JSONValue.array([.string(composed)]), .array([.string(decomposed)]))
        XCTAssertNotEqual(JSONValue.object([composed: .int(1)]), .object([decomposed: .int(1)]))
        XCTAssertFalse(JSONValue.identical(["a": .string(composed)], ["a": .string(decomposed)]))
        XCTAssertEqual(Set([JSONValue.string(composed), .string(decomposed)]).count, 2)
        XCTAssertEqual(JSONValue.object([composed: .array([.string(decomposed)])]),
                       .object([composed: .array([.string(decomposed)])]))
        XCTAssertNotEqual(JSONValue.int(1), .double(1))
    }
}
