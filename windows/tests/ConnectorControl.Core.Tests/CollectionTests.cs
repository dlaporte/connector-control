using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

public class CollectionTests : IDisposable
{
    private readonly TempDir dir = new("collection-store");
    private string Url => dir.File("mcps.json");

    public void Dispose() => dir.Dispose();

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

    [Fact]
    public void UnknownActiveCollectionFallsBackToExistingCollection()
    {
        File.WriteAllText(Url, "{\"version\":2,\"activeProfile\":\"Ghost\",\"profiles\":{\"Alpha\":{\"mcps\":{}},\"Beta\":{\"mcps\":{}}}}");
        var (store, corrupt) = MasterStoreIO.Load(Url);
        Assert.Null(corrupt);
        Assert.Equal("Alpha", store.ActiveCollection);
    }

    [Fact]
    public void V1FormatFileIsTreatedAsCorruptAndRebuilt()
    {
        File.WriteAllText(Url, "{\"version\":1,\"mcps\":{\"scoutbook\":{\"enabled\":true,\"config\":{\"command\":\"npx\",\"args\":[\"-y\",\"mcp-remote\",\"https://example.com/mcp\"]},\"lastEditView\":\"form\"}}}");
        var (store, corrupt) = MasterStoreIO.Load(Url);
        Assert.Equal(MasterStore.Empty(), store);
        Assert.NotNull(corrupt);
        Assert.StartsWith("mcps.corrupt.", Path.GetFileName(corrupt), StringComparison.Ordinal);
        Assert.False(File.Exists(Url));
    }

    [Theory]
    [InlineData("{\"version\":2,\"activeProfile\":\"D\",\"profiles\":{\"D\":{\"mcps\":{\"s\":{\"enabled\":true,\"config\":{}}}}}}")]                    // lastEditView missing
    [InlineData("{\"version\":2,\"activeProfile\":\"D\",\"profiles\":{\"D\":{\"mcps\":{\"s\":{\"enabled\":\"yes\",\"config\":{},\"lastEditView\":\"form\"}}}}}")]  // enabled not bool
    [InlineData("{\"version\":2,\"activeProfile\":\"D\",\"profiles\":{\"D\":{\"mcps\":{\"s\":{\"enabled\":true,\"config\":{},\"lastEditView\":\"grid\"}}}}}")]   // unknown view
    [InlineData("{\"version\":\"2\",\"activeProfile\":\"D\",\"profiles\":{\"D\":{\"mcps\":{}}}}")]                                                  // version not a number
    [InlineData("{\"version\":2,\"profiles\":{\"D\":{\"mcps\":{}}}}")]                                                                             // activeProfile missing
    [InlineData("[]")]
    public void RequiredKeysAreStrictLikeSwiftCodable(string json)
    {
        Assert.Throws<FormatException>(() => MasterStore.FromJson(JsonValue.Parse(json)));
    }

    [Fact]
    public void UnknownKeysAreIgnored()
    {
        var store = MasterStore.FromJson(JsonValue.Parse("{\"version\":2,\"activeProfile\":\"D\",\"future\":1,\"profiles\":{\"D\":{\"mcps\":{},\"note\":\"x\"}}}"));
        Assert.Equal("D", store.ActiveCollection);
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
        Assert.Null(store.DeleteCollection(store.ActiveCollection));
        Assert.Equal("Alpha", store.ActiveCollection);
        Assert.False(store.Collections.ContainsKey("Work"));
    }

    [Fact]
    public void DeleteActiveCollectionRejectsLastCollection()
    {
        var store = MasterStore.Empty();
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

    [Fact]
    public void VersionBeyondInt32IsAcceptedLikeSwiftInt64()
    {
        var store = MasterStore.FromJson(JsonValue.Parse("{\"version\":99999999999,\"activeProfile\":\"D\",\"profiles\":{\"D\":{\"mcps\":{}}}}"));
        Assert.Equal(99999999999L, store.Version);
    }
}
