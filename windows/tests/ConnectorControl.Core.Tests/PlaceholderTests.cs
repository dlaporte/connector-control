namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/PlaceholderTests.swift</summary>
public class PlaceholderTests
{
    [Fact]
    public void MarkerSyntax()
    {
        Assert.Equal("${CC_NEEDS:DBT_TOKEN}", Placeholder.Marker("DBT_TOKEN"));
        Assert.True(Placeholder.IsValidName("server_path2"));
        Assert.False(Placeholder.IsValidName("bad-name"));
        Assert.False(Placeholder.IsValidName(""));
    }

    [Fact]
    public void FindsNamesInOrderWithoutDuplicates()
    {
        Assert.Equal(["token", "x"], Placeholder.NamesIn("Bearer ${CC_NEEDS:token} ${CC_NEEDS:token} ${CC_NEEDS:x}"));
        Assert.Empty(Placeholder.NamesIn("${CC_NEEDS:bad-name} plain"));
        Assert.True(Placeholder.ContainsMarker("a ${CC_NEEDS:b} c"));
        Assert.False(Placeholder.ContainsMarker("${COLLECTION_DIR}/x"));
    }

    [Fact]
    public void MarkersInAConfigReportPointers()
    {
        var config = JsonValue.Object(
            ("args", JsonValue.Array([JsonValue.String("${CC_NEEDS:server_path}")])),
            ("env", JsonValue.Object(("AUTH_HEADER", JsonValue.String("Bearer ${CC_NEEDS:token}")), ("PLAIN", JsonValue.String("v")))));
        var found = Placeholder.MarkersIn(config);
        Assert.Equal(["/args/0", "/env/AUTH_HEADER"], found.Select(m => m.Pointer.ToString()).ToArray());
        Assert.Equal([["server_path"], ["token"]], found.Select(m => m.Names.ToArray()).ToArray());
    }

    [Fact]
    public void ExpandsTheDirectoryTokenInEveryStringLeaf()
    {
        var config = JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String("${COLLECTION_DIR}/../servers/x.js")])),
            ("env", JsonValue.Object(("ROOT", JsonValue.String("${COLLECTION_DIR}")))));
        Assert.True(Placeholder.UsesDirectoryToken(config));
        var expanded = Placeholder.ExpandDirectoryToken(config, @"C:\Users\d\Acme\mcp");
        Assert.Equal(JsonValue.String(@"C:\Users\d\Acme\mcp/../servers/x.js"), expanded.ValueAt(new JsonPointer(["args", "0"])));
        Assert.Equal(JsonValue.String(@"C:\Users\d\Acme\mcp"), expanded.ValueAt(new JsonPointer(["env", "ROOT"])));
        Assert.False(Placeholder.UsesDirectoryToken(expanded));
    }
}
