using System.Text.Json;

namespace ConnectorControl.Core.Tests;

/// <summary>Port of JSONValueTests.swift (serialization-dependent cases live in AppleJsonWriterTests).</summary>
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
    public void LastDuplicateKeyWins()
    {
        Assert.Equal(JsonValue.Int(2), JsonValue.Parse("{\"k\": 1, \"k\": 2}")["k"]);
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
}
