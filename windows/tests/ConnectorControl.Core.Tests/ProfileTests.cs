using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

public class ProfileTests : IDisposable
{
    private readonly TempDir dir = new("profile-store");
    private string Url => dir.File("mcps.json");

    public void Dispose() => dir.Dispose();

    private static McpEntry Entry(string url) => new(JsonValue.Object(
        ("command", JsonValue.String("npx")),
        ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("mcp-remote"), JsonValue.String(url)]))));

    private static string[] Keys(Profile? p) => p is null ? [] : p.Mcps.Keys.Order(StringComparer.Ordinal).ToArray();

    private static MasterStore TwoProfiles() => new(2, "Work",
    [
        new("Work", new Profile(new Dictionary<string, McpEntry> { ["a"] = Entry("https://a.example/mcp") })),
        new("Personal", new Profile(new Dictionary<string, McpEntry> { ["b"] = Entry("https://b.example/mcp") })),
    ]);

    // Decoding

    [Fact]
    public void V2RoundTripPreservesTwoProfiles()
    {
        var store = TwoProfiles();
        var decoded = MasterStore.FromJson(JsonValue.Parse(store.ToJson().Serialize()));
        Assert.Equal(store, decoded);
        Assert.Equal("Work", decoded.ActiveProfile);
        Assert.Equal(["a"], Keys(decoded.Profiles["Work"]));
        Assert.Equal(["b"], Keys(decoded.Profiles["Personal"]));
    }

    [Fact]
    public void UnknownActiveProfileFallsBackToExistingProfile()
    {
        File.WriteAllText(Url, "{\"version\":2,\"activeProfile\":\"Ghost\",\"profiles\":{\"Alpha\":{\"mcps\":{}},\"Beta\":{\"mcps\":{}}}}");
        var (store, corrupt) = MasterStoreIO.Load(Url);
        Assert.Null(corrupt);
        Assert.Equal("Alpha", store.ActiveProfile);
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
        Assert.Equal("D", store.ActiveProfile);
    }

    // mcps accessor scoping

    [Fact]
    public void McpsAccessorReadsAndWritesOnlyActiveProfile()
    {
        var store = TwoProfiles();
        store.Mcps["c"] = Entry("https://c.example/mcp");
        Assert.Equal(["a", "c"], Keys(store.Profiles["Work"]));
        Assert.Equal(["b"], Keys(store.Profiles["Personal"]));
    }

    // Profile management

    [Fact]
    public void AddProfileCopyingCurrent()
    {
        var store = new MasterStore(new Dictionary<string, McpEntry> { ["a"] = Entry("https://a.example/mcp") });
        Assert.Null(store.AddProfile("Copy", copyingCurrent: true));
        Assert.Equal("Copy", store.ActiveProfile);
        Assert.Equal(["a"], Keys(store.Profiles["Copy"]));
        Assert.Equal(["a"], Keys(store.Profiles["Default"]));
    }

    [Fact]
    public void AddProfileEmptyStartsBlank()
    {
        var store = new MasterStore(new Dictionary<string, McpEntry> { ["a"] = Entry("https://a.example/mcp") });
        Assert.Null(store.AddProfile("Fresh", copyingCurrent: false));
        Assert.Empty(store.Profiles["Fresh"].Mcps);
    }

    [Fact]
    public void AddProfileRejectsEmptyName()
    {
        Assert.Equal("Name must not be empty.", MasterStore.Empty().AddProfile("   ", false));
    }

    [Fact]
    public void AddProfileRejectsDuplicateName()
    {
        Assert.Equal("A profile named \"Default\" already exists.", MasterStore.Empty().AddProfile("Default", false));
    }

    [Fact]
    public void RenameActiveProfile()
    {
        var store = MasterStore.Empty();
        Assert.Null(store.RenameActiveProfile("Main"));
        Assert.Equal("Main", store.ActiveProfile);
        Assert.Equal(["Main"], store.Profiles.Keys.ToArray());
    }

    [Fact]
    public void RenameActiveProfileRejectsCollision()
    {
        var store = new MasterStore(2, "Work", [new("Work", new Profile()), new("Personal", new Profile())]);
        Assert.Equal("A profile named \"Personal\" already exists.", store.RenameActiveProfile("Personal"));
        Assert.Equal("Work", store.ActiveProfile);
    }

    [Fact]
    public void RenameActiveProfileRejectsEmptyName()
    {
        Assert.NotNull(MasterStore.Empty().RenameActiveProfile("  "));
    }

    [Fact]
    public void DeleteActiveProfileSwitchesToFirstRemaining()
    {
        var store = new MasterStore(2, "Work", [new("Work", new Profile()), new("Alpha", new Profile()), new("Zeta", new Profile())]);
        Assert.Null(store.DeleteActiveProfile());
        Assert.Equal("Alpha", store.ActiveProfile);
        Assert.False(store.Profiles.ContainsKey("Work"));
    }

    [Fact]
    public void DeleteActiveProfileRejectsLastProfile()
    {
        var store = MasterStore.Empty();
        Assert.Equal("Can't delete the last profile.", store.DeleteActiveProfile());
        Assert.Single(store.Profiles);
    }

    [Fact]
    public void SwitchProfile()
    {
        var store = new MasterStore(2, "Work", [new("Work", new Profile()), new("Personal", new Profile())]);
        Assert.Null(store.SwitchProfile("Personal"));
        Assert.Equal("Personal", store.ActiveProfile);
    }

    [Fact]
    public void SwitchProfileRejectsUnknownName()
    {
        var store = MasterStore.Empty();
        Assert.Equal("No profile named \"Nope\".", store.SwitchProfile("Nope"));
        Assert.Equal("Default", store.ActiveProfile);
    }

    [Fact]
    public void CloneIsDeepAndEqual()
    {
        var store = TwoProfiles();
        var clone = store.Clone();
        Assert.Equal(store, clone);
        clone.Mcps["z"] = Entry("https://z.example/mcp");
        Assert.NotEqual(store, clone);
        Assert.False(store.Mcps.ContainsKey("z"));
    }
}
