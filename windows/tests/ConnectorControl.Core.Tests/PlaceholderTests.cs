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

    /// <summary>
    /// Flattened in the order the leaves are walked, so a sentence built from these names reads
    /// the same wherever it is built and however often a name appears.
    /// </summary>
    [Fact]
    public void UnfilledNamesFlattenTheMarkersInFirstAppearanceOrder()
    {
        var config = JsonValue.Object(
            ("args", JsonValue.Array([JsonValue.String("${CC_NEEDS:zulu}"), JsonValue.String("${CC_NEEDS:zulu} ${CC_NEEDS:alpha}")])),
            ("env", JsonValue.Object(("A", JsonValue.String("${CC_NEEDS:alpha}")), ("B", JsonValue.String("plain")))));
        Assert.Equal(["zulu", "alpha"], Placeholder.UnfilledNamesIn(config));
        Assert.Empty(Placeholder.UnfilledNamesIn(JsonValue.Object(("a", JsonValue.String("none")))));
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

    [Fact]
    public void CollapsesTheDirectoryOnlyWhereItStandsAsAFolder()
    {
        const string share = @"C:\Users\d\share";
        var config = JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([
                JsonValue.String(share + "/tools/x.js"), JsonValue.String("--root=" + share),
                JsonValue.String(share + "-tools/y.js"), JsonValue.String(@"\\nas\" + share + @"\z.js"),
            ])),
            ("env", JsonValue.Object(("ROOT", JsonValue.String(share)))));
        var collapsed = Placeholder.CollapseDirectory(config, share);
        // A sibling that begins with the name, and a longer path that holds it, are left alone.
        Assert.Equal(
            ["${COLLECTION_DIR}/tools/x.js", "--root=${COLLECTION_DIR}", share + "-tools/y.js", @"\\nas\" + share + @"\z.js"],
            collapsed.ValueAt(new JsonPointer(["args"]))!.ArrayItems.Select(a => a.StringValue));
        Assert.Equal(JsonValue.String("${COLLECTION_DIR}"), collapsed.ValueAt(new JsonPointer(["env", "ROOT"])));
        // Collapsing undoes what expanding did.
        var one = JsonValue.Object(("args", JsonValue.Array([JsonValue.String(share + "/tools/x.js")])));
        Assert.Equal(one, Placeholder.ExpandDirectoryToken(Placeholder.CollapseDirectory(one, share), share));
        Assert.Equal(config, Placeholder.CollapseDirectory(config, ""));
    }
}
