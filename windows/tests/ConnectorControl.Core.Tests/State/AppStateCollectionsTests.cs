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
}
