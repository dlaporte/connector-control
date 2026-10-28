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
    [Fact]
    public void ControlAndFormatCharactersShowAsEscapes()
    {
        Assert.Equal("""a\nb\rc\td\u{0000}e\u{007F}f\u{0085}g\u{200B}h\u{202E}i\u{2066}j\u{FEFF}k\u{2028}l\u{061C}m""",
            ControlText.Escaped("a\nb\rc\td\u0000e\u007Ff\u0085g\u200Bh\u202Ei\u2066j\uFEFFk\u2028l\u061Cm"));
        Assert.Equal("""café C:\x 😀""", ControlText.Escaped("""café C:\x 😀"""));
        Assert.Equal(["""k\ney   v\nx""", "b       w"], ControlText.Labelled([("k\ney", "v\nx"), ("b", "w")]));
        Assert.Equal(["""a\n   b\u{202E}"""], ControlText.Table(null, [["a\n", "b\u202E"]]));
    }

    /// <summary>An argument list reads back as the arguments it holds: one that is empty, holds a space or
    /// a quote, or a character <see cref="ControlText.Escaped"/> escapes, is quoted, with \ and ' escaped inside.</summary>
    [Fact]
    public void AnArgumentIsQuotedWhenItWouldNotReadAsOneWord()
    {
        Assert.Equal("--name 'x y'", ControlText.Words(["--name", "x y"]));
        Assert.Equal("--name x y", ControlText.Words(["--name", "x", "y"]));
        Assert.Equal("a '' b", ControlText.Words(["a", "", "b"]));
        Assert.Equal("""'it\'s' C:\x {"a":1} 'C:\\Program Files\\x' 'x\ny'""",
            ControlText.Words(["it's", """C:\x""", """{"a":1}""", """C:\Program Files\x""", "x\ny"]));
        Assert.Equal("'a\u00A0b'", ControlText.Word("a\u00A0b"));   // a no-break space is a space
    }

    [Fact]
    public void NamesAreListedTheWayTheAppsSentencesListThem()
    {
        Assert.Equal("“a”", ControlText.QuotedList(["a"]));
        Assert.Equal("“a” and “b”", ControlText.QuotedList(["a", "b"]));
        Assert.Equal("“a”, “b” and “c”", ControlText.QuotedList(["a", "b", "c"]));
    }
}
