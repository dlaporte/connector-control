using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ServerDeltaTests.swift</summary>
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
        Assert.Equal("adds d; deletes a; changes c", delta.Summary());
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

    /// <summary>The Mac pins the same body, the notification identifiers and the recheck delay in
    /// AppStateTests.swift's testConnectorListChangedBodySummarizesTheDeltaAndInternalIdentifiersStayStable.</summary>
    [Fact]
    public void BodyNamesTheChangeAndWhatToDoNext()
    {
        // Internal identifiers, kept out of the shared string catalog on purpose.
        Assert.Equal("restartPending", Notifications.RestartCategory);
        Assert.Equal("restartClaude", Notifications.RestartAction);
        Assert.Equal(TimeSpan.FromSeconds(3), AppState.RestartRecheckDelay);
        Assert.Equal(
            "The connector list changed outside Connector Control — Claude's config now adds evil; deletes fs. Restart Claude to pick it up.",
            AppState.ConnectorListChangedBody(new ServerDelta(["evil"], ["fs"], []), restartRequired: true));
        Assert.Equal(
            "The connector list changed outside Connector Control — Claude's config was regenerated. Claude will use it the next time it starts.",
            AppState.ConnectorListChangedBody(new ServerDelta([], [], []), restartRequired: false));
    }
}
