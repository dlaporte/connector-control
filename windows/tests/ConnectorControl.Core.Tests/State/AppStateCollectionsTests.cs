using System.Text;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// Tests/ConnectorControlStateTests/AppStateCollectionsTests.swift. The sidecar, the
/// machine-local cache and the named collection actions, against the real on-disk layout the
/// harness builds.
///
/// Nothing subscribes or publishes yet, so a synced or published collection is set up the way
/// those flows will leave it — the two files on disk — and read back through a real reload.
/// </summary>
public class AppStateCollectionsTests
{
    private static CollectionsFile.Entry Synced(string fileName, string? relativeToStore = null,
        IEnumerable<KeyValuePair<string, IReadOnlyDictionary<string, CollectionsFile.Need>>>? needs = null) =>
        new(CollectionKind.Synced, fileName, relativeToStore, needs: needs);

    private static CollectionsFile.Entry Published(string slug) =>
        new(CollectionKind.Local, publish: new CollectionsFile.PublishRecord(slug, "origin", PublishIntent.None));

    private static CollectionsLocalCache.SyncedBinding Bound(string? path) => new(path, null);

    private static CollectionsFile File_(params (string Name, CollectionsFile.Entry Entry)[] entries) =>
        new(entries.Select(e => new KeyValuePair<string, CollectionsFile.Entry>(e.Name, e.Entry)));

    private static CollectionsLocalCache Cache(
        IEnumerable<KeyValuePair<string, CollectionsLocalCache.SyncedBinding>>? synced = null,
        IEnumerable<KeyValuePair<string, CollectionsLocalCache.PublishBinding>>? published = null) =>
        new(synced ?? [], published ?? []);

    /// <summary>Writes both files where the app reads them. With a state, reloads so it picks them up.</summary>
    private static void Seed(AppStateHarness h, AppState? state, CollectionsFile file, CollectionsLocalCache? cache = null)
    {
        file.Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        (cache ?? Cache()).Save(state?.Service.Paths.CollectionsCachePath
            ?? Path.Combine(h.StoreDir, CollectionsLocalCache.FileName));
        state?.Reload();
    }

    /// <summary>A store on disk with the named collections, so a sidecar entry has something to annotate.</summary>
    private static void SeedStore(AppStateHarness h, params string[] collections)
    {
        var store = MasterStore.Empty();
        foreach (var name in collections)
        {
            Assert.Null(store.AddCollection(name, copyingCurrent: false));
        }
        store.ActiveCollection = "Default";
        MasterStoreIO.Save(store, h.MasterStorePath);
    }

    // MARK: load

    [Fact]
    public void TheSidecarAndCacheLoadWithTheStoreAndReconcile()
    {
        using var h = new AppStateHarness();
        Seed(h, null, File_(("Ghost", Synced("g.json"))),
            Cache([new("Ghost", Bound("/nowhere/g.json"))],
                  [new("Ghost", new CollectionsLocalCache.PublishBinding("/nowhere", null))]));
        using var state = h.Create();
        // A sidecar entry with no collection in the store is dropped, and with it every binding
        // the sidecar no longer vouches for.
        Assert.Empty(state.CollectionsFile.Collections);
        Assert.Empty(state.CollectionsCache.Synced);
        Assert.Empty(state.CollectionsCache.Published);
        Assert.Equal(CollectionKind.Local, state.KindOf(state.ActiveCollection));
        Assert.False(state.ActiveCollectionIsSynced);
    }

    [Fact]
    public void AnUnreadableSidecarLeavesTheCacheAlone()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Seed(h, state, File_(("Default", Published("default"))),
            Cache(published: [new("Default", new CollectionsLocalCache.PublishBinding("/tmp/share", null))]));
        Assert.Equal("/tmp/share", state.CollectionsCache.Published["Default"].Folder);

        TempDir.Touch(Path.Combine(h.StoreDir, CollectionsFile.FileName), "{not json");
        state.Reload();
        // A half-written sidecar must not be read as "nothing is published any more".
        Assert.Equal("/tmp/share", state.CollectionsCache.Published["Default"].Folder);
        Assert.True(state.IsPublished("Default"));
    }

    [Fact]
    public void AnUnlocatedSyncedCollectionBindsAFileBesideTheStore()
    {
        using var h = new AppStateHarness();
        SeedStore(h, "Team");
        Seed(h, null, File_(("Team", Synced("team.json", "shared/team.json"))));
        var source = Path.Combine(h.StoreDir, "shared", "team.json");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        TempDir.Touch(source, "{\"version\":1}");

        using var state = h.Create();
        Assert.Equal(source, state.SourceBinding("Team")?.Path);
        Assert.Equal(ContentHash.Sha256(Encoding.UTF8.GetBytes("{\"version\":1}")), state.SourceBinding("Team")?.LastHash);
        // The binding is persisted, so the next launch starts bound.
        Assert.Equal(source, CollectionsLocalCache.Load(state.Service.Paths.CollectionsCachePath).Synced["Team"].Path);
        Assert.Null(state.CollectionBanner);
    }

    [Fact]
    public void ASyncedCollectionWithNoFileBesideTheStoreKeepsAskingToBeLocated()
    {
        using var h = new AppStateHarness();
        SeedStore(h, "Team");
        Seed(h, null, File_(("Team", Synced("team.json", "shared/team.json"))));
        using var state = h.Create();
        Assert.Null(state.SourceBinding("Team")?.Path);
        Assert.Equal(new CollectionBanner.Locate("Team", "team.json"), state.CollectionBanner);
    }

    [Fact]
    public void ACustomStoreDirKeepsTheCacheMachineLocal()
    {
        using var h = new AppStateHarness();
        var custom = h.Dir.File("Dropbox");
        h.Settings.MasterStoreDir = custom;
        using var state = h.Create();
        Assert.Equal(custom, state.Service.Paths.StoreDir);
        // The bindings name files that exist on this machine only.
        Assert.Equal(Path.Combine(h.StoreDir, CollectionsLocalCache.FileName), state.Service.Paths.CollectionsCachePath);
        Assert.False(state.Service.Paths.CollectionsCachePath.StartsWith(custom, StringComparison.Ordinal));
    }

    // MARK: named actions

    [Fact]
    public void CreateRenameAndDeleteByName()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Work"));
        Assert.Null(state.RenameCollection("Work", "Team"));
        Assert.Equal(["Default", "Team"], state.CollectionNames);
        // A new collection becomes the active one, as the chip menu has always done.
        Assert.Equal("Team", state.ActiveCollection);
        Assert.Null(state.DeleteCollection("Team"));
        Assert.Equal(["Default"], state.CollectionNames);
        Assert.Equal(AppState.LastLocalCollectionError, state.DeleteCollection(state.ActiveCollection));
    }

    [Fact]
    public void CreateCopiesTheActiveCollectionAndReportsItsErrors()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Equal("A collection named “Default” already exists.", state.CreateCollection("Default"));
        Assert.Equal(AppState.NameEmptyError, state.CreateCollection("   "));
        Assert.Equal(["Default"], state.CollectionNames);
        Assert.Null(state.CreateCollection("Work"));
        Assert.Equal(["aws-mcp", "scoutbook", "service-now"], state.SortedNames);   // a COPY of the active collection
        Assert.Equal(h.Now, h.Settings.LastApplyDate);
    }

    [Fact]
    public void RenameAndDeleteReportTheStoresErrors()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Equal("No collection named “Nope”.", state.RenameCollection("Nope", "Q"));
        Assert.Equal("No collection named “Nope”.", state.DeleteCollection("Nope"));
        Assert.Null(state.CreateCollection("Work"));
        Assert.Equal("A collection named “Default” already exists.", state.RenameCollection("Work", "Default"));
        Assert.Equal(AppState.NameEmptyError, state.RenameCollection("Work", " "));
    }

    [Fact]
    public void DeletingTheActiveCollectionSwitchesToTheAlphabeticallyFirstRemaining()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Zeta"));
        Assert.Null(state.CreateCollection("Work"));
        Assert.Equal("Work", state.ActiveCollection);
        Assert.Null(state.DeleteCollection("Work"));
        Assert.Equal(["Default", "Zeta"], state.CollectionNames);
        Assert.Equal("Default", state.ActiveCollection);
        Assert.Equal("Default", h.StoreOnDisk().ActiveCollection);
    }

    [Fact]
    public void TheLastLocalCollectionStaysEvenBesideASyncedOne()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        Seed(h, state, File_(("Team", Synced("team.json"))));
        Assert.Equal(AppState.LastLocalCollectionError, state.DeleteCollection("Default"));
        Assert.Null(state.DeleteCollection("Team"));   // a synced collection is not the one that has to stay
        Assert.Equal(["Default"], state.CollectionNames);
    }

    [Fact]
    public void DeletingASyncedCollectionDropsItsBindingAndLeavesTheSourceFile()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var source = h.Dir.File("team.json");
        TempDir.Touch(source, "{\"version\":1}");
        Assert.Null(state.CreateCollection("Team"));
        Seed(h, state, File_(("Team", Synced("team.json"))), Cache([new("Team", Bound(source))]));
        Assert.Equal(source, state.SourceBinding("Team")?.Path);

        Assert.Null(state.DeleteCollection("Team"));
        Assert.False(state.CollectionsFile.Collections.ContainsKey("Team"));
        Assert.Null(state.SourceBinding("Team"));
        Assert.True(File.Exists(source), "the source file is never ours to delete");
    }

    [Fact]
    public void RenamingCarriesTheSidecarEntryTheBindingsAndTheDerivedState()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        Seed(h, state, File_(("Team", Synced("team.json"))), Cache([new("Team", Bound("/shared/team.json"))]));
        state.PendingUpdates = new Dictionary<string, CollectionDiff>(StringComparer.Ordinal)
        {
            ["Team"] = new CollectionDiff(["jira"], [], []),
        };
        state.SourceErrors = new Dictionary<string, string>(StringComparer.Ordinal) { ["Team"] = "unreadable" };
        state.PublishError = new CollectionPublishError("Team", "no room");

        Assert.Null(state.RenameCollection("Team", "Data"));
        Assert.False(state.CollectionsFile.Collections.ContainsKey("Team"));
        Assert.Equal("team.json", state.CollectionsFile.Collections["Data"].FileName);
        Assert.Equal("/shared/team.json", state.SourceBinding("Data")?.Path);
        Assert.Equal(["jira"], state.PendingUpdates["Data"].Added);
        Assert.Equal("unreadable", state.SourceErrors["Data"]);
        Assert.Equal("Data", state.PublishError?.Collection);
        Assert.Equal("Data", state.ActiveCollection);
    }

    // MARK: persist

    [Fact]
    public void PersistWritesTheSidecarBesideTheStore()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Work"));
        Assert.True(File.Exists(Path.Combine(h.StoreDir, CollectionsFile.FileName)));
        Assert.True(File.Exists(state.Service.Paths.CollectionsCachePath));
    }

    [Fact]
    public void TheSidecarAndCacheSurviveARestart()
    {
        using var h = new AppStateHarness();
        using (var first = h.Create())
        {
            Assert.Null(first.CreateCollection("Team"));
            Seed(h, first, File_(("Team", Synced("team.json"))), Cache([new("Team", Bound("/shared/team.json"))]));
            Assert.Null(first.RenameCollection("Team", "Data"));   // a real persist of both files
        }

        using var second = h.Create();
        Assert.Equal(CollectionKind.Synced, second.KindOf("Data"));
        Assert.Equal("/shared/team.json", second.SourceBinding("Data")?.Path);
    }

    // MARK: queries

    [Fact]
    public void TheCollectionQueriesReadTheSidecarAndTheStore()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        var tokenPointer = JsonPointer.Parse("/env/DBT_TOKEN")!;
        var config = JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String($"{Placeholder.DirectoryToken}/server.js")])),
            ("env", JsonValue.Object(("DBT_TOKEN", JsonValue.String(Placeholder.Marker("DBT_TOKEN"))))));
        Assert.Null(state.Upsert("dbt", new McpEntry(config), null));
        Seed(h, state, File_(("Team", Synced("team.json", needs: [
            new("dbt", new Dictionary<string, CollectionsFile.Need>(StringComparer.Ordinal)
            {
                ["DBT_TOKEN"] = new CollectionsFile.Need("your dbt token", tokenPointer),
            }),
        ]))));

        Assert.True(state.IsSynced("Team"));
        Assert.True(state.ActiveCollectionIsSynced);
        Assert.False(state.IsSynced("Default"));
        Assert.False(state.IsPublished("Team"));
        Assert.Equal(["Default"], state.LocalCollectionNames);
        Assert.Equal("your dbt token", state.Needs("dbt", "Team")["DBT_TOKEN"].Hint);
        Assert.Empty(state.Needs("dbt", "Default"));
        Assert.Equal(AppState.NeedsValueCaution("DBT_TOKEN"), state.ConnectorCaution("dbt", "Team"));
        Assert.Null(state.ConnectorCaution("aws-mcp", "Team"));
        Assert.Null(state.ConnectorCaution("nope", "Team"));

        // With the marker filled, the unbound directory token is what is left to complain about.
        var filled = config.Replacing(tokenPointer, JsonValue.String("secret"))!;
        Assert.Null(state.Upsert("dbt", new McpEntry(filled), "dbt"));
        Assert.Equal(AppState.LocateCaution, state.ConnectorCaution("dbt", "Team"));
    }

    [Fact]
    public void PublishingIsAFactAboutALocalCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Seed(h, state, File_(("Default", Published("default"))),
            Cache(published: [new("Default", new CollectionsLocalCache.PublishBinding("/tmp/share", null))]));
        Assert.True(state.IsPublished("Default"));
        // Publishing is a fact about a local collection, not a third kind.
        Assert.Equal(CollectionKind.Local, state.KindOf("Default"));
        Assert.False(state.IsPublished("Nope"));
    }

    // MARK: banner

    [Fact]
    public void TheBannerFollowsItsPrecedence()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Alpha"));
        Assert.Null(state.CreateCollection("Team"));
        var bothSynced = File_(("Alpha", Synced("alpha.json")), ("Team", Synced("team.json")));
        Seed(h, state, bothSynced);
        Assert.Equal("Team", state.ActiveCollection);

        // Locate: the active collection first, then the alphabetically first of the rest.
        Assert.Equal(new CollectionBanner.Locate("Team", "team.json"), state.CollectionBanner);
        Seed(h, state, bothSynced, Cache([new("Team", Bound("/shared/team.json"))]));
        Assert.Equal(new CollectionBanner.Locate("Alpha", "alpha.json"), state.CollectionBanner);

        // Any pending update outranks any locate; the active collection's outranks the rest.
        var alphaDiff = new CollectionDiff(["jira"], [], []);
        state.PendingUpdates = new Dictionary<string, CollectionDiff>(StringComparer.Ordinal) { ["Alpha"] = alphaDiff };
        Assert.Equal(new CollectionBanner.UpdateAvailable("Alpha", alphaDiff.Summary()), state.CollectionBanner);
        var teamDiff = new CollectionDiff([], ["datadog"], []);
        state.PendingUpdates = new Dictionary<string, CollectionDiff>(StringComparer.Ordinal)
        {
            ["Alpha"] = alphaDiff,
            ["Team"] = teamDiff,
        };
        Assert.Equal(new CollectionBanner.UpdateAvailable("Team", teamDiff.Summary()), state.CollectionBanner);

        // A failed publish outranks everything, for any collection.
        state.PublishError = new CollectionPublishError("Default", "the folder is read-only");
        Assert.Equal(new CollectionBanner.PublishFailed("Default", "the folder is read-only"), state.CollectionBanner);
        state.PublishError = null;
        Assert.Equal(new CollectionBanner.UpdateAvailable("Team", teamDiff.Summary()), state.CollectionBanner);
    }

    [Fact]
    public void APurelyLocalSetupHasNoBanner()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Work"));
        Assert.Null(state.CollectionBanner);
        Assert.Empty(state.PendingUpdates);
        Assert.Empty(state.SourceErrors);
        Assert.Null(state.PublishError);
    }
}
