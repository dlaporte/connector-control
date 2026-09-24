namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/JSONPointerTests.swift</summary>
public class JsonPointerTests
{
    private static readonly JsonValue Config = JsonValue.Object(
        ("command", JsonValue.String("node")),
        ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("server.js")])),
        ("env", JsonValue.Object(("A", JsonValue.String("1")), ("B/C", JsonValue.String("2")))));

    [Fact]
    public void ParsesAndPrintsWithEscapes()
    {
        var p = Assert.IsType<JsonPointer>(JsonPointer.Parse("/env/B~1C"));
        Assert.Equal(["env", "B/C"], p.Segments);
        Assert.Equal("/env/B~1C", p.ToString());
        Assert.Null(JsonPointer.Parse("args/1"));
        Assert.Empty(JsonPointer.Parse("")!.Segments);
    }

    [Fact]
    public void LooksUpKeysAndIndexes()
    {
        Assert.Equal(JsonValue.String("server.js"), Config.ValueAt(JsonPointer.Parse("/args/1")!));
        Assert.Equal(JsonValue.String("2"), Config.ValueAt(JsonPointer.Parse("/env/B~1C")!));
        Assert.Null(Config.ValueAt(JsonPointer.Parse("/args/7")!));
        Assert.Null(Config.ValueAt(JsonPointer.Parse("/command/x")!));
        // An index is digits and nothing else.
        foreach (var segment in new[] { " 1", "1 ", "+1", "-0", "" })
        {
            Assert.Null(Config.ValueAt(new JsonPointer(["args", segment])));
            Assert.Null(Config.Replacing(new JsonPointer(["args", segment]), JsonValue.String("x")));
        }
    }

    [Fact]
    public void ReplacesALeafAndLeavesTheRestAlone()
    {
        var replaced = Config.Replacing(JsonPointer.Parse("/args/1")!, JsonValue.String("x.js"))!;
        Assert.Equal(JsonValue.String("x.js"), replaced.ValueAt(new JsonPointer(["args", "1"])));
        Assert.Equal(JsonValue.String("node"), replaced.ValueAt(new JsonPointer(["command"])));
        Assert.Null(Config.Replacing(new JsonPointer(["args", "9"]), JsonValue.String("x")));
    }

    [Fact]
    public void StringLeavesAreDepthFirstWithSortedKeys()
    {
        Assert.Equal(["/args/0", "/args/1", "/command", "/env/A", "/env/B~1C"],
            Config.StringLeaves().Select(l => l.Pointer.ToString()).ToArray());
    }
}
