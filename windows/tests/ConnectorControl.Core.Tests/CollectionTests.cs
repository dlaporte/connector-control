namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/CollectionTests.swift</summary>
public class CollectionTests
{
    private static McpEntry Entry(string url) => new(JsonValue.Object(
        ("command", JsonValue.String("npx")),
        ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("mcp-remote"), JsonValue.String(url)]))));

    private static string[] Keys(Collection? c) => c is null ? [] : c.Mcps.Keys.Order(StringComparer.Ordinal).ToArray();

    private static MasterStore TwoCollections() => new(2, "Work",
    [
        new("Work", new Collection(new Dictionary<string, McpEntry> { ["a"] = Entry("https://a.example/mcp") })),
        new("Personal", new Collection(new Dictionary<string, McpEntry> { ["b"] = Entry("https://b.example/mcp") })),
    ]);

    // Decoding

    [Fact]
    public void V2RoundTripPreservesTwoCollections()
    {
        var store = TwoCollections();
        var decoded = MasterStore.FromJson(JsonValue.Parse(store.ToJson().Serialize()));
        Assert.Equal(store, decoded);
        Assert.Equal("Work", decoded.ActiveCollection);
        Assert.Equal(["a"], Keys(decoded.Collections["Work"]));
        Assert.Equal(["b"], Keys(decoded.Collections["Personal"]));
    }

    // mcps accessor scoping

    [Fact]
    public void McpsAccessorReadsAndWritesOnlyActiveCollection()
    {
        var store = TwoCollections();
        store.Mcps["c"] = Entry("https://c.example/mcp");
        Assert.Equal(["a", "c"], Keys(store.Collections["Work"]));
        Assert.Equal(["b"], Keys(store.Collections["Personal"]));
    }

    // Collection management

    [Fact]
    public void AddCollectionCopyingCurrent()
    {
        var store = new MasterStore(new Dictionary<string, McpEntry> { ["a"] = Entry("https://a.example/mcp") });
        Assert.Null(store.AddCollection("Copy", copyingCurrent: true));
        Assert.Equal("Copy", store.ActiveCollection);
        Assert.Equal(["a"], Keys(store.Collections["Copy"]));
        Assert.Equal(["a"], Keys(store.Collections["Default"]));
    }

    [Fact]
    public void AddCollectionEmptyStartsBlank()
    {
        var store = new MasterStore(new Dictionary<string, McpEntry> { ["a"] = Entry("https://a.example/mcp") });
        Assert.Null(store.AddCollection("Fresh", copyingCurrent: false));
        Assert.Empty(store.Collections["Fresh"].Mcps);
    }

    [Fact]
    public void AddCollectionWithoutActivatingLeavesTheActiveOne()
    {
        var store = new MasterStore(new Dictionary<string, McpEntry> { ["a"] = Entry("https://a.example/mcp") });
        Assert.Null(store.AddCollection("Fresh", copyingCurrent: false, activating: false));
        Assert.Empty(store.Collections["Fresh"].Mcps);
        Assert.Equal("Default", store.ActiveCollection);
    }

    [Fact]
    public void AddCollectionRejectsEmptyName()
    {
        Assert.Equal("Name must not be empty.", MasterStore.Empty().AddCollection("   ", false));
    }

    [Fact]
    public void AddCollectionRejectsDuplicateName()
    {
        Assert.Equal("A collection named “Default” already exists.", MasterStore.Empty().AddCollection("Default", false));
    }

    [Fact]
    public void RenameActiveCollection()
    {
        var store = MasterStore.Empty();
        Assert.Null(store.RenameCollection(store.ActiveCollection, "Main"));
        Assert.Equal("Main", store.ActiveCollection);
        Assert.Equal(["Main"], store.Collections.Keys.ToArray());
    }

    [Fact]
    public void RenameActiveCollectionRejectsCollision()
    {
        var store = new MasterStore(2, "Work", [new("Work", new Collection()), new("Personal", new Collection())]);
        Assert.Equal("A collection named “Personal” already exists.", store.RenameCollection(store.ActiveCollection, "Personal"));
        Assert.Equal("Work", store.ActiveCollection);
    }

    [Fact]
    public void RenameActiveCollectionRejectsEmptyName()
    {
        var store = MasterStore.Empty();
        Assert.NotNull(store.RenameCollection(store.ActiveCollection, "  "));
    }

    [Fact]
    public void DeleteActiveCollectionSwitchesToFirstRemaining()
    {
        var store = new MasterStore(2, "Work", [new("Work", new Collection()), new("Alpha", new Collection()), new("Zeta", new Collection())]);
        Assert.Equal("Alpha", store.ActiveAfterDeleting("Work"));   // the confirmation names what the delete picks
        Assert.Equal("Work", store.ActiveAfterDeleting("Alpha"));   // the name being deleted is never its own successor
        Assert.Null(store.DeleteCollection(store.ActiveCollection));
        Assert.Equal("Alpha", store.ActiveCollection);
        Assert.False(store.Collections.ContainsKey("Work"));
    }

    [Fact]
    public void DeleteActiveCollectionRejectsLastCollection()
    {
        var store = MasterStore.Empty();
        Assert.Null(store.ActiveAfterDeleting(store.ActiveCollection));   // nothing remains to take the active spot
        Assert.Equal("Can’t delete the last collection.", store.DeleteCollection(store.ActiveCollection));
        Assert.Single(store.Collections);
    }

    [Fact]
    public void SwitchCollection()
    {
        var store = new MasterStore(2, "Work", [new("Work", new Collection()), new("Personal", new Collection())]);
        Assert.Null(store.SwitchCollection("Personal"));
        Assert.Equal("Personal", store.ActiveCollection);
    }

    [Fact]
    public void SwitchCollectionRejectsUnknownName()
    {
        var store = MasterStore.Empty();
        Assert.Equal("No collection named “Nope”.", store.SwitchCollection("Nope"));
        Assert.Equal("Default", store.ActiveCollection);
    }

    [Fact]
    public void ErrorMessagesUseTypographicPunctuationLikeTheMacApp()
    {
        var store = MasterStore.Empty();
        var duplicate = store.AddCollection("Default", false)!;
        Assert.Equal('“', duplicate[duplicate.IndexOf("Default", StringComparison.Ordinal) - 1]);
        Assert.Equal('”', duplicate[duplicate.IndexOf("Default", StringComparison.Ordinal) + "Default".Length]);
        Assert.Contains('’', store.DeleteCollection(store.ActiveCollection)!);
    }

    /// <summary>C#-only: MasterStore is a class here, so a copy has to be made; a Swift struct copies on assignment.</summary>
    [Fact]
    public void CloneIsDeepAndEqual()
    {
        var store = TwoCollections();
        var clone = store.Clone();
        Assert.Equal(store, clone);
        clone.Mcps["z"] = Entry("https://z.example/mcp");
        Assert.NotEqual(store, clone);
        Assert.False(store.Mcps.ContainsKey("z"));
    }
}
