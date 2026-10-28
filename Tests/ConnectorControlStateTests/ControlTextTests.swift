import XCTest
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlTextTests.cs
final class ControlTextTests: XCTestCase {
    func testLabelledLinesLineUpTheirValues() {
        XCTAssertEqual(ControlText.labelled([("Active collection", "Harvard"), ("Claude integration", "on"), ("", "more")]), [
            "Active collection    Harvard",
            "Claude integration   on",
            "                     more",
        ])
    }

    func testATablePadsEveryColumnButTheLast() {
        XCTAssertEqual(ControlText.table(header: ["", "NAME", "RUNS"], rows: [["on", "aws-mcp", "aws-mcp.us-east-1.api.aws"],
                                                                              ["off", "café", "npx x"]]), [
            "      NAME      RUNS",
            "on    aws-mcp   aws-mcp.us-east-1.api.aws",
            "off   café      npx x",
        ])
    }

    /// A character outside the Basic Multilingual Plane is one scalar here and two UTF-16 units on
    /// Windows, where widths count runes: both count it once, so both pad alike.
    func testACharacterBeyondTheBasicPlaneIsOneColumnWide() {
        XCTAssertEqual(ControlText.width("a😀b"), 3)
        XCTAssertEqual(ControlText.labelled([("😀", "x"), ("ab", "y")]), ["😀    x", "ab   y"])
        XCTAssertEqual(ControlText.table(header: nil, rows: [["𝔸𝔹", "1"], ["abc", "2"]]), ["𝔸𝔹    1", "abc   2"])
    }

    /// A value can neither break its line nor hide part of it: control characters and the format
    /// characters that reorder or hide text show as escapes, on labels, values and cells alike.
    func testControlAndFormatCharactersShowAsEscapes() {
        XCTAssertEqual(ControlText.escaped("a\nb\rc\td\u{0}e\u{7F}f\u{85}g\u{200B}h\u{202E}i\u{2066}j\u{FEFF}k\u{2028}l\u{61C}m"),
                       #"a\nb\rc\td\u{0000}e\u{007F}f\u{0085}g\u{200B}h\u{202E}i\u{2066}j\u{FEFF}k\u{2028}l\u{061C}m"#)
        XCTAssertEqual(ControlText.escaped(#"café C:\x 😀"#), #"café C:\x 😀"#)
        XCTAssertEqual(ControlText.labelled([("k\ney", "v\nx"), ("b", "w")]), [#"k\ney   v\nx"#, "b       w"])
        XCTAssertEqual(ControlText.table(header: nil, rows: [["a\n", "b\u{202E}"]]), [#"a\n   b\u{202E}"#])
    }

    /// An argument list reads back as the arguments it holds: one that is empty, holds a space or
    /// a quote, or a character `escaped` escapes, is quoted, with \ and ' escaped inside.
    func testAnArgumentIsQuotedWhenItWouldNotReadAsOneWord() {
        XCTAssertEqual(ControlText.words(["--name", "x y"]), "--name 'x y'")
        XCTAssertEqual(ControlText.words(["--name", "x", "y"]), "--name x y")
        XCTAssertEqual(ControlText.words(["a", "", "b"]), "a '' b")
        XCTAssertEqual(ControlText.words(["it's", #"C:\x"#, #"{"a":1}"#, #"C:\Program Files\x"#, "x\ny"]),
                       #"'it\'s' C:\x {"a":1} 'C:\\Program Files\\x' 'x\ny'"#)
        XCTAssertEqual(ControlText.word("a\u{A0}b"), "'a\u{A0}b'", "a no-break space is a space")
    }

    func testNamesAreListedTheWayTheAppsSentencesListThem() {
        XCTAssertEqual(ControlText.quotedList(["a"]), "“a”")
        XCTAssertEqual(ControlText.quotedList(["a", "b"]), "“a” and “b”")
        XCTAssertEqual(ControlText.quotedList(["a", "b", "c"]), "“a”, “b” and “c”")
    }
}
