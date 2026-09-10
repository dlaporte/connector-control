using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.State;

public class ServerDeltaTests
{
    private static JsonValue Server(string command) => JsonValue.Object(("command", JsonValue.String(command)));

    private static Dictionary<string, JsonValue> Servers(params (string Name, string Command)[] entries) =>
        entries.ToDictionary(e => e.Name, e => Server(e.Command), StringComparer.Ordinal);

    [Fact]
    public void ClassifiesAddedRemovedAndChangedByName()
    {
        var before = Servers(("a", "npx"), ("b", "uvx"), ("c", "node"));
        var after = Servers(("b", "uvx"), ("c", "deno"), ("d", "npx"));
        var delta = ServerDelta.Between(before, after);
        Assert.Equal(["d"], delta.Added);
        Assert.Equal(["a"], delta.Removed);
        Assert.Equal(["c"], delta.Changed);
        Assert.False(delta.IsEmpty);
        Assert.Equal("adds d; removes a; changes c", delta.Summary());
    }

    [Fact]
    public void IdenticalSetsAreEmpty()
    {
        var servers = Servers(("a", "npx"));
        Assert.True(ServerDelta.Between(servers, servers).IsEmpty);
        Assert.Equal("", ServerDelta.Between(servers, servers).Summary());
    }

    [Fact]
    public void LongListsAreCappedAndSorted()
    {
        var after = Servers(("f", "npx"), ("e", "npx"), ("d", "npx"), ("c", "npx"), ("b", "npx"), ("a", "npx"));
        var delta = ServerDelta.Between(new Dictionary<string, JsonValue>(StringComparer.Ordinal), after);
        Assert.Equal("adds a, b, c, d and 2 more", delta.Summary());
        Assert.Equal("adds a, b, c, d, e, f", delta.Summary(limit: 6));
    }

    [Fact]
    public void BodyNamesTheChangeAndWhatToDoNext()
    {
        Assert.Equal(
            "The connector list changed outside Connector Control — Claude's config now adds evil; removes fs. Restart Claude to pick it up.",
            AppState.ConnectorListChangedBody(new ServerDelta(["evil"], ["fs"], []), restartRequired: true));
        Assert.Equal(
            "The connector list changed outside Connector Control — Claude's config was regenerated. Claude will use it the next time it starts.",
            AppState.ConnectorListChangedBody(ServerDelta.Empty, restartRequired: false));
    }
}
