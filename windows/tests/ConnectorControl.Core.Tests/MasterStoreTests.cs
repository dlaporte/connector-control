using System.Text;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

public class MasterStoreTests : IDisposable
{
    private readonly TempDir dir = new("store");
    private string Url => dir.File("mcps.json");

    public void Dispose() => dir.Dispose();

    private static JsonValue Cmd(string command) => JsonValue.Object(("command", JsonValue.String(command)));

    [Fact]
    public void EnabledServersRendersEnabledSubset()
    {
        var store = new MasterStore(new Dictionary<string, McpEntry>
        {
            ["on"] = new McpEntry(true, Cmd("a")),
            ["off"] = new McpEntry(false, Cmd("b")),
        });
        Assert.Equal(new Dictionary<string, JsonValue> { ["on"] = Cmd("a") }, store.EnabledServers);
    }

    [Fact]
    public void RenamesANonActiveCollectionAndKeepsTheActiveName()
    {
        var store = new MasterStore(2, "A", [
            new KeyValuePair<string, Collection>("A", new Collection()),
            new KeyValuePair<string, Collection>("B", new Collection()),
        ]);
        Assert.Null(store.RenameCollection("B", "C"));
        Assert.Equal(new HashSet<string>(["A", "C"], StringComparer.Ordinal), store.Collections.Keys.ToHashSet(StringComparer.Ordinal));
        Assert.Equal("A", store.ActiveCollection);
        Assert.Null(store.RenameCollection("A", "Z"));
        Assert.Equal("Z", store.ActiveCollection);
        Assert.NotNull(store.RenameCollection("Z", "C"));   // a taken name is refused
    }

    [Fact]
    public void DeletesANonActiveCollectionAndRefusesTheLast()
    {
        var store = new MasterStore(2, "A", [
            new KeyValuePair<string, Collection>("A", new Collection()),
            new KeyValuePair<string, Collection>("B", new Collection()),
        ]);
        Assert.Null(store.DeleteCollection("B"));
        Assert.NotNull(store.DeleteCollection("A"));   // the last collection stays
        store = new MasterStore(2, "B", [
            new KeyValuePair<string, Collection>("A", new Collection()),
            new KeyValuePair<string, Collection>("B", new Collection()),
        ]);
        Assert.Null(store.DeleteCollection("B"));
        Assert.Equal("A", store.ActiveCollection);   // deleting the active one moves to the sorted-first remaining
    }

    [Fact]
    public void LoadMissingFileReturnsEmptyStore()
    {
        var (store, corrupt) = MasterStoreIO.Load(Url);
        Assert.Equal(MasterStore.Empty(), store);
        Assert.Null(corrupt);
    }

    [Fact]
    public void ReadingMcpsDoesNotCreateACollection()
    {
        var store = new MasterStore(2, "Work", [new KeyValuePair<string, Collection>("Work", new Collection())]);
        store.ActiveCollection = "Ghost";   // an active collection the store has no Collection object for
        Assert.Empty(store.Mcps);
        Assert.False(store.Collections.ContainsKey("Ghost"), "reading Mcps must not create a collection as a side effect");
        Assert.Single(store.Collections);
    }

    [Fact]
    public void EnabledCountCountsWithoutBuildingTheServerDictionary()
    {
        var store = new MasterStore(new Dictionary<string, McpEntry>
        {
            ["on"] = new McpEntry(true, Cmd("a")),
            ["off"] = new McpEntry(false, Cmd("b")),
            ["on2"] = new McpEntry(true, Cmd("c")),
        });
        Assert.Equal(2, store.EnabledCount);
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var store = MasterStore.Empty();
        store.Mcps["scoutbook"] = new McpEntry(false,
            JsonValue.Object(("command", JsonValue.String("npx")),
                ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("mcp-remote"), JsonValue.String("https://example.com/mcp")]))),
            EditView.Json);
        MasterStoreIO.Save(store, Url);
        var (loaded, corrupt) = MasterStoreIO.Load(Url);
        Assert.Equal(store, loaded);
        Assert.Null(corrupt);
    }

    [Fact]
    public void LoadCorruptFilePreservesItAndReturnsEmpty()
    {
        File.WriteAllText(Url, "{not json!!");
        var (store, corrupt) = MasterStoreIO.Load(Url);
        Assert.Equal(MasterStore.Empty(), store);
        Assert.NotNull(corrupt);
        Assert.StartsWith("mcps.corrupt.", Path.GetFileName(corrupt), StringComparison.Ordinal);
        Assert.Equal("{not json!!", File.ReadAllText(corrupt));
        Assert.False(File.Exists(Url));
    }

    [Fact]
    public void ReadIsSideEffectFree()
    {
        Assert.Null(MasterStoreIO.Read(Url));
        File.WriteAllText(Url, "{not json!!");
        Assert.Null(MasterStoreIO.Read(Url));
        Assert.Equal("{not json!!", File.ReadAllText(Url));
        var store = MasterStore.Empty();
        store.Mcps["s"] = new McpEntry(JsonValue.Object(("command", JsonValue.String("npx"))));
        MasterStoreIO.Save(store, Url);
        Assert.Equal(store, MasterStoreIO.Read(Url));
    }

    [Fact]
    public void LoadCorruptFileReportsOriginalPathWhenMoveFails()
    {
        var fixedNow = DateTime.UnixEpoch.AddSeconds(1_752_600_000);
        File.WriteAllText(Url, "{not json!!");
        var aside = dir.File($"mcps.corrupt.{BackupTimestamp.From(fixedNow)}.json");
        File.WriteAllText(aside, "existing");
        var (store, corrupt) = MasterStoreIO.Load(Url, fixedNow);
        Assert.Equal(MasterStore.Empty(), store);
        Assert.Equal(Url, corrupt);
        Assert.Equal("{not json!!", File.ReadAllText(Url));
    }

    [Fact]
    public void UnknownActiveCollectionFallsBackToExistingCollection()
    {
        File.WriteAllText(Url, """
            {"version":2,"activeProfile":"Ghost","profiles":{"Alpha":{"mcps":{}},"Beta":{"mcps":{}}}}
            """);
        var (store, corrupt) = MasterStoreIO.Load(Url);
        Assert.Null(corrupt);
        Assert.Equal("Alpha", store.ActiveCollection);   // sorted-first existing collection
    }

    [Fact]
    public void SavedFileUsesAppleEncoderFormat()
    {
        var store = MasterStore.Empty();
        store.Mcps["s"] = new McpEntry(JsonValue.Object(("command", JsonValue.String("npx")), ("args", JsonValue.Array([JsonValue.String("https://x.y/z")]))));
        MasterStoreIO.Save(store, Url);
        const string expected =
            "{\n  \"activeProfile\" : \"Default\",\n  \"profiles\" : {\n    \"Default\" : {\n      \"mcps\" : {\n        \"s\" : {\n" +
            "          \"config\" : {\n            \"args\" : [\n              \"https:\\/\\/x.y\\/z\"\n            ],\n            \"command\" : \"npx\"\n          },\n" +
            "          \"enabled\" : true,\n          \"lastEditView\" : \"form\"\n        }\n      }\n    }\n  },\n  \"version\" : 2\n}";
        Assert.Equal(expected, File.ReadAllText(Url, Encoding.UTF8));
    }
}
