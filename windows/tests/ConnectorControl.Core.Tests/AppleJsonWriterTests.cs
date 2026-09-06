using System.Text;

namespace ConnectorControl.Core.Tests;

public class AppleJsonWriterTests
{
    private static readonly JsonValue Sample = JsonValue.Object(
        ("z", JsonValue.Int(1)),
        ("a", JsonValue.Array([])),
        ("e", JsonValue.Object()),
        ("s", JsonValue.String("q\"b\\t\t")),
        ("d", JsonValue.Double(0.5)),
        ("b", JsonValue.Bool(true)),
        ("url", JsonValue.String("https://x.y/z")),
        ("uni", JsonValue.String("é☃😀")),
        ("ctrl", JsonValue.String("\u0001\u001f\u007f")),
        ("n", JsonValue.Null));

    private const string ExpectedEncoder =
        "{\n" +
        "  \"a\" : [\n" +
        "\n" +
        "  ],\n" +
        "  \"b\" : true,\n" +
        "  \"ctrl\" : \"\\u0001\\u001f\u007f\",\n" +
        "  \"d\" : 0.5,\n" +
        "  \"e\" : {\n" +
        "\n" +
        "  },\n" +
        "  \"n\" : null,\n" +
        "  \"s\" : \"q\\\"b\\\\t\\t\",\n" +
        "  \"uni\" : \"é☃😀\",\n" +
        "  \"url\" : \"https:\\/\\/x.y\\/z\",\n" +
        "  \"z\" : 1\n" +
        "}";

    [Fact]
    public void EncoderFormatMatchesAppleJsonEncoder()
    {
        Assert.Equal(ExpectedEncoder, AppleJsonWriter.Write(Sample, AppleJsonFormat.Encoder));
        Assert.Equal(Encoding.UTF8.GetBytes(ExpectedEncoder), Sample.Serialize());
    }

    [Fact]
    public void EditorTextDoesNotEscapeSlashes()
    {
        var text = JsonValue.Object(("u", JsonValue.String("https://x.y/z"))).EditorText();
        Assert.Equal("{\n  \"u\" : \"https://x.y/z\"\n}", text);
    }

    [Fact]
    public void NestedContainersIndentTwoSpacesPerLevel()
    {
        var value = JsonValue.Object(("nested", JsonValue.Object(("k", JsonValue.Array(
            [JsonValue.Int(1), JsonValue.Double(2.5), JsonValue.Object(("deep", JsonValue.Bool(true))), JsonValue.Array([]), JsonValue.Object()])))));
        const string expected =
            "{\n  \"nested\" : {\n    \"k\" : [\n      1,\n      2.5,\n      {\n        \"deep\" : true\n      },\n      [\n\n      ],\n      {\n\n      }\n    ]\n  }\n}";
        Assert.Equal(expected, AppleJsonWriter.Write(value, AppleJsonFormat.Encoder));
    }

    [Fact]
    public void EmptyRootObjectHasBlankLine()
    {
        Assert.Equal("{\n\n}", AppleJsonWriter.Write(JsonValue.Object(), AppleJsonFormat.Encoder));
    }

    [Fact]
    public void StringEscapesMatchApple()
    {
        var value = JsonValue.Object(
            ("nl", JsonValue.String("a\nb")), ("cr", JsonValue.String("a\rb")),
            ("bs", JsonValue.String("a\bb")), ("ff", JsonValue.String("a\fb")),
            ("lt", JsonValue.String("<&>'")), ("ls", JsonValue.String("x\u2028y\u2029")),
            ("del", JsonValue.String("\u007f")), ("nul", JsonValue.String("\u0000")),
            ("esc", JsonValue.String("\u001b")));
        const string expected =
            "{\n  \"bs\" : \"a\\bb\",\n  \"cr\" : \"a\\rb\",\n  \"del\" : \"\u007f\",\n  \"esc\" : \"\\u001b\",\n" +
            "  \"ff\" : \"a\\fb\",\n  \"ls\" : \"x\u2028y\u2029\",\n  \"lt\" : \"<&>'\",\n  \"nl\" : \"a\\nb\",\n  \"nul\" : \"\\u0000\"\n}";
        Assert.Equal(expected, AppleJsonWriter.Write(value, AppleJsonFormat.Encoder));
    }

    [Fact]
    public void SerializationFormatUsesCollatedKeysAndG17Doubles()
    {
        var value = JsonValue.Object(
            ("b", JsonValue.Int(1)), ("B", JsonValue.Int(2)), ("a", JsonValue.Int(3)),
            ("tiny", JsonValue.Double(1e-7)), ("f", JsonValue.Double(0.1)));
        const string expected =
            "{\n  \"a\" : 3,\n  \"b\" : 1,\n  \"B\" : 2,\n  \"f\" : 0.10000000000000001,\n  \"tiny\" : 9.9999999999999995e-08\n}";
        Assert.Equal(expected, AppleJsonWriter.Write(value, AppleJsonFormat.Serialization));
    }

    [Fact]
    public void CompactSerializationHasNoWhitespace()
    {
        var value = JsonValue.Object(("b", JsonValue.String("1")), ("a", JsonValue.String("2")), ("s", JsonValue.String("x/y")));
        Assert.Equal("{\"a\":\"2\",\"b\":\"1\",\"s\":\"x\\/y\"}", AppleJsonWriter.Write(value, AppleJsonFormat.SerializationCompact));
    }

    [Fact]
    public void TopLevelArrayAndScalars()
    {
        Assert.Equal("[\n  1,\n  \"x\"\n]", AppleJsonWriter.Write(JsonValue.Array([JsonValue.Int(1), JsonValue.String("x")]), AppleJsonFormat.Encoder));
        Assert.Equal("null", AppleJsonWriter.Write(JsonValue.Null, AppleJsonFormat.Encoder));
        Assert.Equal("\"s\"", AppleJsonWriter.Write(JsonValue.String("s"), AppleJsonFormat.Encoder));
    }

    // Ported from JSONValueTests.swift

    [Fact]
    public void ParseAndSerializeRoundTrip()
    {
        // testParseAndSerializeRoundTrip
        var value = JsonValue.Parse("{\"a\": 1, \"b\": \"two\", \"c\": [true, null, 2.5], \"d\": {\"e\": []}}");
        Assert.Equal(value, JsonValue.Parse(value.Serialize()));
    }

    [Fact]
    public void IntStaysIntThroughSerialization()
    {
        // testIntStaysIntThroughSerialization
        var text = Encoding.UTF8.GetString(JsonValue.Object(("n", JsonValue.Int(3))).Serialize());
        Assert.Contains("\"n\" : 3", text);
    }

    [Fact]
    public void WholeValuedFloatsAreStableThroughReparse()
    {
        // testWholeValuedFloatsCanonicalizeToInt (serialize half)
        var parsed = JsonValue.Parse("{\"x\": 2.0}");
        Assert.Equal(parsed, JsonValue.Parse(parsed.Serialize()));
        Assert.Contains("\"x\" : 2", Encoding.UTF8.GetString(parsed.Serialize()));
    }

    [Fact]
    public void WrittenBytesAreUtf8WithoutBom()
    {
        var bytes = JsonValue.String("é").Serialize();
        Assert.Equal(new byte[] { (byte)'"', 0xC3, 0xA9, (byte)'"' }, bytes);
    }
}
