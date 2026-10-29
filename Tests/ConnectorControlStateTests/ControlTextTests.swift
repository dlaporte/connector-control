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
    /// An app sentence that quotes a name someone else chose shows that name as `escapedName` does,
    /// its own quotes and the rest of it as `escaped` does.
    func testANameInsideAnAppSentenceShowsAsItsEscape() {
        XCTAssertEqual(ControlText.escaped("No collection named “a\nb”.\n", naming: ["a\nb"]), #"No collection named “a\nb”.\n"#)
        XCTAssertEqual(ControlText.escaped("A “x” and “y\u{201D}”.", naming: ["x", "y\u{201D}"]), #"A “x” and “y\u{201D}”."#)
        XCTAssertEqual(ControlText.escaped("Plain “a”.", naming: ["", "zz"]), "Plain “a”.")
    }

    /// A name that contains another is escaped whole: replaced first, before the shorter name can
    /// change part of it, so its own closing quote cannot read as the end of a name.
    func testANameThatContainsAnotherIsEscapedWhole() {
        XCTAssertEqual(ControlText.escaped("“a\n”b” and “a\n”", naming: ["a\n", "a\n”b"]),
                       #"“a\n\u{201D}b” and “a\n”"#)
    }

    func testControlAndFormatCharactersShowAsEscapes() {
        XCTAssertEqual(ControlText.escaped("a\nb\rc\td\u{0}e\u{7F}f\u{85}g\u{200B}h\u{202E}i\u{2066}j\u{FEFF}k\u{2028}l\u{61C}m"),
                       #"a\nb\rc\td\u{0000}e\u{007F}f\u{0085}g\u{200B}h\u{202E}i\u{2066}j\u{FEFF}k\u{2028}l\u{061C}m"#)
        XCTAssertEqual(ControlText.escaped(#"café C:\x 😀"#), #"café C:\x 😀"#)
        XCTAssertEqual(ControlText.labelled([("k\ney", "v\nx"), ("b", "w")]), [#"k\ney   v\nx"#, "b       w"])
        XCTAssertEqual(ControlText.table(header: nil, rows: [["a\n", "b\u{202E}"]]), [#"a\n   b\u{202E}"#])
    }

    /// The invisible characters: a soft hyphen, the word joiner, the invisible operators and the
    /// unassigned U+2065 beside them, each of which hides where one word ends.
    func testInvisibleCharactersShowAsEscapes() {
        XCTAssertEqual(ControlText.escaped("a\u{AD}b\u{2060}c\u{2061}d\u{2064}e\u{2065}f"),
                       #"a\u{00AD}b\u{2060}c\u{2061}d\u{2064}e\u{2065}f"#)
    }

    /// A name shows as `escaped` shows it, and its own curly quotes show as escapes too, so a name
    /// cannot close the quotes the app's sentences put around it and read as two names.
    func testANameCannotCloseItsOwnQuotes() {
        XCTAssertEqual(ControlText.escapedName("x” and “y\n\u{202E}"), #"x\u{201D} and \u{201C}y\n\u{202E}"#)
        XCTAssertEqual(ControlText.escaped("“x”"), "“x”", "a value is not a name: its quotes stay as they are")
        XCTAssertEqual(ControlText.quotedList(["x” and “y", "z\n"]), #"“x\u{201D} and \u{201C}y” and “z\n”"#)
    }

    /// An argument list reads back as the arguments it holds: one that is empty, holds a space or
    /// a quote, or a character `escaped` escapes, is quoted, with \ and ' escaped inside.
    func testAnArgumentIsQuotedWhenItWouldNotReadAsOneWord() {
        XCTAssertEqual(ControlText.words(["--name", "x y"]), "--name 'x y'")
        XCTAssertEqual(ControlText.words(["--name", "x", "y"]), "--name x y")
        XCTAssertEqual(ControlText.words(["a", "", "b"]), "a '' b")
        XCTAssertEqual(ControlText.words(["it's", #"C:\x"#, #"{"a":1}"#, #"C:\Program Files\x"#, "x\ny"]),
                       #"'it\'s' C:\x {"a":1} 'C:\\Program Files\\x' 'x\ny'"#)
        XCTAssertEqual(ControlText.word("a\u{A0}b"), #"'a\u{00A0}b'"#, "a no-break space is a space, and shows as its code")
    }

    func testNamesAreListedTheWayTheAppsSentencesListThem() {
        XCTAssertEqual(ControlText.quotedList(["a"]), "“a”")
        XCTAssertEqual(ControlText.quotedList(["a", "b"]), "“a” and “b”")
        XCTAssertEqual(ControlText.quotedList(["a", "b", "c"]), "“a”, “b” and “c”")
    }

    // MARK: What a dialog can trust

    /// Every character that shows as nothing, or as a blank, shows as its code: the rest of
    /// Default_Ignorable_Code_Point, the blank letters, the interlinear annotation marks and the tags
    /// beyond the Basic Plane, as the controls and direction marks already did.
    func testEveryCharacterThatShowsAsNothingIsEscaped() {
        XCTAssertEqual(ControlText.escaped("a\u{034F}b\u{115F}c\u{3164}d\u{2800}e\u{FFA0}f\u{FFF9}g"),
                       #"a\u{034F}b\u{115F}c\u{3164}d\u{2800}e\u{FFA0}f\u{FFF9}g"#)
        XCTAssertEqual(ControlText.escaped("\u{1160}\u{17B4}\u{17B5}\u{180B}\u{180E}\u{180F}\u{206A}\u{206F}\u{FE00}\u{FFF0}\u{FFFB}"),
                       #"\u{1160}\u{17B4}\u{17B5}\u{180B}\u{180E}\u{180F}\u{206A}\u{206F}\u{FE00}\u{FFF0}\u{FFFB}"#)
        XCTAssertEqual(ControlText.escaped("tag\u{E0041}\u{E007F} \u{1BCA0}\u{1D173}\u{E0000}\u{E0100}\u{E0FFF}"),
                       #"tag\u{E0041}\u{E007F} \u{1BCA0}\u{1D173}\u{E0000}\u{E0100}\u{E0FFF}"#,
                       "beyond the Basic Plane an escape has five hex digits")
        XCTAssertEqual(ControlText.escaped("❤\u{FE0F}"), #"❤\u{FE0F}"#, "a variation selector is invisible, so it shows too")
        XCTAssertEqual(ControlText.escaped("é—👍 ok"), "é—👍 ok", "what shows as itself is left alone")
        XCTAssertEqual(ControlText.word("a\u{3164}b"), #"'a\u{3164}b'"#, "a word holding a blank letter is quoted")
    }

    /// A name shows one way only: a backslash in it is doubled, so a name holding the text
    /// `\u{202E}` reads apart from one holding the character.
    func testANameShowsOneWayOnly() {
        XCTAssertEqual(ControlText.escapedName(#"a\u{202E}"#), #"a\\u{202E}"#)
        XCTAssertEqual(ControlText.escapedName("a\u{202E}"), #"a\u{202E}"#)
        XCTAssertEqual(ControlText.escapedName(#"C:\tools"#), #"C:\\tools"#)
        XCTAssertEqual(ControlText.quotedList([#"a\n"#, "a\n"]), #"“a\\n” and “a\n”"#)
        XCTAssertEqual(ControlText.escaped("Added “x\\y”.", naming: ["x\\y"]), #"Added “x\\y”."#)
        // One pass from the left: a shorter name holding \ cannot re-escape a longer name's escape.
        XCTAssertEqual(ControlText.escaped("No “x\u{202E}” in “\\”.", naming: ["x\u{202E}", "\\"]), #"No “x\u{202E}” in “\\”."#)
        XCTAssertEqual(ControlText.escaped(#"No “x\u{202E}”."#, naming: [#"x\u{202E}"#]), #"No “x\\u{202E}”."#)
    }

    /// A path keeps its own backslashes, and its curly quotes are escaped, so it can sit between the
    /// app's quotes in a dialog's footer and be set apart there.
    @MainActor
    func testAPathKeepsItsBackslashesAndCannotCloseItsQuotes() {
        XCTAssertEqual(ControlText.escapedPath(#"C:\Users\me\team “x”.json"#), #"C:\Users\me\team \u{201C}x\u{201D}.json"#)
        XCTAssertEqual(ControlText.escapedPath("/tmp/a\nb.json"), #"/tmp/a\nb.json"#)
        XCTAssertEqual(ControlTools.fromFooter(ControlText.escapedPath("/Users/me/team.json")), "From “/Users/me/team.json”")
    }

    /// A name is found only between the app's own quotes, so two different names can never show alike:
    /// a name that begins with “ cannot match at another name's opening quote and swallow its start.
    func testANameIsFoundOnlyBetweenTheAppsQuotes() {
        let one = ControlText.escaped("Moved “aa\u{202E}” to “\u{201C}a”.", naming: ["aa\u{202E}", "\u{201C}a"])
        let other = ControlText.escaped("Moved “aa\\u{202E}” to “\u{201C}a”.", naming: ["aa\\u{202E}", "\u{201C}a"])
        XCTAssertEqual(one, #"Moved “aa\u{202E}” to “\u{201C}a”."#)
        XCTAssertEqual(other, #"Moved “aa\\u{202E}” to “\u{201C}a”."#)
        XCTAssertNotEqual(one, other)
        let character = ControlText.escaped("No “x\u{202E}” in “\\”.", naming: ["x\u{202E}", "\\"])
        let literal = ControlText.escaped(#"No “x\u{202E}” in “\”."#, naming: [#"x\u{202E}"#, "\\"])
        XCTAssertEqual(character, #"No “x\u{202E}” in “\\”."#)
        XCTAssertEqual(literal, #"No “x\\u{202E}” in “\\”."#)
        XCTAssertNotEqual(character, literal)
        XCTAssertEqual(ControlText.escaped("a \\ b “c”", naming: ["c"]), "a \\ b “c”", "outside a name's quotes nothing is doubled")
    }

    /// Over thousands of pairs of names, drawn by a fixed generator, two different pairs never read
    /// alike. The Windows mirror runs the same generator and expects the same count.
    func testDifferentNamesNeverShowAlike() {
        let tokens = ["\u{201C}", "\u{201D}", "\\", "\u{202E}", "a", "\\u{202E}", "\\\\"]
        var state: UInt64 = 20261028
        func next() -> Int {
            state = (state &* 1103515245 &+ 12345) % 2147483648
            return Int(state >> 16)
        }
        func name() -> String { (0..<(1 + next() % 3)).map { _ in tokens[next() % tokens.count] }.joined() }
        var seen: [String: String] = [:]
        var inputs = Set<String>()
        for _ in 0..<4000 {
            let a = name(), b = name()
            let input = a + "\u{0}" + b
            guard inputs.insert(input).inserted else { continue }
            let shown = ControlText.escaped("Moved “\(a)” to “\(b)”.", naming: [a, b])
            if let earlier = seen[shown] { XCTFail("\(earlier.debugDescription) and \(input.debugDescription) both show \(shown)") }
            seen[shown] = input
        }
        XCTAssertEqual(inputs.count, 3021, "the generator's own count, the same on both platforms")
    }

    /// A character that shows as a blank is escaped too: the no-break and other spaces beside the
    /// ordinary one.
    func testEveryBlankSpaceIsEscaped() {
        XCTAssertEqual(ControlText.escaped("a\u{00A0}b\u{1680}c\u{2000}d\u{200A}e\u{202F}f\u{205F}g\u{3000}h"),
                       #"a\u{00A0}b\u{1680}c\u{2000}d\u{200A}e\u{202F}f\u{205F}g\u{3000}h"#)
        XCTAssertEqual(ControlText.escaped("a b"), "a b", "the ordinary space shows as itself")
        XCTAssertEqual(ControlText.word("a\u{00A0}b"), #"'a\u{00A0}b'"#)
    }
}
