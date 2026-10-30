using System.Text.Json;

namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/JSONValueTests.swift
/// (serialization-dependent cases live in AppleJsonWriterTests).</summary>
public class JsonValueTests
{
    [Fact]
    public void ParsesEveryKindWithStructuralEquality()
    {
        // From testParseAndSerializeRoundTrip (parse half).
        var value = JsonValue.Parse("{\"a\": 1, \"b\": \"two\", \"c\": [true, null, 2.5], \"d\": {\"e\": []}}");
        var expected = JsonValue.Object(
            ("a", JsonValue.Int(1)),
            ("b", JsonValue.String("two")),
            ("c", JsonValue.Array([JsonValue.Bool(true), JsonValue.Null, JsonValue.Double(2.5)])),
            ("d", JsonValue.Object(("e", JsonValue.Array([])))));
        Assert.Equal(expected, value);
        Assert.Equal(expected.GetHashCode(), value.GetHashCode());
    }

    [Fact]
    public void BoolIsNotConfusedWithInt()
    {
        // testBoolIsNotConfusedWithInt
        var value = JsonValue.Parse("{\"t\": true, \"one\": 1}");
        Assert.Equal(JsonValue.Object(("t", JsonValue.Bool(true)), ("one", JsonValue.Int(1))), value);
        Assert.NotEqual(JsonValue.Bool(true), JsonValue.Int(1));
    }

    [Fact]
    public void WholeValuedFloatsCanonicalizeToInt()
    {
        // testWholeValuedFloatsCanonicalizeToInt (parse half) + Apple JSONDecoder behavior
        // observed on macOS 26: 2.0, 1e2, 100.000 and -0 all decode as Int.
        var value = JsonValue.Parse("{\"x\": 2.0, \"y\": 2.5, \"z\": 1e2, \"w\": 100.000, \"n\": -0.0}");
        Assert.Equal(JsonValue.Int(2), value["x"]);
        Assert.Equal(JsonValue.Double(2.5), value["y"]);
        Assert.Equal(JsonValue.Int(100), value["z"]);
        Assert.Equal(JsonValue.Int(100), value["w"]);
        Assert.Equal(JsonValue.Int(0), value["n"]);
    }

    /// <summary>A number written with a fraction or an exponent that is a whole number reads as that integer
    /// exactly, past where a double is exact; the 64-bit extremes read as doubles, as the Mac's JSONDecoder gives
    /// them.</summary>
    [Theory]
    [InlineData("9007199254740993.0", 9007199254740993L, null)]
    [InlineData("123456789012345678.0", 123456789012345678L, null)]
    [InlineData("1234567890123456780e-1", 123456789012345678L, null)]
    [InlineData("-9223372036854775807.0", -9223372036854775807L, null)]
    [InlineData("-9223372036854775808.0", null, -9223372036854775808.0)]
    [InlineData("9223372036854775807.0", null, 9223372036854775808.0)]
    [InlineData("1e19", null, 1e19)]
    public void WholeNumbersWithAFractionOrExponentReadExactly(string text, long? integer, double? number)
    {
        var expected = integer is { } i ? JsonValue.Int(i) : JsonValue.Double(number!.Value);
        Assert.Equal(JsonValue.Array([expected]), JsonValue.Parse("[" + text + "]"));
    }

    [Fact]
    public void IntegersBeyondInt64BecomeDoubles()
    {
        var value = JsonValue.Parse("[9223372036854775807, 9223372036854775808, 12345678901234567890]");
        Assert.Equal(JsonValue.Int(long.MaxValue), value.ArrayItems[0]);
        Assert.Equal(JsonKind.Double, value.ArrayItems[1].Kind);
        Assert.Equal(JsonKind.Double, value.ArrayItems[2].Kind);
    }

    [Fact]
    public void IntAndDoubleWithSameValueAreDifferentKinds()
    {
        Assert.NotEqual(JsonValue.Int(2), JsonValue.Double(2.0));
    }

    [Fact]
    public void TypeName()
    {
        // testTypeName + the remaining cases
        Assert.Equal("object", JsonValue.Object().TypeName);
        Assert.Equal("array", JsonValue.Array([]).TypeName);
        Assert.Equal("string", JsonValue.String("").TypeName);
        Assert.Equal("number", JsonValue.Int(1).TypeName);
        Assert.Equal("number", JsonValue.Double(1.5).TypeName);
        Assert.Equal("boolean", JsonValue.Bool(false).TypeName);
        Assert.Equal("null", JsonValue.Null.TypeName);
    }

    [Fact]
    public void IndexerReturnsNullForMissingKeyOrNonObject()
    {
        var obj = JsonValue.Object(("k", JsonValue.Int(1)));
        Assert.Null(obj["missing"]);
        Assert.Null(JsonValue.Array([])["k"]);
        Assert.Equal(JsonValue.Int(1), obj["k"]);
    }

    [Fact]
    public void WithAndWithoutProduceNewObjects()
    {
        var obj = JsonValue.Object(("a", JsonValue.Int(1)));
        var with = obj.With("b", JsonValue.Int(2));
        Assert.Equal(JsonValue.Object(("a", JsonValue.Int(1)), ("b", JsonValue.Int(2))), with);
        Assert.Equal(JsonValue.Object(("a", JsonValue.Int(1))), obj);   // unchanged
        Assert.Equal(JsonValue.Object(("b", JsonValue.Int(2))), with.Without("a"));
    }

    [Fact]
    public void ObjectPropertiesAreOrdinallySorted()
    {
        var obj = JsonValue.Parse("{\"b\": 1, \"B\": 2, \"a\": 3, \"10\": 4, \"9\": 5}");
        Assert.Equal(["10", "9", "B", "a", "b"], obj.ObjectProperties.Keys.ToArray());
    }

    [Fact]
    public void AnEscapedLoneSurrogateThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => JsonValue.Parse("\"\\ud800\""));
        Assert.Throws<JsonException>(() => JsonValue.Parse("{\"\\udc00\":1}"));
    }

    [Fact]
    public void FirstDuplicateKeyWins()
    {
        Assert.Equal(JsonValue.Int(1), JsonValue.Parse("{\"k\": 1, \"k\": 2}")["k"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json!!")]
    [InlineData("{\"a\": 1} trailing")]
    [InlineData("[1,]")]
    [InlineData("1e400")]
    public void InvalidDocumentsThrowJsonException(string text)
    {
        Assert.ThrowsAny<JsonException>(() => JsonValue.Parse(text));
    }

    [Fact]
    public void Utf8BomIsAccepted()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'1', (byte)'}'];
        Assert.Equal(JsonValue.Object(("a", JsonValue.Int(1))), JsonValue.Parse(bytes));
        Assert.Equal(JsonValue.Object(), JsonValue.Parse(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' }));
    }

    [Fact]
    public void TopLevelScalarsParse()
    {
        Assert.Equal(JsonValue.String("x"), JsonValue.Parse("\"x\""));
        Assert.Equal(JsonValue.Null, JsonValue.Parse("null"));
    }

    [Fact]
    public void NonFiniteDoublesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => JsonValue.Double(double.NaN));
        Assert.Throws<ArgumentException>(() => JsonValue.Double(double.PositiveInfinity));
    }

    [Fact]
    public void AccessorsThrowOnKindMismatch()
    {
        Assert.Throws<InvalidOperationException>(() => JsonValue.Int(1).StringValue);
        Assert.Throws<InvalidOperationException>(() => JsonValue.String("s").ArrayItems);
    }

    /// <summary>Arrays and objects nest at most 64 deep, as the Mac mirror allows, so a document reads
    /// the same on both platforms; brackets inside a string do not count.</summary>
    /// <summary>A comma before a closing bracket or brace is refused, as the Mac mirror refuses it; a comma or
    /// bracket inside a string is only text.</summary>
    [Fact]
    public void ATrailingCommaIsRefused()
    {
        foreach (var text in new[] { "[1,]", """{"a":1,}""", "[1 , \n\t\r ]", "[[1,],2]", """{"a":{"b":[],},"c":1}""" })
        {
            Assert.ThrowsAny<JsonException>(() => JsonValue.Parse(text));
        }
        Assert.Equal(JsonValue.Array([JsonValue.String("a,]"), JsonValue.String(",}")]), JsonValue.Parse("""["a,]",",}"]"""));
        Assert.Equal(JsonValue.Object(("a", JsonValue.String("x\",}"))), JsonValue.Parse("""{"a":"x\",}"}"""));
        Assert.Equal(JsonValue.Array([JsonValue.Int(1), JsonValue.Int(2)]), JsonValue.Parse("[1, 2]"));
    }

    [Fact]
    public void NestingDeeperThan64IsRefused()
    {
        static string Nested(int depth) => new string('[', depth) + new string(']', depth);
        JsonValue.Parse(Nested(64));
        Assert.ThrowsAny<JsonException>(() => JsonValue.Parse(Nested(65)));
        Assert.ThrowsAny<JsonException>(() => JsonValue.Parse(Nested(70)));
        JsonValue.Parse("[\"" + new string('[', 70) + "\"]");
    }

    /// <summary>Text is compared ordinally, as the Mac compares it scalar by scalar: é as one character and
    /// as e and a combining accent are different values, in a string and in a key alike, and a set
    /// holds both.</summary>
    [Fact]
    public void TextComposedAnotherWayIsAnotherValue()
    {
        const string composed = "caf\u00E9", decomposed = "cafe\u0301";
        Assert.NotEqual(JsonValue.String(composed), JsonValue.String(decomposed));
        Assert.NotEqual(JsonValue.Array([JsonValue.String(composed)]), JsonValue.Array([JsonValue.String(decomposed)]));
        Assert.NotEqual(JsonValue.Object((composed, JsonValue.Int(1))), JsonValue.Object((decomposed, JsonValue.Int(1))));
        Assert.Equal(2, new HashSet<JsonValue> { JsonValue.String(composed), JsonValue.String(decomposed) }.Count);
        Assert.Equal(JsonValue.Object((composed, JsonValue.Array([JsonValue.String(decomposed)]))),
                     JsonValue.Object((composed, JsonValue.Array([JsonValue.String(decomposed)]))));
        Assert.NotEqual(JsonValue.Int(1), JsonValue.Double(1));
    }
}
