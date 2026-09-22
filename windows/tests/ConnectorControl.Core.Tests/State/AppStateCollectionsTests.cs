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

    [Fact]
    public void AHalfWrittenSidecarIsNeverOverwrittenByASave()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, null));
        var sidecar = Path.Combine(h.StoreDir, CollectionsFile.FileName);
        var cachePath = state.Service.Paths.CollectionsCachePath;
        var bindings = File.ReadAllBytes(cachePath);

        // A sync tool is halfway through writing the sidecar when the app next looks at it.
        TempDir.Touch(sidecar, "{half");
        state.Reload();
        state.SetEnabled("aws-mcp", false);
        Assert.Equal(AppState.CollectionsNotSavedNote, state.LastError);   // a change that was not written says so
        // A save must not land our copy of the sidecar on top of the real one, and the bindings
        // that hang off it wait too.
        Assert.Equal("{half", File.ReadAllText(sidecar));
        Assert.Equal(bindings, File.ReadAllBytes(cachePath));
        Assert.False(h.StoreOnDisk().Mcps["aws-mcp"].Enabled, "the master list is ours alone, and still saves");

        // The write finishes: the next load reads it, and saves resume.
        File_(("Data team", Synced("data-team.json"))).Save(sidecar);
        state.Reload();
        Assert.Equal(CollectionKind.Synced, state.KindOf("Data team"));
        state.StopSyncing("Data team");
        Assert.Empty(CollectionsFile.Load(sidecar).Collections);
        Assert.Null(state.LastError);   // the save landed, so the note goes with it
    }

    [Fact]
    public void ASidecarChangedElsewhereIsRewrittenWhenOurBytesReturn()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, null));
        var sidecar = Path.Combine(h.StoreDir, CollectionsFile.FileName);

        // Another machine writes the same collection under a different file name.
        var entry = state.CollectionsFile.Collections["Data team"];
        File_(("Data team", new CollectionsFile.Entry(entry.Kind, "moved.json", entry.RelativeToStore,
            entry.Origin, entry.Needs, entry.Publish, entry.Provenance))).Save(sidecar);
        state.Reload();
        Assert.Equal("moved.json", state.CollectionsFile.Collections["Data team"].FileName);

        // Pointing it back at the file this machine has restores exactly the bytes we once wrote.
        Assert.Null(state.LocateSource("Data team", path));
        // What is in memory is what the file must hold, whatever this app last wrote.
        Assert.Equal("data-team.json", CollectionsFile.Load(sidecar).Collections["Data team"].FileName);
    }

    [Fact]
    public void ASidecarSaveFailureSetsTheErrorAndStopsTheChain()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, null));
        var sidecar = Path.Combine(h.StoreDir, CollectionsFile.FileName);
        var cachePath = state.Service.Paths.CollectionsCachePath;
        var bindings = File.ReadAllBytes(cachePath);

        // Nothing can be written where a directory holds the name. Simpler than a permission
        // change, and the same failure on both platforms.
        File.Delete(sidecar);
        Directory.CreateDirectory(sidecar);

        // Stop Syncing changes the sidecar, so the save is really attempted, and it is the one
        // collection action that does not re-apply afterwards and clear the error again.
        state.StopSyncing("Data team");
        Assert.NotNull(state.LastError);
        Assert.Equal(bindings, File.ReadAllBytes(cachePath));   // the cache save behind it never ran
        Assert.True(h.StoreOnDisk().Collections.ContainsKey("Data team"), "the master list saved first, and is intact");
        // The failure is the disk's, not a rollback.
        Assert.False(state.CollectionsFile.Collections.ContainsKey("Data team"));
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

    // MARK: synced collections

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);

    /// <summary>Gives a just-armed source watcher a moment before a test relies on it seeing the
    /// very next write — the same arming race AppStateWatcherTests waits out.</summary>
    private static readonly TimeSpan WatcherSettle = TimeSpan.FromMilliseconds(300);

    /// <summary>The bytes an author's machine would have written, at a path this machine can read.</summary>
    private static void WriteDocument(CollectionDocument doc, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, doc.Serialize());
    }

    /// <summary>One local connector, authored on <paramref name="platform"/>, so a test can pin
    /// what a launcher from the other platform (or a directory token) does without carrying the
    /// four-connector sample.</summary>
    private static CollectionDocument OneLocalConnector(string name, string command, string[] args,
        CollectionPlatform platform = CollectionPlatforms.Current) =>
        new("Tools", null, "o-tools", "2026-09-21T14:02:11Z",
            new Dictionary<string, CollectionDocument.Connector>
            {
                [name] = new(new CollectionDocument.Launcher.Local(command, args, platform)),
            });

    /// <summary>The sample with github gone and dbt's arguments changed — an author's next commit.</summary>
    private static CollectionDocument ChangedSample()
    {
        var sample = CollectionDocumentSamples.DataTeam;
        var connectors = new Dictionary<string, CollectionDocument.Connector>(sample.Connectors, StringComparer.Ordinal);
        connectors.Remove("github");
        var dbt = connectors["dbt"];
        connectors["dbt"] = new CollectionDocument.Connector(
            new CollectionDocument.Launcher.Local("npx", ["-y", "@dbt/mcp@2"], CollectionPlatform.Mac),
            dbt.Env, dbt.Needs, dbt.Additional);
        return new CollectionDocument(sample.Name, sample.Author, sample.Origin, sample.Exported, connectors);
    }

    [Fact]
    public void SubscribeCreatesADisabledReadOnlyMirror()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, null));
        Assert.Equal(CollectionKind.Synced, state.KindOf("Data team"));
        var mcps = state.Store.Collections["Data team"].Mcps;
        Assert.Equal(4, mcps.Count);
        Assert.All(mcps.Values, entry => Assert.False(entry.Enabled));
        Assert.Equal(JsonValue.String("${CC_NEEDS:DBT_TOKEN}"), mcps["dbt"].Config.ValueAt(JsonPointer.Parse("/env/DBT_TOKEN")!));
        Assert.Equal(path, state.SourceBinding("Data team")?.Path);
        Assert.Equal("cloud.getdbt.com ▸ API tokens", state.Needs("dbt", "Data team")["DBT_TOKEN"].Hint);
        Assert.Empty(state.PendingUpdates);
        // Everything in it arrives disabled, so subscribing must not empty Claude's config.
        Assert.Equal("Default", state.ActiveCollection);
        Assert.Equal(["Data team"], state.WatchedSourceCollections);
    }

    [Fact]
    public void SubscribeNamesTheCollectionAndRefusesWhatItCannotRead()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, "Analytics"));   // the caller's name beats the document's
        Assert.Equal(CollectionKind.Synced, state.KindOf("Analytics"));

        Assert.NotNull(state.Subscribe(h.Dir.File("gone.json"), null));
        var half = h.Dir.File("half.json");
        TempDir.Touch(half, "{half");
        Assert.StartsWith("half.json couldn’t be read: ", state.Subscribe(half, null), StringComparison.Ordinal);
        var newer = CollectionDocumentSamples.DataTeam.Encode()
            .Replacing(JsonPointer.Parse("/connectorControlCollection")!, JsonValue.Int(2))!;
        var future = h.Dir.File("future.json");
        File.WriteAllBytes(future, newer.Serialize());
        Assert.Equal(AppState.NewerDocumentError, state.Subscribe(future, null));
        Assert.Equal(["Analytics", "Default"], state.CollectionNames);   // a refused document creates nothing
    }

    [Fact]
    public void SubscribingToYourOwnPublishedCollectionIsRefused()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        // What the publishing task will leave behind: a local collection whose document carries
        // this origin. Reading it back in would make the app its own author.
        Seed(h, state, File_(("Default", new CollectionsFile.Entry(CollectionKind.Local,
            publish: new CollectionsFile.PublishRecord("data-team", "6f1c4a2e-1b8d-4b0e-9f0a-3c2d7e8a91e2", PublishIntent.None)))));
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Equal(AppState.OwnCollectionError, state.Subscribe(path, null));
        Assert.Equal(["Default"], state.CollectionNames);
    }

    [Fact]
    public void ASourceChangeBecomesAPendingUpdateThatApplyLandsWithFilledValuesKept()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, null));
        Thread.Sleep(WatcherSettle);
        // The user fills the token and turns dbt on.
        state.SwitchCollection("Data team");
        var token = JsonPointer.Parse("/env/DBT_TOKEN")!;
        var dbt = state.Store.Collections["Data team"].Mcps["dbt"];
        dbt = dbt with { Config = dbt.Config.Replacing(token, JsonValue.String("tok"))!, Enabled = true };
        // Task 6 gives Upsert a collection argument; until then the edit lands in the active one.
        Assert.Null(state.Upsert("dbt", dbt, "dbt"));
        Assert.Empty(state.PendingUpdates);   // a filled marker is not a change to the collection

        WriteDocument(ChangedSample(), path);
        TempDir.BumpModificationTime(path);
        Assert.True(h.Ui.PumpUntil(() => state.PendingUpdates.ContainsKey("Data team"), Wait));
        Assert.Equal("removes github; changes dbt", state.PendingUpdates["Data team"].Summary());
        Assert.Equal(AppState.CollectionUpdateNotificationBody("Data team", "removes github; changes dbt"), h.Notifier.Sent[^1].Body);
        var announced = h.Notifier.Sent.Count;
        state.RecomputePending();
        Assert.Equal(announced, h.Notifier.Sent.Count);   // one document, one announcement

        Assert.Null(state.ApplyPendingUpdate("Data team"));
        var after = state.Store.Collections["Data team"].Mcps["dbt"];
        Assert.Equal(JsonValue.String("@dbt/mcp@2"), after.Config.ValueAt(JsonPointer.Parse("/args/1")!));
        Assert.Equal(JsonValue.String("tok"), after.Config.ValueAt(token));
        Assert.True(after.Enabled);
        Assert.False(state.Store.Collections["Data team"].Mcps.ContainsKey("github"));
        Assert.Empty(state.PendingUpdates);
        // The active collection's update reaches Claude.
        Assert.Equal(JsonValue.String("@dbt/mcp@2"), h.ClaudeServers()["dbt"].ValueAt(JsonPointer.Parse("/args/1")!));
    }

    [Fact]
    public void AnUnreadableSourceIsTransientUntilRefreshedByHand()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("t.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, "T"));
        Thread.Sleep(WatcherSettle);
        File.WriteAllText(path, "{half");
        TempDir.BumpModificationTime(path);
        h.Ui.PumpUntil(() => h.Delays.Pending.Count > 0, Wait);
        Assert.Empty(state.SourceErrors);   // the first failure schedules a retry instead of reporting
        Assert.Single(h.Delays.Pending);
        Assert.Equal(TimeSpan.FromSeconds(2), h.Delays.Pending[0].Delay);

        state.RefreshSource("T");
        Assert.NotNull(state.SourceErrors["T"]);
        Assert.Single(h.Delays.Pending);   // Refresh answers now instead of waiting again

        // The half-written file lands in full: the next read clears the error and the collection
        // is back to having nothing to say.
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        state.RefreshSource("T");
        Assert.Empty(state.SourceErrors);
        Assert.Empty(state.PendingUpdates);
    }

    [Fact]
    public void LocateBindsAndTheRelativePathBindsAutomatically()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        // The document travels inside the store's own folder, as a shared master list does.
        var inStore = Path.Combine(h.StoreDir, "shared", "data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, inStore);
        Assert.Null(state.Subscribe(inStore, null));
        Assert.Equal("shared/data-team.json", state.CollectionsFile.Collections["Data team"].RelativeToStore);

        // Another machine: the sidecar travels with the store, this machine's bindings do not.
        File.Delete(state.Service.Paths.CollectionsCachePath);
        state.Reload();
        Assert.Equal(inStore, state.SourceBinding("Data team")?.Path);   // found beside the store, with no prompt

        // A document somewhere the relative path cannot reach is pointed at by hand.
        var elsewhere = h.Dir.File(Path.Combine("elsewhere", "data-team.json"));
        WriteDocument(CollectionDocumentSamples.DataTeam, elsewhere);
        Assert.Null(state.LocateSource("Data team", elsewhere));
        Assert.Equal(elsewhere, state.SourceBinding("Data team")?.Path);
        Assert.Equal(["Data team"], state.WatchedSourceCollections);
        Assert.Empty(state.PendingUpdates);   // the same document in a new place changes nothing
        Assert.Null(state.CollectionBanner);
        Assert.StartsWith("nope.json couldn’t be read: ", state.LocateSource("Data team", h.Dir.File("nope.json")), StringComparison.Ordinal);
        // A file we cannot read is not bound.
        Assert.Equal(elsewhere, state.SourceBinding("Data team")?.Path);
    }

    [Fact]
    public void StopSyncingKeepsContentAndDropsTheBinding()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, null));
        Thread.Sleep(WatcherSettle);
        var before = new Dictionary<string, McpEntry>(state.Store.Collections["Data team"].Mcps, StringComparer.Ordinal);

        state.StopSyncing("Data team");
        Assert.Equal(CollectionKind.Local, state.KindOf("Data team"));
        Assert.Null(state.SourceBinding("Data team"));
        Assert.False(state.CollectionsFile.Collections.ContainsKey("Data team"));
        // The connectors are the user's now.
        Assert.True(DictionaryEquality.Equal(before, state.Store.Collections["Data team"].Mcps));
        Assert.Empty(state.WatchedSourceCollections);
        Assert.Empty(state.Needs("ledger", "Data team"));   // the hints travelled with the document
        // An unfilled marker is still text in the config, so the row still says so.
        Assert.Equal(AppState.NeedsValueCaution("server_path"), state.ConnectorCaution("ledger", "Data team"));

        // The author's next change reaches nobody: there is no binding left to watch.
        WriteDocument(ChangedSample(), path);
        TempDir.BumpModificationTime(path);
        h.Ui.PumpUntil(() => false, TimeSpan.FromSeconds(1));
        Assert.Empty(state.PendingUpdates);
    }

    [Fact]
    public void DeletingASyncedCollectionLeavesTheFileAlone()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, null));

        Assert.Null(state.DeleteCollection("Data team"));
        Assert.Equal(["Default"], state.CollectionNames);
        Assert.Null(state.SourceBinding("Data team"));
        Assert.Empty(state.WatchedSourceCollections);
        Assert.True(File.Exists(path), "the source file is never ours to delete");
    }

    [Fact]
    public void TheDirectoryTokenExpandsAgainstTheBoundFolderWhenApplied()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File(Path.Combine("tools", "servers.json"));
        WriteDocument(OneLocalConnector("x", "node", [$"{Placeholder.DirectoryToken}/srv.js"]), path);
        Assert.Null(state.Subscribe(path, null));
        state.SwitchCollection("Tools");
        state.SetEnabled("x", true);

        var directory = Path.GetDirectoryName(path)!;
        var argument = JsonPointer.Parse("/args/0")!;
        Assert.Equal(JsonValue.String(directory + "/srv.js"), h.ClaudeServers()["x"].ValueAt(argument));
        // The store keeps the token, so the same list resolves on the next machine too.
        Assert.Equal(JsonValue.String($"{Placeholder.DirectoryToken}/srv.js"),
            state.Store.Collections["Tools"].Mcps["x"].Config.ValueAt(argument));
        Assert.Null(state.ConnectorCaution("x", "Tools"));
        Assert.False(state.ApplyRetryNeeded);
        state.Reload();
        // The expanded config is what we wrote, so a reload finds nothing to regenerate.
        Assert.Equal(JsonValue.String(directory + "/srv.js"), h.ClaudeServers()["x"].ValueAt(argument));
    }

    [Fact]
    public void ALocalConnectorAuthoredOnTheOtherPlatformIsFlagged()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var other = CollectionPlatforms.Current == CollectionPlatform.Mac ? CollectionPlatform.Windows : CollectionPlatform.Mac;
        var path = h.Dir.File("tools.json");
        WriteDocument(OneLocalConnector("x", "node", ["srv.js"], other), path);
        Assert.Null(state.Subscribe(path, null));
        Assert.Equal(AppState.AuthoredElsewhereCaution, state.ConnectorCaution("x", "Tools"));

        WriteDocument(OneLocalConnector("x", "node", ["srv.js"]), path);
        state.RefreshSource("Tools");
        Assert.Null(state.ConnectorCaution("x", "Tools"));   // a launcher from this platform needs no warning
    }

    [Fact]
    public void AnUnchangedSourceIsNotReRenderedButPendingIsReDerived()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, null));
        state.SwitchCollection("Data team");
        var renders = state.SourceRenders;

        // A local edit makes the collection differ from its source without the file moving.
        var argument = JsonPointer.Parse("/args/1")!;
        var dbt = state.Store.Collections["Data team"].Mcps["dbt"];
        Assert.Null(state.Upsert("dbt", dbt with { Config = dbt.Config.Replacing(argument, JsonValue.String("@dbt/mcp@local"))! }, "dbt"));
        Assert.Equal("changes dbt", state.PendingUpdates["Data team"].Summary());
        Assert.Equal(renders, state.SourceRenders);   // the same bytes are never decoded twice

        // Another machine applies the source, and its master list arrives here.
        var store = h.StoreOnDisk();
        var rendered = state.PendingDocument("Data team")!;
        var applied = CollectionApply.Apply(rendered, store.Collections["Data team"].Mcps,
            state.CollectionsFile.Collections["Data team"].Needs);
        store.Collections["Data team"] = new Collection(applied.Entries);
        MasterStoreIO.Save(store, h.MasterStorePath);
        state.Reload();
        // The list already matches the document; the banner must not outlive it.
        Assert.Empty(state.PendingUpdates);
        Assert.Equal(renders, state.SourceRenders);
    }

    [Fact]
    public void TheRetryChainReportsOnlyTheThirdFailure()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("t.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, "T"));
        Thread.Sleep(WatcherSettle);
        File.WriteAllText(path, "{half");
        TempDir.BumpModificationTime(path);
        Assert.True(h.Ui.PumpUntil(() => h.Delays.Pending.Count > 0, Wait));

        Assert.Equal([TimeSpan.FromSeconds(2)], h.Delays.Pending.Select(p => p.Delay));
        Assert.Empty(state.SourceErrors);
        h.Delays.RunNext();
        Assert.Equal([TimeSpan.FromSeconds(10)], h.Delays.Pending.Select(p => p.Delay));
        Assert.Empty(state.SourceErrors);   // the second failure is still worth waiting out
        h.Delays.RunNext();
        Assert.Equal([TimeSpan.FromSeconds(30)], h.Delays.Pending.Select(p => p.Delay));
        Assert.NotNull(state.SourceErrors["T"]);   // the third consecutive failure is the one to report
        h.Delays.RunNext();
        Assert.Empty(h.Delays.Pending);   // the backoff gives up after the third retry
        Assert.NotNull(state.SourceErrors["T"]);

        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        state.RefreshSource("T");
        Assert.Empty(state.SourceErrors);
    }

    [Fact]
    public void APendingUpdateFoundAtLaunchIsNotAnnouncedTwice()
    {
        using var h = new AppStateHarness();
        var path = h.Dir.File("data-team.json");
        using (var first = h.Create())
        {
            WriteDocument(CollectionDocumentSamples.DataTeam, path);
            Assert.Null(first.Subscribe(path, null));
        }

        var sample = CollectionDocumentSamples.DataTeam;
        var connectors = new Dictionary<string, CollectionDocument.Connector>(sample.Connectors, StringComparer.Ordinal);
        connectors.Remove("github");
        WriteDocument(new CollectionDocument(sample.Name, sample.Author, sample.Origin, sample.Exported, connectors), path);
        h.Notifier.Sent.Clear();

        using var second = h.Create();
        Assert.Equal("removes github", second.PendingUpdates["Data team"].Summary());
        Assert.Empty(h.Notifier.Sent);   // the banner already says it; a launch is not news
        second.Reload();
        Assert.Empty(h.Notifier.Sent);   // and the reload behind it must not announce it either
    }

    [Fact]
    public void ApplyingAnInactiveCollectionLeavesClaudesConfigAlone()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, null));
        Thread.Sleep(WatcherSettle);
        Assert.Equal("Default", state.ActiveCollection);
        var before = h.ClaudeServers();

        WriteDocument(ChangedSample(), path);
        TempDir.BumpModificationTime(path);
        Assert.True(h.Ui.PumpUntil(() => state.PendingUpdates.ContainsKey("Data team"), Wait));

        Assert.Null(state.ApplyPendingUpdate("Data team"));
        Assert.False(state.Store.Collections["Data team"].Mcps.ContainsKey("github"));
        // Claude runs the active collection, and that one did not change.
        Assert.True(DictionaryEquality.Equal(before, h.ClaudeServers()));
        Assert.False(state.NeedsClaudeRestart);
    }

    [Fact]
    public void LocateAppliesTheExpandedPathAtOnce()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File(Path.Combine("tools", "servers.json"));
        WriteDocument(OneLocalConnector("x", "node", [$"{Placeholder.DirectoryToken}/srv.js"]), path);
        Assert.Null(state.Subscribe(path, null));
        // A machine that has the collection but not the file yet: the bindings never travel.
        File.Delete(state.Service.Paths.CollectionsCachePath);
        state.Reload();
        Assert.Null(state.SourceBinding("Tools")?.Path);
        state.SwitchCollection("Tools");
        state.SetEnabled("x", true);
        var argument = JsonPointer.Parse("/args/0")!;
        // With nothing bound the token is written as it stands.
        Assert.Equal(JsonValue.String($"{Placeholder.DirectoryToken}/srv.js"), h.ClaudeServers()["x"].ValueAt(argument));
        Assert.Equal(AppState.LocateCaution, state.ConnectorCaution("x", "Tools"));

        Assert.Null(state.LocateSource("Tools", path));
        // Locating resolves it without another click.
        Assert.Equal(JsonValue.String(Path.GetDirectoryName(path) + "/srv.js"), h.ClaudeServers()["x"].ValueAt(argument));
        Assert.Null(state.ConnectorCaution("x", "Tools"));
    }

    [Fact]
    public void StopSyncingBakesTheExpandedPathIn()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File(Path.Combine("tools", "servers.json"));
        WriteDocument(OneLocalConnector("x", "node", [$"{Placeholder.DirectoryToken}/srv.js"]), path);
        Assert.Null(state.Subscribe(path, null));
        state.SwitchCollection("Tools");
        state.SetEnabled("x", true);
        var argument = JsonPointer.Parse("/args/0")!;
        var expanded = JsonValue.String(Path.GetDirectoryName(path) + "/srv.js");
        Assert.Equal(expanded, h.ClaudeServers()["x"].ValueAt(argument));

        state.StopSyncing("Tools");
        // The folder it resolved to is the collection's own path now, so what Claude runs does
        // not change under the user.
        Assert.Equal(expanded, state.Store.Collections["Tools"].Mcps["x"].Config.ValueAt(argument));
        Assert.Equal(expanded, h.ClaudeServers()["x"].ValueAt(argument));
        Assert.Null(state.ConnectorCaution("x", "Tools"));
        Assert.Equal(CollectionKind.Local, state.KindOf("Tools"));
    }

    [Fact]
    public void AnExcludedConnectorIsRecordedInTheCacheAndNeverShowsAsAdded()
    {
        // The Mac has no cmd /c launcher, so it excludes nothing and this test exists only here;
        // the Swift mirror says so where this test would sit.
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("risky.json");
        WriteDocument(new CollectionDocument("Risky", null, "o-risky", "2026-09-21T14:02:11Z",
            new Dictionary<string, CollectionDocument.Connector>
            {
                ["bad"] = new(new CollectionDocument.Launcher.Remote("https://h/mcp&calc", CollectionDocument.Auth.Auto, "mcp-remote", [])),
                ["good"] = new(new CollectionDocument.Launcher.Remote("https://h/mcp", CollectionDocument.Auth.Auto, "mcp-remote", [])),
            }), path);
        Assert.Null(state.Subscribe(path, null));
        Assert.Equal(["good"], state.Store.Collections["Risky"].Mcps.Keys);
        Assert.Equal(RemotePattern.CmdUnsafeReason(RemoteField.Url), state.SourceBinding("Risky")?.Excluded["bad"]);
        // A connector this platform cannot carry is not a connector it is missing.
        Assert.Empty(state.PendingUpdates);
        Assert.Equal(RemotePattern.CmdUnsafeReason(RemoteField.Url),
            CollectionsLocalCache.Load(state.Service.Paths.CollectionsCachePath).Synced["Risky"].Excluded["bad"]);
    }

    [Fact]
    public void ACorruptSidecarAtLaunchKeepsTheCacheBindings()
    {
        using var h = new AppStateHarness();
        SeedStore(h, "Team");
        Seed(h, null, File_(("Team", Synced("team.json"))), Cache([new("Team", Bound("/shared/team.json"))]));
        var sidecar = Path.Combine(h.StoreDir, CollectionsFile.FileName);
        TempDir.Touch(sidecar, "{not json");

        using var state = h.Create();
        // A good cache outlives a sidecar a sync tool is halfway through writing.
        Assert.Equal("/shared/team.json", state.SourceBinding("Team")?.Path);
        // With nothing readable to vouch for it, nothing is synced.
        Assert.Equal(CollectionKind.Local, state.KindOf("Team"));

        File_(("Team", Synced("team.json"))).Save(sidecar);
        state.Reload();
        Assert.Equal(CollectionKind.Synced, state.KindOf("Team"));
        Assert.Equal("/shared/team.json", state.SourceBinding("Team")?.Path);
    }

    // MARK: publishing and export

    /// <summary>A folder to publish into, created so the guards read a real directory rather than a path.</summary>
    private static string PublishFolder(AppStateHarness h, string name = "pub")
    {
        var path = h.Dir.File(name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A local connector with two environment values and an absolute path argument — what
    /// the Publish sheet's rows are built from, and what the intent strips or shares.</summary>
    private static JsonValue LedgerConfig() => JsonValue.Object(new Dictionary<string, JsonValue>(StringComparer.Ordinal)
    {
        ["command"] = JsonValue.String("node"),
        ["args"] = JsonValue.Array([JsonValue.String("/Users/d/ledger/dist/index.js")]),
        ["env"] = JsonValue.Object(new Dictionary<string, JsonValue>(StringComparer.Ordinal)
        {
            ["A"] = JsonValue.String("sk-live-secret"),
            ["B"] = JsonValue.String("us"),
        }),
    });

    private static McpEntry NewConnector(string name) =>
        new(true, JsonValue.Object(new Dictionary<string, JsonValue>(StringComparer.Ordinal)
        {
            ["command"] = JsonValue.String(name),
        }));

    [Fact]
    public void StartPublishingWritesTheDocumentAndRepublishesOnlyWhenTheContentChanges()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var file = Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json");
        var first = File.ReadAllBytes(file);
        Assert.True(state.IsPublished(state.ActiveCollection));
        Assert.Equal(folder, state.CollectionsCache.Published[state.ActiveCollection].Folder);

        // The clock moves between the two saves. What decides a rewrite is what the document says,
        // never when it was written, or every toggle would publish.
        h.Now = h.Now.AddSeconds(60);
        state.SetEnabled("aws-mcp", false);
        Assert.Equal(first, File.ReadAllBytes(file));   // toggles never change the document

        Assert.Null(state.Upsert("new", NewConnector("x"), null));
        var second = File.ReadAllBytes(file);
        Assert.NotEqual(first, second);
        var document = CollectionDocument.Decode(second);
        Assert.NotNull(document.Origin);
        Assert.Equal(state.ActiveCollection, document.Name);
        Assert.Equal(IsoTimestamp.String(h.Now), document.Exported);
        Assert.Contains("new", document.Connectors.Keys);   // a disabled connector still travels
        Assert.Null(state.PublishError);
    }

    [Fact]
    public void ASyncedCollectionCannotBePublished()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, null));
        var folder = PublishFolder(h);

        // A synced collection has an author elsewhere, and nothing in the window offers Publish
        // for one — the toolbar swaps it for Refresh and Make Local Copy. The refusal is the same
        // silence LocateSource gives a collection that is not synced.
        Assert.Null(state.StartPublishing("Data team", folder, PublishIntent.None));
        Assert.False(state.IsPublished("Data team"));
        Assert.Null(state.CollectionsFile.Collections["Data team"].Publish);
        Assert.False(state.CollectionsCache.Published.ContainsKey("Data team"));
        Assert.Empty(Directory.GetFileSystemEntries(folder));   // nothing was written for it
        // And it is still a synced collection.
        Assert.Equal(CollectionKind.Synced, state.KindOf("Data team"));
        Assert.Null(state.PublishError);
    }

    [Fact]
    public void PublishGuards()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Equal(AppState.PublishIntoStoreError,
            state.StartPublishing(state.ActiveCollection, h.StoreDir, PublishIntent.None));
        // A folder inside the backups folder is the backups folder.
        Assert.Equal(AppState.PublishIntoStoreError,
            state.StartPublishing(state.ActiveCollection, Path.Combine(h.BackupsDir, "2026"), PublishIntent.None));

        var folder = PublishFolder(h);
        var fileName = Slug.Make(state.ActiveCollection) + ".json";
        WriteDocument(CollectionDocumentSamples.DataTeam, Path.Combine(folder, fileName));
        Assert.Equal(AppState.PublishSlugTakenError(fileName),
            state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        Assert.False(state.IsPublished(state.ActiveCollection));   // a refused publish records nothing
    }

    [Fact]
    public void PublishingAgainIntoTheFolderItAlreadyOwnsIsAllowed()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        // The document sitting there carries this collection's own origin, which is the whole
        // point of the guard: only somebody else's file is in the way.
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
    }

    [Fact]
    public void PublishingIsRefusedWhileTheCollectionFileCannotBeRead()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishFolder(h);
        TempDir.Touch(Path.Combine(h.StoreDir, CollectionsFile.FileName), "{half");
        state.Reload();
        // A publish record that cannot be saved must not be created.
        Assert.Equal(AppState.CollectionsNotSavedNote,
            state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        Assert.False(File.Exists(Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json")));
        Assert.False(state.IsPublished(state.ActiveCollection));
    }

    [Fact]
    public void APublishFailureRaisesTheBannerAndClearsOnSuccess()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var file = Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json");
        Assert.True(File.Exists(file));

        // A file where the folder belongs fails the write on both platforms and needs no
        // permission games. Deleting the folder would not: the writer creates it again.
        Directory.Delete(folder, recursive: true);
        TempDir.Touch(folder, "not a folder");
        Assert.Null(state.Upsert("new", NewConnector("x"), null));
        Assert.Equal(state.ActiveCollection, state.PublishError?.Collection);
        var banner = Assert.IsType<CollectionBanner.PublishFailed>(state.CollectionBanner);
        Assert.Equal(state.ActiveCollection, banner.Collection);

        File.Delete(folder);
        Directory.CreateDirectory(folder);
        Assert.Null(state.Upsert("another", NewConnector("y"), null));
        Assert.Null(state.PublishError);   // a write that succeeds clears the mark
        var document = CollectionDocument.Decode(File.ReadAllBytes(file));
        // The change the failed write held back still lands.
        Assert.Contains("new", document.Connectors.Keys);
        Assert.Contains("another", document.Connectors.Keys);
    }

    [Fact]
    public void ExportStripsSecretsAndPublishedDocumentsStripThemToo()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.Upsert("ledger", new McpEntry(true, LedgerConfig()), null));
        var intent = new PublishIntent(
            [new("ledger", new HashSet<string>(StringComparer.Ordinal) { "B" })],
            [],
            [new("ledger", new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "your ledger token" })]);

        var exported = h.Dir.File("out/ledger.json");
        Assert.Null(state.WriteExport(state.ActiveCollection, intent, exported));
        var exportedBytes = File.ReadAllBytes(exported);
        var document = CollectionDocument.Decode(exportedBytes);
        // An unshared value travels as its hint.
        Assert.Equal(new CollectionDocument.EnvValue.Hint("your ledger token"), document.Connectors["ledger"].Env["A"]);
        Assert.Equal(new CollectionDocument.EnvValue.Value("us"), document.Connectors["ledger"].Env["B"]);
        Assert.False(JsonText.Contains(exportedBytes, "sk-live-secret"));
        Assert.Null(document.Origin);   // nothing published, nothing to identify it by

        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, intent));
        var published = File.ReadAllBytes(Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json"));
        Assert.False(JsonText.Contains(published, "sk-live-secret"));
        Assert.Equal(new CollectionDocument.EnvValue.Value("us"),
            CollectionDocument.Decode(published).Connectors["ledger"].Env["B"]);
    }

    [Fact]
    public void ChangingWhatIsSharedRewritesTheDocument()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.Upsert("ledger", new McpEntry(true, LedgerConfig()), null));
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var file = Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json");
        Assert.Equal(new CollectionDocument.EnvValue.Hint(null),
            CollectionDocument.Decode(File.ReadAllBytes(file)).Connectors["ledger"].Env["B"]);

        var shared = new PublishIntent([new("ledger", new HashSet<string>(StringComparer.Ordinal) { "B" })], [], []);
        Assert.Null(state.UpdatePublishIntent(state.ActiveCollection, shared));
        Assert.Equal(new CollectionDocument.EnvValue.Value("us"),
            CollectionDocument.Decode(File.ReadAllBytes(file)).Connectors["ledger"].Env["B"]);
        // What was ticked is remembered for the next write.
        Assert.Equal(shared, state.CollectionsFile.Collections[state.ActiveCollection].Publish?.Intent);
    }

    [Fact]
    public void StopPublishingDropsTheRecordAndCanDeleteTheFile()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishFolder(h);
        var fileName = Slug.Make(state.ActiveCollection) + ".json";
        var file = Path.Combine(folder, fileName);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));

        state.StopPublishing(state.ActiveCollection, deleteFile: false);
        Assert.False(state.IsPublished(state.ActiveCollection));
        Assert.Empty(state.CollectionsCache.Published);
        var left = File.ReadAllBytes(file);
        Assert.Null(state.Upsert("new", NewConnector("x"), null));
        Assert.Equal(left, File.ReadAllBytes(file));   // nothing is published once it has stopped

        // The collection no longer holds the origin that vouched for the document left behind, so
        // publishing here again is refused exactly as somebody else's file would be. That is why
        // Stop Publishing offers to delete it.
        Assert.Equal(AppState.PublishSlugTakenError(fileName),
            state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));

        var second = PublishFolder(h, "pub2");
        Assert.Null(state.StartPublishing(state.ActiveCollection, second, PublishIntent.None));
        state.StopPublishing(state.ActiveCollection, deleteFile: true);
        Assert.False(File.Exists(Path.Combine(second, fileName)));
        Assert.True(File.Exists(file), "only the folder it was publishing to is touched");
    }

    [Fact]
    public void AChangeThatArrivesFromAnotherMachineIsPublishedOnTheNextLoad()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var file = Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json");

        // The author's other machine added a connector to the shared master list.
        var store = h.StoreOnDisk();
        store.Collections[store.ActiveCollection].Mcps["elsewhere"] = NewConnector("z");
        MasterStoreIO.Save(store, h.MasterStorePath);
        state.Reload(ReloadTrigger.ExternalStoreAdoption);
        // The publishing machine carries another machine's change to the team.
        Assert.Contains("elsewhere", CollectionDocument.Decode(File.ReadAllBytes(file)).Connectors.Keys);
    }

    [Fact]
    public void SubscribingToWhatThisMachinePublishesIsRefused()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var file = Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json");
        Assert.Equal(AppState.OwnCollectionError, state.Subscribe(file, "Copy"));
        Assert.Equal(["Default"], state.CollectionNames);
    }

    // MARK: a marked path never leaves as written

    private const string MarkedPath = "/Users/d/ledger/dist/index.js";

    private static JsonValue NodeWith(params string[] args) => JsonValue.Object(
        ("command", JsonValue.String("node")),
        ("args", JsonValue.Array(args.Select(JsonValue.String))));

    private static string[] ArgsOf(JsonValue config) =>
        config.ValueAt(new JsonPointer(["args"]))!.ArrayItems.Select(a => a.StringValue).ToArray();

    private static JsonPointer ArgPointer(int index) =>
        new(["args", index.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

    /// <summary>
    /// The active collection publishing <c>ledger</c> with its path argument marked, the way the
    /// Publish dialog records it: the pointer and the path it was made on. Returns the document's path.
    /// </summary>
    private static string PublishMarkedLedger(AppStateHarness h, AppState state, params string[] args)
    {
        var index = Array.IndexOf(args, MarkedPath);
        Assert.True(index >= 0);
        Assert.Null(state.Upsert("ledger", new McpEntry(NodeWith(args)), null));
        var intent = new PublishIntent(
            [],
            [new("ledger", new Dictionary<JsonPointer, PublishIntent.PathMark>
            {
                [ArgPointer(index)] = new("server_path", "your ledger clone", MarkedPath),
            })],
            []);
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, intent));
        return Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json");
    }

    /// <summary>
    /// A change to the connector that did not come through its editor — the JSON view of another
    /// window, the author's other machine, an older app — so nothing re-keyed the marks.
    /// </summary>
    private static void RewriteLedger(AppState state, params string[] args) =>
        Assert.Null(state.Upsert("ledger", new McpEntry(NodeWith(args)), "ledger"));

    private static IReadOnlyList<string> LedgerArgs(string file) =>
        Assert.IsType<CollectionDocument.Launcher.Local>(
            CollectionDocument.Decode(File.ReadAllBytes(file)).Connectors["ledger"].Launcher).Args;

    [Fact]
    public void AMarkMovedOutsideTheEditorFollowsItsPathByValue()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var file = PublishMarkedLedger(h, state, MarkedPath, "--quiet");
        Assert.Equal(["${CC_NEEDS:server_path}", "--quiet"], LedgerArgs(file));

        RewriteLedger(state, "--quiet", MarkedPath);
        Assert.Null(state.PublishError);
        // The placeholder stays on the path, and the flag that took its place travels as written.
        Assert.Equal(["--quiet", "${CC_NEEDS:server_path}"], LedgerArgs(file));
        Assert.False(JsonText.FileContains(file, MarkedPath));
    }

    [Fact]
    public void AMarkThatLostItsPathFailsClosedUntilItIsMarkedAgain()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var file = PublishMarkedLedger(h, state, MarkedPath);
        var before = File.ReadAllBytes(file);

        // Edited outside the editor, so the record still names the old path: nothing now says which
        // argument the author meant, and the new path must not go out as written.
        const string edited = "/Users/d/ledger-v2/dist/index.js";
        RewriteLedger(state, "--quiet", edited);
        Assert.Equal(state.ActiveCollection, state.PublishError?.Collection);
        Assert.Equal(AppState.PathMarkMovedError("ledger"), state.PublishError?.Message);
        // Blocked for review, not a failed write: another folder is no answer to a moved mark.
        Assert.Equal(PublishErrorKind.BlockedForReview, state.PublishError?.Kind);
        var banner = Assert.IsType<CollectionBanner.PublishBlocked>(state.CollectionBanner);
        Assert.Equal(AppState.PathMarkMovedError("ledger"), banner.Message);
        // No file is written: the old document, placeholder and all, stays.
        Assert.Equal(before, File.ReadAllBytes(file));
        // Export… goes through the dialog, which holds the lost mark and will not write.
        var exported = h.Dir.File(Path.Combine("out", "copy.json"));
        Assert.Equal(PublishModel.UnresolvedMarkNote("ledger"), new PublishModel(state, state.ActiveCollection).Export(exported));
        Assert.False(File.Exists(exported));

        // Re-ticking in the Publish dialog records the path where it is now, and clears it.
        var dialog = new PublishModel(state, state.ActiveCollection);
        var row = dialog.PathRows.Single(r => r.Connector == "ledger" && r.Value == edited);
        Assert.False(row.Marked);   // a mark that lost its argument ticks nothing
        // The row that could be the lost path carries its name and its hint.
        Assert.Equal("server_path", row.Name);
        Assert.Equal("your ledger clone", row.Hint);
        row.Marked = true;
        Assert.Null(dialog.Publish());
        Assert.Null(state.PublishError);
        Assert.Equal(["--quiet", "${CC_NEEDS:server_path}"], LedgerArgs(file));
        Assert.False(JsonText.FileContains(file, edited));
        var marks = state.CollectionsFile.Collections[state.ActiveCollection].Publish!.Intent.PathMarks["ledger"];
        Assert.Equal(new PublishIntent.PathMark("server_path", "your ledger clone", edited), marks[ArgPointer(1)]);
        Assert.Single(marks);
    }

    [Fact]
    public void ARenamedConnectorKeepsItsMarksAndARemovedOneLeavesNone()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var file = PublishMarkedLedger(h, state, MarkedPath);
        var entry = state.Store.Collections[state.ActiveCollection].Mcps["ledger"];
        Assert.Null(state.Upsert("books", entry, "ledger"));
        Assert.Null(state.PublishError);
        var intent = state.CollectionsFile.Collections[state.ActiveCollection].Publish!.Intent;
        Assert.False(intent.PathMarks.ContainsKey("ledger"));
        Assert.Equal(MarkedPath, Assert.Single(intent.PathMarks["books"]).Value.Value);
        Assert.Equal(["server_path"], CollectionDocument.Decode(File.ReadAllBytes(file)).Connectors["books"].Needs.Keys);

        state.Remove("books");
        // A connector added later under the same name was never ticked.
        Assert.False(state.CollectionsFile.Collections[state.ActiveCollection].Publish!.Intent.PathMarks.ContainsKey("books"));
        Assert.Null(state.PublishError);
    }

    [Fact]
    public void AMarkWhoseConnectorWasRenamedElsewhereFailsClosed()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var file = PublishMarkedLedger(h, state, MarkedPath);
        var before = File.ReadAllBytes(file);

        // An older app renamed it on another machine: the master list arrives, the record does not
        // follow, and the path would otherwise travel under the new name as written.
        var store = h.StoreOnDisk();
        var mcps = store.Collections[store.ActiveCollection].Mcps;
        mcps["books"] = mcps["ledger"];
        mcps.Remove("ledger");
        MasterStoreIO.Save(store, h.MasterStorePath);
        state.Reload(ReloadTrigger.ExternalStoreAdoption);
        Assert.Equal(AppState.PathMarkMovedError("ledger"), state.PublishError?.Message);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    // MARK: this machine's list of marked paths

    /// <summary>
    /// The record as the author's other machine rewrote it, landing here before its master list
    /// does: what Remove or a row deletion there leaves in the sidecar.
    /// </summary>
    private static void SidecarLandsFirst(AppStateHarness h, AppState state, Func<PublishIntent, PublishIntent> edit)
    {
        var entry = state.CollectionsFile.Collections[state.ActiveCollection];
        var record = entry.Publish!;
        var rewritten = new CollectionsFile.Entry(entry.Kind, entry.FileName, entry.RelativeToStore, entry.Origin, entry.Needs,
            new CollectionsFile.PublishRecord(record.Slug, record.Origin, edit(record.Intent)), entry.Provenance);
        var file = new CollectionsFile(state.CollectionsFile.Collections
            .Select(p => p.Key == state.ActiveCollection ? new KeyValuePair<string, CollectionsFile.Entry>(p.Key, rewritten) : p));
        file.Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        state.Reload();   // a flyout open between the two files
    }

    private static IReadOnlySet<string>? MarkedValues(AppState state) =>
        state.CollectionsCache.Published.GetValueOrDefault(state.ActiveCollection)?.MarkedValues;

    [Fact]
    public void ASidecarThatDropsAMarkBeforeItsRowIsGoneCannotPublishThePath()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var file = PublishMarkedLedger(h, state, MarkedPath, "--quiet");
        // What this write replaced with a placeholder.
        Assert.Equal([MarkedPath], MarkedValues(state)!);
        var before = File.ReadAllBytes(file);

        // The other machine deleted the marked row: its sidecar arrives first, without the mark.
        SidecarLandsFirst(h, state, intent => intent.ReplacingPathMarks("ledger", new Dictionary<JsonPointer, PublishIntent.PathMark>()));
        Assert.Equal(AppState.PathMarkMovedError("ledger"), state.PublishError?.Message);
        // The path is still in the master list here, so nothing is written.
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.False(JsonText.FileContains(file, MarkedPath));

        // Its master list follows, without the row: nothing left to keep back.
        var store = h.StoreOnDisk();
        store.Collections[store.ActiveCollection].Mcps["ledger"] = new McpEntry(NodeWith("--quiet"));
        MasterStoreIO.Save(store, h.MasterStorePath);
        state.Reload(ReloadTrigger.ExternalStoreAdoption);
        Assert.Null(state.PublishError);
        Assert.Equal(["--quiet"], LedgerArgs(file));
        // A publish nobody reviewed never forgets a path.
        Assert.Equal([MarkedPath], MarkedValues(state)!);
    }

    [Fact]
    public void ASidecarThatDropsAConnectorBeforeItIsGoneCannotPublishItsPath()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var file = PublishMarkedLedger(h, state, MarkedPath);
        var before = File.ReadAllBytes(file);
        SidecarLandsFirst(h, state, intent => intent.MovingConnector("ledger", null));
        Assert.Equal(AppState.PathMarkMovedError("ledger"), state.PublishError?.Message);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.False(JsonText.FileContains(file, MarkedPath));
    }

    /// <summary>
    /// Removed on the other machine while this one was off: at launch Claude's config brings the
    /// connector back, and the sidecar no longer marks it. The list outlives the launch.
    /// </summary>
    [Fact]
    public void AConnectorBroughtBackAtLaunchWithoutItsMarkIsNotPublished()
    {
        using var h = new AppStateHarness();
        string file;
        CollectionsFile sidecar;
        using (var first = h.Create())
        {
            file = PublishMarkedLedger(h, first, MarkedPath);
            first.Apply();
            // Claude runs it, which is how it comes back.
            Assert.True(h.ClaudeServers().ContainsKey("ledger"));
            var entry = first.CollectionsFile.Collections[first.ActiveCollection];
            var record = entry.Publish!;
            sidecar = new CollectionsFile(first.CollectionsFile.Collections.Select(p => p.Key == first.ActiveCollection
                ? new KeyValuePair<string, CollectionsFile.Entry>(p.Key, new CollectionsFile.Entry(
                    entry.Kind, entry.FileName, entry.RelativeToStore, entry.Origin, entry.Needs,
                    new CollectionsFile.PublishRecord(record.Slug, record.Origin, record.Intent.MovingConnector("ledger", null)),
                    entry.Provenance))
                : p));
        }
        var before = File.ReadAllBytes(file);
        var store = h.StoreOnDisk();
        store.Collections[store.ActiveCollection].Mcps.Remove("ledger");
        MasterStoreIO.Save(store, h.MasterStorePath);
        sidecar.Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));

        using var relaunched = h.Create();
        // Claude's config brought it back.
        Assert.True(relaunched.Store.Collections[relaunched.ActiveCollection].Mcps.ContainsKey("ledger"));
        Assert.Equal(AppState.PathMarkMovedError("ledger"), relaunched.PublishError?.Message);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public void AMarkedPathInsideAnotherStringIsNotPublished()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var file = PublishMarkedLedger(h, state, MarkedPath);
        var before = File.ReadAllBytes(file);
        // Still marked where it was, and now also inside a flag nothing marks.
        RewriteLedger(state, MarkedPath, $"--config={MarkedPath}.config");
        Assert.Equal(AppState.PathMarkMovedError("ledger"), state.PublishError?.Message);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public void OnlyTheSheetsPublishTakesAPathOffTheList()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var file = PublishMarkedLedger(h, state, MarkedPath);
        var dialog = new PublishModel(state, state.ActiveCollection);
        var row = dialog.PathRows.Single(r => r.Value == MarkedPath);
        Assert.True(row.Marked);
        // The author unticks it, reads the preview, and presses Publish: that is the reviewed answer.
        row.Marked = false;
        Assert.True(JsonText.Contains(dialog.Preview, MarkedPath));   // the preview shows the path as it will travel
        Assert.Null(dialog.Publish());
        Assert.Null(state.PublishError);
        Assert.Empty(MarkedValues(state)!);
        Assert.Equal([MarkedPath], LedgerArgs(file));
    }

    [Fact]
    public void TheSheetsExportRefusesACopyOfAPathItMarks()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.Upsert("ledger", new McpEntry(NodeWith(MarkedPath, $"--config={MarkedPath}.config")), null));
        var dialog = new PublishModel(state, state.ActiveCollection);
        dialog.PathRows.Single(r => r.Value == MarkedPath).Marked = true;
        var output = h.Dir.File(Path.Combine("away", "copy.json"));
        Assert.Equal(AppState.PathMarkMovedError("ledger"), dialog.Export(output));
        Assert.False(File.Exists(output));
        // The preview says why rather than show it.
        Assert.Equal(AppState.PathMarkMovedError("ledger"), dialog.Preview);
    }

    /// <summary>
    /// C#-only: the Mac never starts a connector through cmd.exe. A folder with a space or a cmd
    /// metacharacter, expanded into a cmd /c connector's arguments, would split or run as a command.
    /// </summary>
    [Fact]
    public void ACmdConnectorWarnsWhenItsCollectionFolderIsUnsafeForCmd()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var viaCmd = JsonValue.Object(
            ("command", JsonValue.String("cmd")),
            ("args", JsonValue.Array([JsonValue.String("/c"), JsonValue.String("node"), JsonValue.String($"{Placeholder.DirectoryToken}/srv.js")])));
        Assert.Null(state.Upsert("viaCmd", new McpEntry(viaCmd), null));
        Assert.Null(state.Upsert("direct", new McpEntry(NodeWith($"{Placeholder.DirectoryToken}/srv.js")), null));
        Assert.Null(state.StartPublishing(state.ActiveCollection, PublishFolder(h, "my share"), PublishIntent.None));
        Assert.Equal(AppState.CollectionDirCmdUnsafeCaution, state.ConnectorCaution("viaCmd", state.ActiveCollection));
        // Started directly, the folder reaches the program as one argument.
        Assert.Null(state.ConnectorCaution("direct", state.ActiveCollection));

        Assert.Null(state.StartPublishing(state.ActiveCollection, PublishFolder(h, "share"), PublishIntent.None));
        Assert.Null(state.ConnectorCaution("viaCmd", state.ActiveCollection));
    }

    /// <summary>
    /// Renamed on the other machine while this one was off, the rename carrying the marks along; at
    /// launch Claude's config brings the old name back from this machine's own last apply.
    /// </summary>
    [Fact]
    public void AConnectorRenamedElsewhereWhileOffIsNotPublishedUnderItsOldName()
    {
        using var h = new AppStateHarness();
        string file;
        CollectionsFile sidecar;
        using (var first = h.Create())
        {
            file = PublishMarkedLedger(h, first, MarkedPath);
            first.Apply();
            var entry = first.CollectionsFile.Collections[first.ActiveCollection];
            var record = entry.Publish!;
            sidecar = new CollectionsFile(first.CollectionsFile.Collections.Select(p => p.Key == first.ActiveCollection
                ? new KeyValuePair<string, CollectionsFile.Entry>(p.Key, new CollectionsFile.Entry(
                    entry.Kind, entry.FileName, entry.RelativeToStore, entry.Origin, entry.Needs,
                    new CollectionsFile.PublishRecord(record.Slug, record.Origin, record.Intent.MovingConnector("ledger", "books")),
                    entry.Provenance))
                : p));
        }
        var before = File.ReadAllBytes(file);
        var store = h.StoreOnDisk();
        var mcps = store.Collections[store.ActiveCollection].Mcps;
        mcps["books"] = mcps["ledger"];
        mcps.Remove("ledger");
        MasterStoreIO.Save(store, h.MasterStorePath);
        sidecar.Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));

        using var relaunched = h.Create();
        // The old name came back.
        Assert.True(relaunched.Store.Collections[relaunched.ActiveCollection].Mcps.ContainsKey("ledger"));
        Assert.Equal(AppState.PathMarkMovedError("ledger"), relaunched.PublishError?.Message);
        Assert.Equal(PublishErrorKind.BlockedForReview, relaunched.PublishError?.Kind);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public void AMarkedPathCarriedInAnyOtherFieldIsNotPublished()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var file = PublishMarkedLedger(h, state, MarkedPath);
        var before = File.ReadAllBytes(file);
        var carriers = new (string Name, JsonValue Config)[]
        {
            ("in the command", JsonValue.Object(("command", JsonValue.String(MarkedPath + "/bin/start")), ("args", JsonValue.Array([])))),
            ("in a remote's arguments", RemotePattern.Encode(new RemoteConfig("https://mcp.example.com/", RemoteAuth.Auto,
                RemoteLaunchStyle.CmdNpx, extraArgs: ["--config", MarkedPath], package: "mcp-remote"))),
            ("in an additional field", JsonValue.Object(
                ("command", JsonValue.String("node")), ("args", JsonValue.Array([JsonValue.String("x.js")])),
                ("cwd", JsonValue.String(MarkedPath)))),
        };
        foreach (var (name, config) in carriers)
        {
            Assert.Null(state.Upsert(name, new McpEntry(config), null));
            Assert.Equal(AppState.PathMarkMovedError(name), state.PublishError?.Message);
            Assert.Equal(PublishErrorKind.BlockedForReview, state.PublishError?.Kind);
            Assert.Equal(before, File.ReadAllBytes(file));
            state.Remove(name);
            // With it gone there is nothing left to keep back.
            Assert.Null(state.PublishError);
        }
    }

    // MARK: the directory token on the publishing machine

    [Fact]
    public void ThePublishFolderStandsForTheDirectoryTokenOnlyWhileThisMachinePublishes()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var token = $"{Placeholder.DirectoryToken}/tools/srv.js";
        Assert.Null(state.Upsert("x", new McpEntry(true, NodeWith(token)), null));
        state.Apply();
        Assert.Equal(AppState.UnpublishedDirectoryCaution, state.ConnectorCaution("x", state.ActiveCollection));
        Assert.Equal([token], ArgsOf(h.ClaudeServers()["x"]));   // no folder is guessed at

        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var published = state.CollectionsCache.Published[state.ActiveCollection].Folder;
        // Claude runs the tool shipped beside the document, from the moment publishing starts.
        Assert.Equal([published + "/tools/srv.js"], ArgsOf(h.ClaudeServers()["x"]));
        Assert.Null(state.ConnectorCaution("x", state.ActiveCollection));
        // The store keeps the token.
        Assert.Equal([token], ArgsOf(state.Store.Collections[state.ActiveCollection].Mcps["x"].Config));
        var document = Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json");
        // Each subscriber resolves it against their own copy, and the author's folder never travels.
        Assert.True(JsonText.FileContains(document, token));
        Assert.False(JsonText.FileContains(document, published));
        state.Reload();
        // What was written is what a reload renders, so nothing is regenerated.
        Assert.Equal([published + "/tools/srv.js"], ArgsOf(h.ClaudeServers()["x"]));

        state.StopPublishing(state.ActiveCollection, deleteFile: false);
        Assert.Equal(AppState.UnpublishedDirectoryCaution, state.ConnectorCaution("x", state.ActiveCollection));
        Assert.Equal([token], ArgsOf(h.ClaudeServers()["x"]));
    }

    [Fact]
    public void ACollectionPublishedFromAnotherMachineHasNoFolderHere()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.Upsert("x", new McpEntry(NodeWith($"{Placeholder.DirectoryToken}/srv.js")), null));
        Seed(h, state, File_((state.ActiveCollection, Published("default"))));
        Assert.True(state.IsPublished(state.ActiveCollection));
        Assert.Null(state.CollectionDirectory(state.ActiveCollection));
        Assert.Equal(AppState.UnpublishedDirectoryCaution, state.ConnectorCaution("x", state.ActiveCollection));
    }

    /// <summary>
    /// A published collection with an enabled connector that runs a tool from its folder, applied, so
    /// Claude's file holds the folder where the store holds the token. Returns the document and the
    /// folder as this machine records it.
    /// </summary>
    private static (string Document, string Folder) PublishTokenConnector(AppStateHarness h, AppState state)
    {
        Assert.Null(state.Upsert("x", new McpEntry(true, NodeWith($"{Placeholder.DirectoryToken}/tools/x.js")), null));
        state.Apply();
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var bound = state.CollectionsCache.Published[state.ActiveCollection].Folder;
        Assert.Equal([bound + "/tools/x.js"], ArgsOf(h.ClaudeServers()["x"]));
        return (Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json"), bound);
    }

    [Fact]
    public void RestoringClaudesConfigKeepsTheTokenInAPublishedCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var (document, folder) = PublishTokenConnector(h, state);
        // Every apply backs Claude's file up first, so any backup taken now holds the folder.
        var backup = h.Dir.File("backup.json");
        File.Copy(h.ClaudeConfigPath, backup);

        state.RestoreClaudeConfig(backup);
        // The store keeps its token.
        Assert.Equal([$"{Placeholder.DirectoryToken}/tools/x.js"], ArgsOf(state.Store.Collections[state.ActiveCollection].Mcps["x"].Config));
        Assert.True(JsonText.FileContains(document, Placeholder.DirectoryToken));
        Assert.False(JsonText.FileContains(document, folder));

        Assert.Null(state.PublishError);

        // Removed since the backup was taken, it comes back as the backup has it: there is no store
        // copy to keep, and rewriting what came in could rewrite a genuine edit. The folder is then
        // kept back from the document, for the author to answer in Publish….
        var before = File.ReadAllBytes(document);
        state.Remove("x");
        Assert.NotEqual(before, File.ReadAllBytes(document));
        var withoutX = File.ReadAllBytes(document);
        state.RestoreClaudeConfig(backup);
        Assert.Equal([folder + "/tools/x.js"], ArgsOf(state.Store.Collections[state.ActiveCollection].Mcps["x"].Config));
        Assert.Equal(AppState.PublishFolderCarriedError("x"), state.PublishError?.Message);
        Assert.Equal(PublishErrorKind.BlockedForReview, state.PublishError?.Kind);
        Assert.Equal(withoutX, File.ReadAllBytes(document));
        Assert.False(JsonText.FileContains(document, folder));
    }

    /// <summary>
    /// Published from another machine: the record is in the sidecar, but this machine has no binding
    /// and so no folder to recognise. What the backup holds is adopted as written.
    /// </summary>
    [Fact]
    public void ARestoreKeepsNothingForACollectionPublishedFromAnotherMachine()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.Upsert("x", new McpEntry(true, NodeWith($"{Placeholder.DirectoryToken}/tools/x.js")), null));
        Seed(h, state, File_((state.ActiveCollection, Published("default"))));
        Assert.True(state.IsPublished(state.ActiveCollection));
        var backup = h.Dir.File("backup.json");
        File.WriteAllText(backup, """{"mcpServers": {"x": {"command": "node", "args": ["/Users/d/elsewhere/tools/x.js"]}}}""");
        state.RestoreClaudeConfig(backup);
        Assert.Equal(["/Users/d/elsewhere/tools/x.js"], ArgsOf(state.Store.Collections[state.ActiveCollection].Mcps["x"].Config));
    }

    /// <summary>
    /// Removed on the other machine while this one was off: at launch Claude's config brings the
    /// connector back with this machine's folder in it. The store has no copy to keep, so it arrives
    /// as written, and the folder is kept back from the document.
    /// </summary>
    [Fact]
    public void ALaunchIngestThatBringsThePublishFolderBackIsNotPublished()
    {
        using var h = new AppStateHarness();
        string document;
        string folder;
        using (var first = h.Create())
        {
            (document, folder) = PublishTokenConnector(h, first);
        }
        var store = h.StoreOnDisk();
        store.Collections[store.ActiveCollection].Mcps.Remove("x");
        MasterStoreIO.Save(store, h.MasterStorePath);

        var before = File.ReadAllBytes(document);
        using var relaunched = h.Create();
        // Ingested as Claude's file has it.
        Assert.Equal([folder + "/tools/x.js"], ArgsOf(relaunched.Store.Collections[relaunched.ActiveCollection].Mcps["x"].Config));
        Assert.Equal(AppState.PublishFolderCarriedError("x"), relaunched.PublishError?.Message);
        Assert.Equal(PublishErrorKind.BlockedForReview, relaunched.PublishError?.Kind);
        Assert.Equal(before, File.ReadAllBytes(document));
        Assert.False(JsonText.FileContains(document, folder));
    }

    [Fact]
    public void ThisMachinesPublishFolderWrittenOutIsNotPublished()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var bound = state.CollectionsCache.Published[state.ActiveCollection].Folder;
        var file = Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json");
        var before = File.ReadAllBytes(file);

        // A sibling folder that merely begins with its name is somebody else's path, and travels.
        Assert.Null(state.Upsert("sibling", new McpEntry(NodeWith(bound + "-tools/x.js")), null));
        Assert.Null(state.PublishError);
        var withSibling = File.ReadAllBytes(file);
        Assert.NotEqual(before, withSibling);

        Assert.Null(state.Upsert("typed", new McpEntry(NodeWith(bound + "/tools/x.js")), null));
        Assert.Equal(AppState.PublishFolderCarriedError("typed"), state.PublishError?.Message);
        // Answered in the Publish dialog, not another folder.
        Assert.Equal(PublishErrorKind.BlockedForReview, state.PublishError?.Kind);
        Assert.Equal(withSibling, File.ReadAllBytes(file));

        var dialog = new PublishModel(state, state.ActiveCollection);
        var output = h.Dir.File(Path.Combine("away", "copy.json"));
        Assert.Equal(AppState.PublishFolderCarriedError("typed"), dialog.Export(output));
        Assert.False(File.Exists(output));
        Assert.Equal(AppState.PublishFolderCarriedError("typed"), dialog.Preview);
    }

    // MARK: import as copies

    /// <summary>The design's sample plus a connector whose path is written against the document's
    /// own folder, so one import exercises markers, shared values and the directory token at once.</summary>
    private static CollectionDocument ImportableDocument()
    {
        var sample = CollectionDocumentSamples.DataTeam;
        var connectors = new Dictionary<string, CollectionDocument.Connector>(sample.Connectors, StringComparer.Ordinal)
        {
            ["ledger"] = new(new CollectionDocument.Launcher.Local("node", ["${COLLECTION_DIR}/dist/index.js"], CollectionPlatforms.Current)),
        };
        return new CollectionDocument(sample.Name, sample.Author, sample.Origin, sample.Exported, connectors);
    }

    private static IReadOnlyDictionary<string, ImportChoice> Choices(params (string Name, ImportChoice Choice)[] choices) =>
        choices.ToDictionary(c => c.Name, c => c.Choice, StringComparer.Ordinal);

    [Fact]
    public void ImportCopiesArriveDisabledWithProvenanceAndExpandedToken()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = Path.Combine(h.Dir.File("shared"), "data-team.json");
        WriteDocument(ImportableDocument(), path);

        Assert.Null(state.ImportCopies(path, "Default", Choices(), "2026-09-21"));
        var mcps = state.Store.Collections["Default"].Mcps;
        Assert.Equal(["aws-mcp", "dbt", "github", "ledger", "notion", "scoutbook", "service-now"],
            AppStateHarness.Keys(mcps.Keys));
        // An imported copy arrives off.
        foreach (var name in new[] { "dbt", "github", "ledger", "notion" })
        {
            Assert.False(mcps[name].Enabled);
        }
        // The directory token is expanded once, against the folder the document sits in.
        Assert.Equal(JsonValue.String(Path.GetDirectoryName(path) + "/dist/index.js"),
            mcps["ledger"].Config.ValueAt(JsonPointer.Parse("/args/0")!));
        Assert.Equal(JsonValue.String("${CC_NEEDS:DBT_TOKEN}"), mcps["dbt"].Config.ValueAt(JsonPointer.Parse("/env/DBT_TOKEN")!));
        Assert.Equal(JsonValue.String("us"), mcps["dbt"].Config.ValueAt(JsonPointer.Parse("/env/DBT_REGION")!));
        // Copies leave no link to the file.
        Assert.Equal(CollectionKind.Local, state.KindOf("Default"));
        Assert.Empty(state.CollectionsCache.Synced);
        Assert.Empty(state.PendingUpdates);
        Assert.Equal(new CollectionsFile.Provenance("Data team", "Acme Data Platform", "2026-09-21"),
            state.CollectionsFile.Collections["Default"].Provenance["dbt"]);
        Assert.Equal(AppState.NeedsValueCaution("DBT_TOKEN"), state.ConnectorCaution("dbt", "Default"));
        // Nothing that arrives off reaches Claude.
        Assert.Equal(["aws-mcp", "scoutbook", "service-now"], AppStateHarness.Keys(h.ClaudeServers().Keys));
        // A collection that does not exist is a no-op, as switching to one is.
        Assert.Null(state.ImportCopies(path, "Nowhere", Choices(), "2026-09-21"));
    }

    [Fact]
    public void ReplaceKeepsAFilledValueAndKeepBothSuffixes()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = Path.Combine(h.Dir.File("shared"), "data-team.json");
        WriteDocument(ImportableDocument(), path);
        Assert.Null(state.ImportCopies(path, "Default", Choices(), "2026-09-21"));

        // The user fills the token and turns dbt on.
        var dbt = state.Store.Collections["Default"].Mcps["dbt"];
        Assert.Null(state.Upsert("dbt", dbt with
        {
            Enabled = true,
            Config = dbt.Config.Replacing(JsonPointer.Parse("/env/DBT_TOKEN")!, JsonValue.String("tok"))!,
        }, "dbt"));

        // The author ships a new dbt, and the same document is imported again.
        var next = ImportableDocument();
        var connectors = new Dictionary<string, CollectionDocument.Connector>(next.Connectors, StringComparer.Ordinal);
        var authored = connectors["dbt"];
        connectors["dbt"] = new CollectionDocument.Connector(
            new CollectionDocument.Launcher.Local("npx", ["-y", "@dbt/mcp@2"], CollectionPlatforms.Current),
            authored.Env, authored.Needs, authored.Additional);
        WriteDocument(new CollectionDocument(next.Name, next.Author, next.Origin, next.Exported, connectors), path);

        Assert.Null(state.ImportCopies(path, "Default", Choices(
            ("dbt", ImportChoice.Replace), ("github", ImportChoice.KeepBoth),
            ("notion", ImportChoice.Skip), ("ledger", ImportChoice.Skip)), "2026-09-22"));
        var mcps = state.Store.Collections["Default"].Mcps;
        Assert.Equal(JsonValue.String("@dbt/mcp@2"), mcps["dbt"].Config.ValueAt(JsonPointer.Parse("/args/1")!));
        // Replace keeps what the user filled in, and a connector that was on stays on.
        Assert.Equal(JsonValue.String("tok"), mcps["dbt"].Config.ValueAt(JsonPointer.Parse("/env/DBT_TOKEN")!));
        Assert.True(mcps["dbt"].Enabled);
        // Keep both lands beside what is already there; Skip leaves it alone.
        Assert.True(mcps.ContainsKey("github 2"));
        Assert.False(mcps["github 2"].Enabled);
        Assert.Equal(["notion"], AppStateHarness.Keys(mcps.Keys.Where(k => k.StartsWith("notion", StringComparison.Ordinal))));
        Assert.Equal("2026-09-22", state.CollectionsFile.Collections["Default"].Provenance["dbt"].Date);
        Assert.Equal("Data team", state.CollectionsFile.Collections["Default"].Provenance["github 2"].From);
        // A replaced connector that was on reaches Claude.
        Assert.Equal(JsonValue.String("@dbt/mcp@2"), h.ClaudeServers()["dbt"].ValueAt(JsonPointer.Parse("/args/1")!));

        // A third import with Keep both again numbers on from the highest suffix taken.
        Assert.Null(state.ImportCopies(path, "Default", Choices(
            ("github", ImportChoice.KeepBoth), ("dbt", ImportChoice.Skip),
            ("notion", ImportChoice.Skip), ("ledger", ImportChoice.Skip)), "2026-09-23"));
        Assert.True(state.Store.Collections["Default"].Mcps.ContainsKey("github 3"));
    }

    [Fact]
    public void MakeLocalCopyIntoALocalCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = Path.Combine(h.Dir.File("shared"), "data-team.json");
        WriteDocument(ImportableDocument(), path);
        Assert.Null(state.Subscribe(path, null));
        Assert.Null(state.CreateCollection("Personal"));

        Assert.Null(state.MakeLocalCopy(["dbt", "ledger"], "Data team", "Personal"));
        var mcps = state.Store.Collections["Personal"].Mcps;
        Assert.False(mcps["dbt"].Enabled);
        // A copy is what it was: an unfilled marker stays unfilled.
        Assert.Equal(JsonValue.String("${CC_NEEDS:DBT_TOKEN}"), mcps["dbt"].Config.ValueAt(JsonPointer.Parse("/env/DBT_TOKEN")!));
        // A local collection has no document to resolve the directory token against later.
        Assert.Equal(JsonValue.String(Path.GetDirectoryName(path) + "/dist/index.js"),
            mcps["ledger"].Config.ValueAt(JsonPointer.Parse("/args/0")!));
        Assert.Equal(new CollectionsFile.Provenance("Data team", null, IsoTimestamp.LocalDate(h.Now)),
            state.CollectionsFile.Collections["Personal"].Provenance["dbt"]);
        // The source is untouched.
        Assert.Equal(CollectionKind.Synced, state.KindOf("Data team"));
        Assert.Equal(4, state.Store.Collections["Data team"].Mcps.Count);

        Assert.Null(state.MakeLocalCopy(["dbt"], "Data team", "Personal"));
        Assert.True(state.Store.Collections["Personal"].Mcps.ContainsKey("dbt 2"));

        Assert.Equal(AppState.TargetMustBeLocalError, state.MakeLocalCopy(["dbt"], "Personal", "Data team"));
        Assert.False(state.Store.Collections["Data team"].Mcps.ContainsKey("dbt 2"));
    }

    [Fact]
    public void MakeLocalCopyOfAWholeSyncedCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = Path.Combine(h.Dir.File("shared"), "data-team.json");
        WriteDocument(ImportableDocument(), path);
        Assert.Null(state.Subscribe(path, null));

        Assert.Null(state.MakeLocalCopyOfCollection("Data team", "Data team copy"));
        Assert.Equal(CollectionKind.Local, state.KindOf("Data team copy"));
        // The copy is all off, so switching to it would empty Claude's config.
        Assert.Equal("Default", state.ActiveCollection);
        var copied = state.Store.Collections["Data team copy"].Mcps;
        Assert.Equal(["dbt", "github", "ledger", "notion"], AppStateHarness.Keys(copied.Keys));
        Assert.All(copied.Values, entry => Assert.False(entry.Enabled));
        Assert.Equal(JsonValue.String(Path.GetDirectoryName(path) + "/dist/index.js"),
            copied["ledger"].Config.ValueAt(JsonPointer.Parse("/args/0")!));
        foreach (var name in copied.Keys)
        {
            Assert.Equal("Data team", state.CollectionsFile.Collections["Data team copy"].Provenance[name].From);
        }
        // A copy follows nothing.
        Assert.False(state.CollectionsCache.Synced.ContainsKey("Data team copy"));
        Assert.Equal(CollectionKind.Synced, state.KindOf("Data team"));

        Assert.NotNull(state.MakeLocalCopyOfCollection("Data team", "Data team copy"));
        Assert.Null(state.MakeLocalCopyOfCollection("Nowhere", "Ghost"));
        Assert.DoesNotContain("Ghost", state.CollectionNames);
    }
}
