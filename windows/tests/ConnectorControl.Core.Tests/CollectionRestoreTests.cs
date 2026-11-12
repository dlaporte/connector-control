namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/CollectionRestoreTests.swift</summary>
public class CollectionRestoreTests
{
    private static McpEntry Entry(string command, bool on = true, EditView view = EditView.Form) =>
        new(on, JsonValue.Object(("command", JsonValue.String(command))), view);

    private static Collection Of(params (string Name, McpEntry Entry)[] mcps) =>
        new(mcps.Select(m => new KeyValuePair<string, McpEntry>(m.Name, m.Entry)));

    private static Dictionary<string, CollectionRestore.Change> ByName(CollectionRestore changes) =>
        changes.ByName.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    [Fact]
    public void EachConnectorComesBackChangesGoesOrStays()
    {
        var current = Of(("same", Entry("s")), ("config", Entry("old")), ("switch", Entry("w", on: true)), ("added-since", Entry("a")));
        var restored = Of(("same", Entry("s")), ("config", Entry("new", on: false)), ("switch", Entry("w", on: false)), ("gone-since", Entry("g", on: true)));
        var changes = CollectionRestore.Changes(current, restored);
        Assert.Equal(new Dictionary<string, CollectionRestore.Change>(StringComparer.Ordinal)
        {
            ["config"] = new CollectionRestore.Change.Changes(false),
            ["switch"] = new CollectionRestore.Change.Changes(false),
            ["gone-since"] = new CollectionRestore.Change.ComesBack(true),
            ["added-since"] = new CollectionRestore.Change.Goes(),
        }, ByName(changes));
        Assert.Equal(1, changes.Unchanged);
        Assert.False(changes.IsEmpty);
    }

    /// <summary>A collection that is gone has nothing to compare with: everything comes back, on or off as it was.</summary>
    [Fact]
    public void EverythingComesBackIntoACollectionThatIsGone()
    {
        var changes = CollectionRestore.Changes(null, Of(("a", Entry("a", on: true)), ("b", Entry("b", on: false))));
        Assert.Equal(new Dictionary<string, CollectionRestore.Change>(StringComparer.Ordinal)
        {
            ["a"] = new CollectionRestore.Change.ComesBack(true),
            ["b"] = new CollectionRestore.Change.ComesBack(false),
        }, ByName(changes));
        Assert.Equal(0, changes.Unchanged);
    }

    /// <summary>Which view the editor last showed is not something a restore is asked to put back.</summary>
    [Fact]
    public void OnlyTheConfigAndTheSwitchMakeAConnectorDiffer()
    {
        var changes = CollectionRestore.Changes(Of(("a", Entry("a", view: EditView.Json))), Of(("a", Entry("a", view: EditView.Form))));
        Assert.True(changes.IsEmpty);
        Assert.Equal(1, changes.Unchanged);
    }

    /// <summary>Names compare as the store compares them: another spelling of a name is the same connector.</summary>
    [Fact]
    public void AnotherSpellingOfANameIsTheSameConnector()
    {
        var current = Of(("café", Entry("c")));
        var restored = Of(("café", Entry("c")));
        Assert.Equal(CollectionRestore.Changes(restored, restored), CollectionRestore.Changes(current, restored));
        Assert.True(CollectionRestore.Changes(current, restored).IsEmpty);
    }

    /// <summary>A config's own text is compared ordinally: a value composed another way is a change.</summary>
    [Fact]
    public void AConfigComposedAnotherWayIsAChange()
    {
        var changes = CollectionRestore.Changes(Of(("a", Entry("café"))), Of(("a", Entry("café"))));
        Assert.Equal(new CollectionRestore.Change.Changes(true), Assert.Single(changes.ByName).Value);
    }
}
