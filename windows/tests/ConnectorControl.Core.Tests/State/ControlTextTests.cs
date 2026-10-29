using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ControlTextTests.swift</summary>
public class ControlTextTests
{
    [Fact]
    public void LabelledLinesLineUpTheirValues()
    {
        Assert.Equal(
            [
                "Active collection    Harvard",
                "Claude integration   on",
                "                     more",
            ],
            ControlText.Labelled([("Active collection", "Harvard"), ("Claude integration", "on"), ("", "more")]));
    }

    [Fact]
    public void ATablePadsEveryColumnButTheLast()
    {
        Assert.Equal(
            [
                "      NAME      RUNS",
                "on    aws-mcp   aws-mcp.us-east-1.api.aws",
                "off   café      npx x",
            ],
            ControlText.Table(["", "NAME", "RUNS"], [["on", "aws-mcp", "aws-mcp.us-east-1.api.aws"], ["off", "café", "npx x"]]));
    }

    /// <summary>A character outside the Basic Multilingual Plane is one scalar on the Mac and two UTF-16 units
    /// here, where widths count runes: both count it once, so both pad alike.</summary>
    [Fact]
    public void ACharacterBeyondTheBasicPlaneIsOneColumnWide()
    {
        Assert.Equal(3, ControlText.Width("a😀b"));
        Assert.Equal(["😀    x", "ab   y"], ControlText.Labelled([("😀", "x"), ("ab", "y")]));
        Assert.Equal(["𝔸𝔹    1", "abc   2"], ControlText.Table(null, [["𝔸𝔹", "1"], ["abc", "2"]]));
    }

    /// <summary>A value can neither break its line nor hide part of it: control characters and the format
    /// characters that reorder or hide text show as escapes, on labels, values and cells alike.</summary>
    /// <summary>An app sentence that quotes a name someone else chose shows that name as <c>EscapedName</c>
    /// does, its own quotes and the rest of it as <c>Escaped</c> does.</summary>
    [Fact]
    public void ANameInsideAnAppSentenceShowsAsItsEscape()
    {
        Assert.Equal("""No collection named “a\nb”.\n""", ControlText.Escaped("No collection named “a\nb”.\n", ["a\nb"]));
        Assert.Equal("""A “x” and “y\u{201D}”.""", ControlText.Escaped("A “x” and “y\u201D”.", ["x", "y\u201D"]));
        Assert.Equal("Plain “a”.", ControlText.Escaped("Plain “a”.", ["", "zz"]));
    }

    /// <summary>A name that contains another is escaped whole: replaced first, before the shorter name can
    /// change part of it, so its own closing quote cannot read as the end of a name.</summary>
    [Fact]
    public void ANameThatContainsAnotherIsEscapedWhole()
    {
        Assert.Equal(@"“a\n\u{201D}b” and “a\n”", ControlText.Escaped("“a\n”b” and “a\n”", ["a\n", "a\n”b"]));
    }

    [Fact]
    public void ControlAndFormatCharactersShowAsEscapes()
    {
        Assert.Equal("""a\nb\rc\td\u{0000}e\u{007F}f\u{0085}g\u{200B}h\u{202E}i\u{2066}j\u{FEFF}k\u{2028}l\u{061C}m""",
            ControlText.Escaped("a\nb\rc\td\u0000e\u007Ff\u0085g\u200Bh\u202Ei\u2066j\uFEFFk\u2028l\u061Cm"));
        Assert.Equal("""café C:\x 😀""", ControlText.Escaped("""café C:\x 😀"""));
        Assert.Equal(["""k\ney   v\nx""", "b       w"], ControlText.Labelled([("k\ney", "v\nx"), ("b", "w")]));
        Assert.Equal(["""a\n   b\u{202E}"""], ControlText.Table(null, [["a\n", "b\u202E"]]));
    }

    /// <summary>The invisible characters: a soft hyphen, the word joiner, the invisible operators and the
    /// unassigned U+2065 beside them, each of which hides where one word ends.</summary>
    [Fact]
    public void InvisibleCharactersShowAsEscapes() =>
        Assert.Equal("""a\u{00AD}b\u{2060}c\u{2061}d\u{2064}e\u{2065}f""",
            ControlText.Escaped("a\u00ADb\u2060c\u2061d\u2064e\u2065f"));

    /// <summary>A name shows as <see cref="ControlText.Escaped(string)"/> shows it, and its own curly quotes show as
    /// escapes too, so a name cannot close the quotes the app's sentences put around it and read as two names.</summary>
    [Fact]
    public void ANameCannotCloseItsOwnQuotes()
    {
        Assert.Equal("""x\u{201D} and \u{201C}y\n\u{202E}""", ControlText.EscapedName("x” and “y\n\u202E"));
        // A value is not a name: its quotes stay as they are.
        Assert.Equal("“x”", ControlText.Escaped("“x”"));
        Assert.Equal("""“x\u{201D} and \u{201C}y” and “z\n”""", ControlText.QuotedList(["x” and “y", "z\n"]));
    }

    /// <summary>An argument list reads back as the arguments it holds: one that is empty, holds a space or
    /// a quote, or a character <see cref="ControlText.Escaped(string)"/> escapes, is quoted, with \ and ' escaped inside.</summary>
    [Fact]
    public void AnArgumentIsQuotedWhenItWouldNotReadAsOneWord()
    {
        Assert.Equal("--name 'x y'", ControlText.Words(["--name", "x y"]));
        Assert.Equal("--name x y", ControlText.Words(["--name", "x", "y"]));
        Assert.Equal("a '' b", ControlText.Words(["a", "", "b"]));
        Assert.Equal("""'it\'s' C:\x {"a":1} 'C:\\Program Files\\x' 'x\ny'""",
            ControlText.Words(["it's", """C:\x""", """{"a":1}""", """C:\Program Files\x""", "x\ny"]));
        Assert.Equal("""'a\u{00A0}b'""", ControlText.Word("a\u00A0b"));   // a no-break space is a space, and shows as its code
    }

    [Fact]
    public void NamesAreListedTheWayTheAppsSentencesListThem()
    {
        Assert.Equal("“a”", ControlText.QuotedList(["a"]));
        Assert.Equal("“a” and “b”", ControlText.QuotedList(["a", "b"]));
        Assert.Equal("“a”, “b” and “c”", ControlText.QuotedList(["a", "b", "c"]));
    }

    // MARK: What a dialog can trust

    /// <summary>Every character that shows as nothing, or as a blank, shows as its code: the rest of
    /// Default_Ignorable_Code_Point, the blank letters, the interlinear annotation marks and the tags
    /// beyond the Basic Plane, as the controls and direction marks already did.</summary>
    [Fact]
    public void EveryCharacterThatShowsAsNothingIsEscaped()
    {
        Assert.Equal("""a\u{034F}b\u{115F}c\u{3164}d\u{2800}e\u{FFA0}f\u{FFF9}g""",
            ControlText.Escaped("a\u034Fb\u115Fc\u3164d\u2800e\uFFA0f\uFFF9g"));
        Assert.Equal("""\u{1160}\u{17B4}\u{17B5}\u{180B}\u{180E}\u{180F}\u{206A}\u{206F}\u{FE00}\u{FFF0}\u{FFFB}""",
            ControlText.Escaped("\u1160\u17B4\u17B5\u180B\u180E\u180F\u206A\u206F\uFE00\uFFF0\uFFFB"));
        // Beyond the Basic Plane an escape has five hex digits.
        Assert.Equal("""tag\u{E0041}\u{E007F} \u{1BCA0}\u{1D173}\u{E0000}\u{E0100}\u{E0FFF}""",
            ControlText.Escaped("tag\U000E0041\U000E007F \U0001BCA0\U0001D173\U000E0000\U000E0100\U000E0FFF"));
        // A variation selector is invisible, so it shows too.
        Assert.Equal("""❤\u{FE0F}""", ControlText.Escaped("❤\uFE0F"));
        Assert.Equal("é—👍 ok", ControlText.Escaped("é—👍 ok"));
        Assert.Equal("""'a\u{3164}b'""", ControlText.Word("a\u3164b"));
        // Platform idiom: only a .NET string can hold a lone surrogate; it shows as its code.
        Assert.Equal("""a\u{D800}b""", ControlText.Escaped("a\uD800b"));
    }

    /// <summary>A combining mark is drawn over the character before it, and marks stacked deep on one character
    /// are drawn over the lines above and below it: so only the first two marks on a character are drawn,
    /// and a third or later one, or one with no character before it, shows as its code, wherever text is shown.</summary>
    [Fact]
    public void AtMostTwoCombiningMarksAreDrawnOnACharacter()
    {
        var stack = "_" + string.Concat(Enumerable.Repeat("\u0305\u033F", 9));
        var one = "_\u0305\u033F" + string.Concat(Enumerable.Repeat(@"\u{0305}\u{033F}", 8));
        var attack = string.Concat(Enumerable.Repeat(stack, 46));
        var shown = string.Concat(Enumerable.Repeat(one, 46));
        Assert.Equal(shown, ControlText.Escaped(attack));
        Assert.Equal(shown, ControlText.EscapedName(attack));
        Assert.Equal(shown, ControlText.EscapedValue(attack));
        Assert.Equal(shown, ControlText.EscapedPath(attack));
        Assert.Equal("'" + shown + "'", ControlText.Word(attack));
        Assert.Equal($"Added “{shown}” and {one}.", ControlText.Escaped($"Added “{attack}” and {stack}.", [attack]));
        Assert.Equal(["K   " + one], ControlText.Labelled([("K", stack)]));
        Assert.Equal([one], ControlText.Table(null, [[stack]]));
        // A mark with no character before it at all.
        Assert.Equal(@"\u{0301}a", ControlText.Escaped("\u0301a"));
        Assert.Equal(@"\u{0301}a", ControlText.EscapedName("\u0301a"));
        Assert.Equal(@"'\u{0301}a'", ControlText.Word("\u0301a"));
        Assert.Equal(@"\u{0301}\u{0301}a", ControlText.Escaped("\u0301\u0301a"));
        Assert.Equal("a\u0301\u0301", ControlText.Word("a\u0301\u0301")); // two marks on a character are one word
        Assert.Equal("'a\u0301\u0301" + @"\u{0301}'", ControlText.Word("a\u0301\u0301\u0301")); // a word with a deeper stack is quoted
        // Every range, at both its ends: the same list the Mac mirror holds.
        int[] ends = [0x0300, 0x036F, 0x0483, 0x0489, 0x0591, 0x05BD, 0x0610, 0x061A, 0x064B, 0x065F, 0x0670,
                      0x06D6, 0x06DC, 0x06DF, 0x06E4, 0x06E7, 0x06E8, 0x06EA, 0x06ED, 0x0E31, 0x0E34, 0x0E3A,
                      0x0E47, 0x0E4E, 0x1AB0, 0x1AFF, 0x1DC0, 0x1DFF, 0x20D0, 0x20FF, 0xFE20, 0xFE2F];
        foreach (var value in ends)
        {
            var mark = char.ConvertFromUtf32(value);
            Assert.Equal("a" + mark + mark + ControlText.HexEscape(value), ControlText.Escaped("a" + mark + mark + mark));
        }
        Assert.Equal("a\u0E30\u0E30\u0E30", ControlText.Escaped("a\u0E30\u0E30\u0E30")); // a letter between the ranges is no mark
    }

    /// <summary>Text with one or two marks on a character reads as it always has: accented letters composed either
    /// way, pointed Hebrew, and the scripts that put two marks on one letter as a matter of course — Thai
    /// vowel and tone, Vietnamese decomposed, Hebrew dagesh and vowel, Arabic shadda and vowel.</summary>
    [Fact]
    public void OneOrTwoMarksOnACharacterReadAsTheyAre()
    {
        var thai = "\u0E17\u0E35\u0E48";
        var vietnamese = "\u0065\u0302\u0300 \u006F\u031B\u0323";
        var hebrew = "\u05D1\u05BC\u05B8";
        var arabic = "\u0645\u0651\u064E";
        foreach (var text in new[] { "cafe\u0301", "caf\u00E9", "\u05E9\u05B8\u05DC\u05D5\u05B9\u05DD", "x\u20D7", thai, hebrew, arabic })
        {
            Assert.Equal(text, ControlText.Escaped(text));
            Assert.Equal(text, ControlText.EscapedName(text));
            Assert.Equal(text, ControlText.EscapedValue(text));
            Assert.Equal(text, ControlText.EscapedPath(text));
            Assert.Equal(text, ControlText.Word(text)); // one word, unquoted
        }
        Assert.Equal(vietnamese, ControlText.Escaped(vietnamese));
        Assert.Equal(vietnamese, ControlText.EscapedName(vietnamese));
    }

    /// <summary>A name shows one way only: a backslash in it is doubled, so a name holding the text
    /// <c>\u{202E}</c> reads apart from one holding the character.</summary>
    [Fact]
    public void ANameShowsOneWayOnly()
    {
        Assert.Equal("""a\\u{202E}""", ControlText.EscapedName("""a\u{202E}"""));
        Assert.Equal("""a\u{202E}""", ControlText.EscapedName("a\u202E"));
        Assert.Equal("""C:\\tools""", ControlText.EscapedName("""C:\tools"""));
        Assert.Equal("""“a\\n” and “a\n”""", ControlText.QuotedList(["""a\n""", "a\n"]));
        Assert.Equal("""Added “x\\y”.""", ControlText.Escaped("Added “x\\y”.", ["x\\y"]));
        // One pass from the left: a shorter name holding \ cannot re-escape a longer name's escape.
        Assert.Equal("""No “x\u{202E}” in “\\”.""", ControlText.Escaped("No “x\u202E” in “\\”.", ["x\u202E", "\\"]));
        Assert.Equal("""No “x\\u{202E}”.""", ControlText.Escaped("""No “x\u{202E}”.""", ["""x\u{202E}"""]));
    }

    /// <summary>A path keeps its own backslashes, and its curly quotes are escaped, so it can sit between the
    /// app's quotes in a dialog's footer and be set apart there.</summary>
    [Fact]
    public void APathKeepsItsBackslashesAndCannotCloseItsQuotes()
    {
        Assert.Equal("""C:\Users\me\team \u{201C}x\u{201D}.json""", ControlText.EscapedPath("""C:\Users\me\team “x”.json"""));
        Assert.Equal("""/tmp/a\nb.json""", ControlText.EscapedPath("/tmp/a\nb.json"));
        Assert.Equal("From “/Users/me/team.json”", ControlTools.FromFooter(ControlText.EscapedPath("/Users/me/team.json")));
    }

    /// <summary>A name is found only between the app's own quotes, so two different names can never show alike:
    /// a name that begins with “ cannot match at another name's opening quote and swallow its start.</summary>
    [Fact]
    public void ANameIsFoundOnlyBetweenTheAppsQuotes()
    {
        var one = ControlText.Escaped("Moved “aa\u202E” to “\u201Ca”.", ["aa\u202E", "\u201Ca"]);
        var other = ControlText.Escaped("""Moved “aa\u{202E}” to “""" + "\u201Ca”.", ["""aa\u{202E}""", "\u201Ca"]);
        Assert.Equal("""Moved “aa\u{202E}” to “\u{201C}a”.""", one);
        Assert.Equal("""Moved “aa\\u{202E}” to “\u{201C}a”.""", other);
        Assert.NotEqual(one, other);
        var character = ControlText.Escaped("No “x\u202E” in “\\”.", ["x\u202E", "\\"]);
        var literal = ControlText.Escaped("""No “x\u{202E}” in “\”.""", ["""x\u{202E}""", "\\"]);
        Assert.Equal("""No “x\u{202E}” in “\\”.""", character);
        Assert.Equal("""No “x\\u{202E}” in “\\”.""", literal);
        Assert.NotEqual(character, literal);
        // Outside a name's quotes nothing is doubled.
        Assert.Equal("a \\ b “c”", ControlText.Escaped("a \\ b “c”", ["c"]));
    }

    /// <summary>Over thousands of pairs of names, drawn by a fixed generator, two different pairs never read
    /// alike. The Mac mirror runs the same generator and expects the same count.</summary>
    [Fact]
    public void DifferentNamesNeverShowAlike()
    {
        string[] tokens = ["\u201C", "\u201D", "\\", "\u202E", "a", "\\u{202E}", "\\\\"];
        ulong state = 20261028;
        int Next()
        {
            state = (unchecked(state * 1103515245UL + 12345UL)) % 2147483648UL;
            return (int)(state >> 16);
        }
        string Name() => string.Concat(Enumerable.Range(0, 1 + Next() % 3).Select(_ => tokens[Next() % tokens.Length]));
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 4000; i++)
        {
            var a = Name();
            var b = Name();
            var input = a + "\0" + b;
            if (!inputs.Add(input))
            {
                continue;
            }
            var shown = ControlText.Escaped($"Moved “{a}” to “{b}”.", [a, b]);
            Assert.False(seen.TryGetValue(shown, out var earlier), $"{earlier} and {input} both show {shown}");
            seen[shown] = input;
        }
        // The generator's own count, the same on both platforms.
        Assert.Equal(3021, inputs.Count);
    }

    /// <summary>A space-like character is escaped wherever a name, a value or a path is shown, since there it could
    /// pass for the ordinary space, but not in free text, where a localized system message sets
    /// punctuation off with one.</summary>
    [Fact]
    public void BlankSpacesAreEscapedInNamesAndValuesOnly()
    {
        const string spaces = "a\u00A0b\u1680c\u2000d\u200Ae\u202Ff\u205Fg\u3000h";
        const string shown = """a\u{00A0}b\u{1680}c\u{2000}d\u{200A}e\u{202F}f\u{205F}g\u{3000}h""";
        Assert.Equal(shown, ControlText.EscapedName(spaces));
        Assert.Equal(shown, ControlText.EscapedPath(spaces));
        Assert.Equal("'" + shown + "'", ControlText.Word(spaces));
        Assert.Equal("a b", ControlText.Escaped("a b"));
        // Free text keeps its spaces.
        const string french = "Impossible\u202F: le fichier\u00A0est absent";
        Assert.Equal(french, ControlText.Escaped(french));
        // Only the name is escaped.
        Assert.Equal("Erreur\u00A0: “" + """a\u{00A0}b""" + "”", ControlText.Escaped("Erreur\u00A0: “a\u00A0b”", ["a\u00A0b"]));
    }

    /// <summary>A table's cells are text as it is: a path keeps its backslashes, which are its own. The tools that
    /// list names put each through <c>EscapedName</c> before it goes in.</summary>
    [Fact]
    public void ATableKeepsAPathsBackslashes()
    {
        Assert.Equal(["""C:\Program Files\nodejs\npx.cmd   a\u{202E}"""],
            ControlText.Table(null, [["""C:\Program Files\nodejs\npx.cmd""", "a\u202E"]]));
        Assert.Equal(["""a\\b"""], ControlText.Table(null, [[ControlText.EscapedName("""a\b""")]]));
    }

    /// <summary>A key or a value on a labelled line shows a space-like character as its code, as the dialog does,
    /// and keeps a path's backslashes.</summary>
    [Fact]
    public void ALabelledLineEscapesBlankSpacesAndKeepsBackslashes()
    {
        Assert.Equal(["""k\u{00A0}x   v\u{202F}w""", """path         C:\x\y"""],
            ControlText.Labelled([("k\u00A0x", "v\u202Fw"), ("path", """C:\x\y""")]));
    }
}
