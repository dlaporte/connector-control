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
        Assert.Null(h.Settings.LastApplyDate);   // a copy runs what Claude already runs, so nothing is written
        // But Claude's file now holds the new collection, which the launch ingest reads.
        Assert.Equal("Work", state.CollectionsCache.LastAppliedCollection);
    }

    /// <summary>
    /// Copy to ▸ New Collection's first half: a collection holding nothing, not a copy of the
    /// active one, and not made active, since switching to it would empty Claude's config.
    /// </summary>
    [Fact]
    public void AddEmptyCollectionLeavesTheActiveCollectionAndClaudesConfigAlone()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.True(state.Store.Collections["Default"].Mcps.ContainsKey("aws-mcp"));
        var before = File.ReadAllBytes(h.ClaudeConfigPath);
        Assert.Null(state.AddEmptyCollection("  Empty  "));
        Assert.Equal(["Default", "Empty"], state.CollectionNames);
        Assert.Empty(state.Store.Collections["Empty"].Mcps);   // empty, not a copy of the active collection
        Assert.False(state.Store.Collections["Empty"].Mcps.ContainsKey("aws-mcp"));
        Assert.Equal("Default", state.ActiveCollection);
        Assert.Equal(before, File.ReadAllBytes(h.ClaudeConfigPath));   // nothing Claude runs has changed, to the byte
        state.Reload();
        Assert.Empty(state.Store.Collections["Empty"].Mcps);   // and it was saved

        // A name the store refuses is the store's own error, and nothing is added.
        Assert.Equal("A collection named “Empty” already exists.", state.AddEmptyCollection("Empty"));
        Assert.Equal(["Default", "Empty"], state.CollectionNames);
        Assert.Equal("Default", state.ActiveCollection);
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

    /// <summary>
    /// A collection Claude does not run can be renamed or deleted without Claude hearing of it:
    /// its config is not rewritten and no restart is asked for.
    /// </summary>
    [Fact]
    public void RenamingOrDeletingAnotherCollectionLeavesClaudesConfigAlone()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.AddEmptyCollection("Other"));
        h.Settings.LastApplyDate = null;
        var before = File.ReadAllBytes(h.ClaudeConfigPath);
        var backups = new BackupManager(h.BackupsDir);
        var backedUp = backups.Backups("claude_desktop_config");

        Assert.Null(state.RenameCollection("Other", "Else"));
        Assert.Null(h.Settings.LastApplyDate);   // nothing Claude runs changed, so there is nothing to restart for
        Assert.Null(state.DeleteCollection("Else"));
        Assert.Null(h.Settings.LastApplyDate);
        Assert.Equal(before, File.ReadAllBytes(h.ClaudeConfigPath));
        Assert.Equal(backedUp, backups.Backups("claude_desktop_config"));
        Assert.Equal("Default", state.CollectionsCache.LastAppliedCollection);
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
        var list = h.Dir.File("list.json");
        TempDir.Touch(list, "[]");
        // What is wrong with the document, as it stands.
        Assert.Equal(AppState.SourceUnreadableError("list.json", "top level is not a JSON object"), state.Subscribe(list, null));
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

    /// <summary>
    /// A retry waiting under the old name dies harmlessly when it fires, and the next failure under
    /// the new one starts a chain of its own: renaming a collection during a backoff must not turn
    /// automatic retries off.
    /// </summary>
    [Fact]
    public void ARenameDuringTheBackoffKeepsRetrying()
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

        Assert.Null(state.RenameCollection("T", "U"));
        // Every retry that is due, the old name's included, until the chain has nothing left.
        for (var i = 0; i < 10 && h.Delays.Pending.Count > 0; i++)
        {
            h.Delays.RunNext();
        }
        Assert.NotNull(state.SourceErrors.GetValueOrDefault("U"));   // the chain went on under the new name to its third failure
        Assert.Empty(h.Delays.Pending);
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
        Assert.Equal(PublishModel.UnresolvedMarkNote("ledger", "server_path"), new PublishModel(state, state.ActiveCollection).Export(exported));
        Assert.False(File.Exists(exported));

        // Re-ticking in the Publish dialog records the path where it is now, and clears it.
        var dialog = new PublishModel(state, state.ActiveCollection);
        var row = dialog.PathRows.Single(r => r.Connector == "ledger" && r.Value == edited);
        Assert.False(row.Marked);   // a mark that lost its argument ticks nothing
        row.Marked = true;
        // The tick that answers the lost mark takes its name and its hint.
        Assert.Equal("server_path", row.Name);
        Assert.Equal("your ledger clone", row.Hint);
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
        // Nothing moved: the path is kept back where it sits.
        Assert.Equal(AppState.KeptPathCarriedError("ledger", FieldName.Argument(1)), state.PublishError?.Message);
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
        Assert.Equal(AppState.KeptPathCarriedError("ledger", FieldName.Argument(1)), state.PublishError?.Message);
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
        Assert.Equal(AppState.KeptPathCarriedError("ledger", FieldName.Argument(1)), relaunched.PublishError?.Message);
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
        RewriteLedger(state, MarkedPath, $"--script={MarkedPath}");
        Assert.Equal(AppState.KeptPathCarriedError("ledger", FieldName.Argument(2)), state.PublishError?.Message);
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
        // Unticked, the path is listed as kept back and holds Publish, until the author releases it
        // after reading the preview: that is the reviewed answer.
        row.Marked = false;
        Assert.True(JsonText.Contains(dialog.Preview, MarkedPath));   // the preview shows the path as it will travel
        Assert.Equal(["ledger local.args[0]"], dialog.KeptPaths.Select(k => $"{k.Connector} {k.Field}"));
        Assert.False(dialog.CanPublish);
        Assert.Equal(PublishModel.KeptPathNote("ledger", FieldName.Argument(1)), dialog.Publish());
        dialog.ReleaseKeptPath(MarkedPath);
        Assert.True(dialog.CanPublish);
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
        Assert.Null(state.Upsert("ledger", new McpEntry(NodeWith(MarkedPath, $"--script={MarkedPath}")), null));
        var dialog = new PublishModel(state, state.ActiveCollection);
        dialog.PathRows.Single(r => r.Value == MarkedPath).Marked = true;
        Assert.Equal(["ledger local.args[1]"], dialog.KeptPaths.Select(k => $"{k.Connector} {k.Field}"));
        Assert.False(dialog.CanExport);
        var output = h.Dir.File(Path.Combine("away", "copy.json"));
        Assert.Equal(PublishModel.KeptPathNote("ledger", FieldName.Argument(2)), dialog.Export(output));
        Assert.False(File.Exists(output));
        // The preview shows where it sits.
        Assert.True(JsonText.Contains(dialog.Preview, $"--script={MarkedPath}"));
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
        Assert.Equal(AppState.KeptPathCarriedError("ledger", FieldName.Argument(1)), relaunched.PublishError?.Message);
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
        var carriers = new (string Name, string Field, JsonValue Config)[]
        {
            ("in the command", FieldName.Command,
             JsonValue.Object(("command", JsonValue.String(MarkedPath + "/bin/start")), ("args", JsonValue.Array([])))),
            // Through cmd, so the command line this platform writes numbers it seventh.
            ("in a remote's arguments", FieldName.Argument(7), RemotePattern.Encode(new RemoteConfig("https://mcp.example.com/", RemoteAuth.Auto,
                RemoteLaunchStyle.CmdNpx, extraArgs: ["--config", MarkedPath], package: "mcp-remote"))),
            ("in an additional field", FieldName.Document("additional.cwd"), JsonValue.Object(
                ("command", JsonValue.String("node")), ("args", JsonValue.Array([JsonValue.String("x.js")])),
                ("cwd", JsonValue.String(MarkedPath)))),
        };
        foreach (var (name, field, config) in carriers)
        {
            Assert.Null(state.Upsert(name, new McpEntry(config), null));
            Assert.Equal(AppState.KeptPathCarriedError(name, field), state.PublishError?.Message);
            Assert.Equal(PublishErrorKind.BlockedForReview, state.PublishError?.Kind);
            Assert.Equal(before, File.ReadAllBytes(file));
            state.Remove(name);
            // With it gone there is nothing left to keep back.
            Assert.Null(state.PublishError);
        }
    }

    /// <summary>
    /// What a refusal and the Publish dialog call a field. Wherever the editor opens the connector in
    /// the local form they use its own words — the command, an argument counted from one, the value
    /// of a variable — and a hint belongs to the dialog whatever the form. Where the editor has no
    /// row of its own, the document's name is given as the document's, rather than one the author
    /// would go looking for and not find.
    /// </summary>
    [Fact]
    public void ARefusalAndTheSheetNameAFieldTheWayTheEditorShowsIt()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var file = PublishMarkedLedger(h, state, MarkedPath);
        var before = File.ReadAllBytes(file);
        // An argument: the document counts from zero and the editor's rows from one.
        Assert.Null(state.Upsert("carrier", new McpEntry(NodeWith("--serve", MarkedPath)), null));
        Assert.Equal(AppState.KeptPathCarriedError("carrier", FieldName.Argument(2)), state.PublishError?.Message);
        Assert.Equal(PublishErrorKind.BlockedForReview, state.PublishError?.Kind);

        // Everywhere else, where the dialog lists the path with a note of its own. An argument row is
        // answered by ticking it, so it is an entry of the dialog's rows rather than this list.
        Assert.Null(state.Upsert("carrier", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String(MarkedPath)),
            ("args", JsonValue.Array([JsonValue.String("x.js")])),
            ("env", JsonValue.Object(("LEDGER", JsonValue.String(MarkedPath)))),
            ("cwd", JsonValue.String(MarkedPath)))), "carrier"));
        // The first place the walk reaches, and one no form of the editor shows.
        Assert.Equal(AppState.KeptPathCarriedError("carrier", FieldName.Document("additional.cwd")),
                     state.PublishError?.Message);
        var dialog = new PublishModel(state, state.ActiveCollection);
        dialog.EnvRows.Single(r => r.Connector == "carrier" && r.Name == "LEDGER").Share = true;
        dialog.PathRows.Single(r => r.Connector == "ledger").Hint = $"like mine, {MarkedPath}";
        (string Entry, string Field)[] named =
        [
            ("carrier additional.cwd", FieldName.Document("additional.cwd")),
            ("carrier env.LEDGER.value", FieldName.EnvValue("LEDGER")),
            ("carrier local.command", FieldName.Command),
            ("ledger needs.server_path.hint", FieldName.Hint("server_path")),
        ];
        Assert.Equal(named.Select(n => n.Entry), dialog.KeptPaths.Select(k => $"{k.Connector} {k.Field}"));
        foreach (var (kept, want) in dialog.KeptPaths.Zip(named))
        {
            Assert.Equal(PublishModel.KeptPathNote(kept.Connector, want.Field), dialog.Note(kept));
        }
        Assert.False(dialog.CanPublish);
        Assert.Equal(PublishModel.KeptPathNote("carrier", FieldName.Document("additional.cwd")), dialog.Publish());
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.False(JsonText.FileContains(file, MarkedPath));
    }

    // MARK: across collections

    /// <summary>
    /// Team (active, published) runs <c>ledger</c> with a marked path and <c>x</c> with the token;
    /// Clients, published too, runs <c>crm</c> beside the harness's connectors. Claude's file holds
    /// Team's.
    /// </summary>
    private static (string Team, string TeamFolder, string ClientsDoc) TwoPublishedCollections(AppStateHarness h, AppState s)
    {
        var team = s.ActiveCollection;
        Assert.Null(s.Upsert("ledger", new McpEntry(true, NodeWith(MarkedPath)), null));
        Assert.Null(s.Upsert("x", new McpEntry(true, NodeWith($"{Placeholder.DirectoryToken}/tools/srv.js")), null));
        Assert.Null(s.StartPublishing(team, PublishFolder(h, "pubTeam"), new PublishIntent(
            [],
            [new("ledger", new Dictionary<JsonPointer, PublishIntent.PathMark> { [ArgPointer(0)] = new("server_path", null, MarkedPath) })],
            []), new HashSet<string>([MarkedPath], StringComparer.Ordinal)));
        Assert.Null(s.CreateCollection("Clients"));
        s.Remove("ledger", "Clients");
        s.Remove("x", "Clients");
        Assert.Null(s.Upsert("crm", new McpEntry(true, JsonValue.Object(("command", JsonValue.String("crm-mcp")))), null, "Clients"));
        var clients = PublishFolder(h, "pubClients");
        Assert.Null(s.StartPublishing("Clients", clients, PublishIntent.None, new HashSet<string>(StringComparer.Ordinal)));
        s.SwitchCollection(team);
        var teamFolder = s.CollectionsCache.Published[team].Folder;
        return (team, teamFolder, Path.Combine(clients, Slug.Make("Clients") + ".json"));
    }

    /// <summary>
    /// The other machine made Clients active while this one was off. Claude's file still holds
    /// Team's connectors, and they are not Clients' to take in: nothing is ingested, and the active
    /// collection is applied over the file.
    /// </summary>
    [Fact]
    public void ARelaunchAfterTheActiveCollectionChangedElsewhereTakesNothingFromClaudesFile()
    {
        using var h = new AppStateHarness();
        string team, teamFolder, clientsDoc;
        using (var first = h.Create())
        {
            (team, teamFolder, clientsDoc) = TwoPublishedCollections(h, first);
            // Every apply records what Claude's file holds.
            Assert.Equal(team, first.CollectionsCache.LastAppliedCollection);
        }
        var store = h.StoreOnDisk();
        store.ActiveCollection = "Clients";
        MasterStoreIO.Save(store, h.MasterStorePath);

        using var relaunched = h.Create();
        Assert.False(relaunched.Store.Collections["Clients"].Mcps.ContainsKey("ledger"));
        Assert.False(relaunched.Store.Collections["Clients"].Mcps.ContainsKey("x"));
        Assert.False(JsonText.FileContains(clientsDoc, MarkedPath));
        Assert.False(JsonText.FileContains(clientsDoc, teamFolder));
        // Claude's file was written from the active collection.
        Assert.False(h.ClaudeServers().ContainsKey("ledger"));
        Assert.Equal("Clients", relaunched.CollectionsCache.LastAppliedCollection);
        Assert.Null(relaunched.PublishError);
    }

    /// <summary>
    /// A file restored with no record of its collection goes into the active one, as a restore
    /// always did; the other collection's marked path and folder are still kept out of its document.
    /// </summary>
    [Fact]
    public void ARestoreWithNoRecordedCollectionStillKeepsAnotherCollectionsPathsBack()
    {
        using var h = new AppStateHarness();
        using var s = h.Create();
        var (_, teamFolder, clientsDoc) = TwoPublishedCollections(h, s);
        var before = File.ReadAllBytes(clientsDoc);
        var backup = h.Dir.File("backup.json");
        File.Copy(h.ClaudeConfigPath, backup);
        s.SwitchCollection("Clients");
        s.RestoreClaudeConfig(backup);
        Assert.Equal("Clients", s.ActiveCollection);
        Assert.Equal("Clients", s.PublishError?.Collection);
        Assert.Equal(AppState.KeptPathCarriedError("ledger", FieldName.Argument(1)), s.PublishError?.Message);
        Assert.Equal(PublishErrorKind.BlockedForReview, s.PublishError?.Kind);
        Assert.Equal(before, File.ReadAllBytes(clientsDoc));
        Assert.False(JsonText.FileContains(clientsDoc, MarkedPath));
        Assert.False(JsonText.FileContains(clientsDoc, teamFolder));
    }

    /// <summary>
    /// A backup records the collection Claude's file held, and its restore goes back there, which
    /// becomes the active collection again: never into whichever collection is active now.
    /// </summary>
    [Fact]
    public void ARestoreGoesBackIntoTheCollectionItsBackupWasTakenFrom()
    {
        using var h = new AppStateHarness();
        using var s = h.Create();
        var (team, teamFolder, clientsDoc) = TwoPublishedCollections(h, s);
        s.SwitchCollection("Clients");   // backs up Team's file first, recorded as Team's
        var backup = s.Service.Backups.Backups("claude_desktop_config")[0];
        Assert.Equal(team, BackupCollections.CollectionOf(backup, h.BackupsDir));
        var clientsBefore = File.ReadAllBytes(clientsDoc);

        s.RestoreClaudeConfig(backup);
        Assert.Equal(team, s.ActiveCollection);
        // Nothing of Team's went into Clients.
        Assert.False(s.Store.Collections["Clients"].Mcps.ContainsKey("ledger"));
        // Team keeps its token.
        Assert.Equal([$"{Placeholder.DirectoryToken}/tools/srv.js"], ArgsOf(s.Store.Collections[team].Mcps["x"].Config));
        Assert.Equal(clientsBefore, File.ReadAllBytes(clientsDoc));
        Assert.False(JsonText.FileContains(clientsDoc, teamFolder));
        Assert.Null(s.PublishError);
        Assert.Equal(team, s.CollectionsCache.LastAppliedCollection);
    }

    [Fact]
    public void ARestoreOfABackupWhoseCollectionIsGoneIsRefused()
    {
        using var h = new AppStateHarness();
        using var s = h.Create();
        Assert.Null(s.CreateCollection("Gone"));
        // A collection of its own content: a backup identical to the newest belongs to whichever
        // collection last wrote it.
        Assert.Null(s.Upsert("gone-only", new McpEntry(true, JsonValue.Object(("command", JsonValue.String("g")))), null, "Gone"));
        s.Apply();
        s.SwitchCollection("Default");   // backs up what Gone put in Claude's file, recorded as Gone's
        var backup = s.Service.Backups.Backups("claude_desktop_config")[0];
        Assert.Equal("Gone", BackupCollections.CollectionOf(backup, h.BackupsDir));
        Assert.Null(s.DeleteCollection("Gone"));
        var claudeBefore = File.ReadAllBytes(h.ClaudeConfigPath);
        var storeBefore = s.Store.Clone();
        var refused = Assert.Throws<RestoreCollectionGoneException>(() => s.RestoreClaudeConfig(backup));
        Assert.Equal("Gone", refused.Collection);
        Assert.Equal(AppState.RestoreCollectionGoneError("Gone"), refused.Message);
        // Nothing was restored.
        Assert.Equal(claudeBefore, File.ReadAllBytes(h.ClaudeConfigPath));
        Assert.Equal(storeBefore, s.Store);

        // The way back the message names: a collection of that name again, and the same backup goes in.
        Assert.Contains("Create a collection named “Gone”", AppState.RestoreCollectionGoneError("Gone"));
        Assert.Null(s.CreateCollection("Gone"));
        s.RestoreClaudeConfig(backup);
        Assert.Equal("Gone", s.ActiveCollection);
        Assert.True(s.Store.Collections["Gone"].Mcps.ContainsKey("gone-only"));
    }

    /// <summary>
    /// Renaming a collection carries what names it outside the store: the record of what Claude's
    /// file holds, and every backup taken from it, which still restores into it under the new name.
    /// </summary>
    [Fact]
    public void ARenameCarriesTheRecordsOfWhereClaudesFileCameFrom()
    {
        using var h = new AppStateHarness();
        using var s = h.Create();
        Assert.Null(s.CreateCollection("Team"));
        Assert.Null(s.Upsert("team-only", new McpEntry(true, JsonValue.Object(("command", JsonValue.String("t")))), null, "Team"));
        s.Apply();
        s.SwitchCollection("Default");   // backs up what Team put in Claude's file
        var backup = s.Service.Backups.Backups("claude_desktop_config")[0];
        Assert.Null(s.RenameCollection("Team", "Team A"));
        Assert.Equal("Team A", BackupCollections.CollectionOf(backup, h.BackupsDir));
        Assert.Null(s.RenameCollection("Default", "Main"));
        Assert.Equal("Main", s.CollectionsCache.LastAppliedCollection);
        s.RestoreClaudeConfig(backup);
        Assert.Equal("Team A", s.ActiveCollection);
        Assert.True(s.Store.Collections["Team A"].Mcps.ContainsKey("team-only"));
    }

    /// <summary>
    /// An own folder where the rewrite cannot reach — a remote connector's client id, which the
    /// command line carries inside a JSON blob — is a folder entry all the same: it says the dialog
    /// cannot write there, both answers say so, and the connector's editor is the way out.
    /// </summary>
    [Fact]
    public void AnOwnFolderTheSheetCannotRewriteSaysWhereToWriteTheToken()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var bound = state.CollectionsCache.Published[state.ActiveCollection].Folder;
        var file = Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json");
        var before = File.ReadAllBytes(file);
        Assert.Null(state.Upsert("svc", new McpEntry(RemotePattern.Encode(new RemoteConfig(
            "https://mcp.example.com/", new RemoteAuth.OAuthClient(bound, "s", ""), RemoteLaunchStyle.Npx,
            package: "mcp-remote"))), null));
        Assert.Equal(AppState.PublishFolderCarriedError("svc", FieldName.Argument(5)), state.PublishError?.Message);

        var dialog = new PublishModel(state, state.ActiveCollection);
        var kept = dialog.KeptPaths[0];
        // A folder of this collection's own, wherever it sits.
        Assert.Equal(PublishModel.KeptPathKind.Folder, kept.Kind);
        // The editor opens this connector in the local form, so the note names the argument the
        // author sees rather than the document's remote.auth.clientId.
        var note = PublishModel.PublishFolderEditNote("svc", FieldName.Argument(5));
        Assert.Equal(note, dialog.Note(kept));
        // The dialog says it did nothing, and what does answer it.
        Assert.Equal(note, dialog.UseDirectoryToken(kept));
        Assert.Equal(note, dialog.ReleaseKeptPath(kept.Value));
        Assert.False(dialog.CanPublish);
        Assert.Equal(note, dialog.Publish());
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.False(JsonText.FileContains(file, bound));

        // The editor is the way out, and taking it clears the block.
        using var editor = new EditorModel(state, EditTarget.Existing("svc", state.Store.Mcps["svc"], state.ActiveCollection),
                                           h.Dialogs, RemoteLaunchStyle.Npx);
        // The command line carries it JSON-escaped, which is why the dialog cannot write over it and
        // the author rewrites the argument itself.
        var carrying = editor.Args.First(row => KeptValue.Holds(row.Value, bound));
        carrying.Value = """{"client_id":"${COLLECTION_DIR}","client_secret":"s"}""";
        Assert.True(editor.Save());
        Assert.Null(state.PublishError);
        Assert.False(JsonText.FileContains(file, bound));
        Assert.True(new PublishModel(state, state.ActiveCollection).CanPublish);
    }

    /// <summary>
    /// Stop Publishing gives up the folder, not this machine's memory: the paths it kept back and the
    /// folders it published into still hold when the collection is published again.
    /// </summary>
    [Fact]
    public void StopPublishingKeepsWhatMustNotTravel()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var firstDocument = PublishMarkedLedger(h, state, MarkedPath);
        var oldFolder = state.CollectionsCache.Published[state.ActiveCollection].Folder;
        state.StopPublishing(state.ActiveCollection, deleteFile: true);
        Assert.False(state.CollectionsCache.Published.ContainsKey(state.ActiveCollection));
        Assert.Equal([MarkedPath], state.CollectionsCache.Kept[state.ActiveCollection].MarkedValues);
        Assert.Equal([oldFolder], state.CollectionsCache.Kept[state.ActiveCollection].PublishedFolders);
        // And it is on disk.
        Assert.Equal([MarkedPath],
                     CollectionsLocalCache.Load(Path.Combine(h.StoreDir, CollectionsLocalCache.FileName))
                        .Kept[state.ActiveCollection].MarkedValues);

        // Published again, into another folder: the mark is still this machine's to keep back, and so
        // is the folder the collection has left.
        RewriteLedger(state, MarkedPath, "--root", oldFolder);
        var second = PublishFolder(h, "again");
        Assert.Equal(AppState.KeptPathCarriedError("ledger", FieldName.Argument(1)),
                     state.StartPublishing(state.ActiveCollection, second, PublishIntent.None));
        Assert.False(File.Exists(Path.Combine(second, Slug.Make(state.ActiveCollection) + ".json")));
        // The binding took the list back, and the record is spent.
        Assert.Equal([MarkedPath], state.CollectionsCache.Published[state.ActiveCollection].MarkedValues);
        Assert.Contains(oldFolder, state.CollectionsCache.Published[state.ActiveCollection].PublishedFolders);
        Assert.False(state.CollectionsCache.Kept.ContainsKey(state.ActiveCollection));
        Assert.False(File.Exists(firstDocument));   // Stop Publishing took the old document
    }

    /// <summary>
    /// Deleting a published collection gives up the binding, and keeps what Stop Publishing keeps:
    /// deleting it is not the author's word that its paths may travel, and the connector that carried
    /// one is still in another collection, or comes back by a copy, an import or an ingest.
    /// </summary>
    [Fact]
    public void DeletingAPublishedCollectionKeepsWhatItKeptBack()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var home = state.ActiveCollection;
        Assert.Null(state.CreateCollection("Team"));            // active, a copy of home
        Assert.Null(state.Upsert("ledger", new McpEntry(NodeWith(MarkedPath)), null, "Team"));
        Assert.Null(state.StartPublishing("Team", PublishFolder(h, "pubTeam"), new PublishIntent(
            [],
            [new("ledger", new Dictionary<JsonPointer, PublishIntent.PathMark> { [ArgPointer(0)] = new("server_path", null, MarkedPath) })],
            []), new HashSet<string>([MarkedPath], StringComparer.Ordinal)));
        var teamBound = state.CollectionsCache.Published["Team"].Folder;
        state.SwitchCollection(home);
        Assert.Null(state.DeleteCollection("Team"));
        Assert.Equal([MarkedPath], state.CollectionsCache.Kept["Team"].MarkedValues);
        Assert.Equal([teamBound], state.CollectionsCache.Kept["Team"].PublishedFolders);
        // And it is on disk.
        Assert.Equal([MarkedPath],
                     CollectionsLocalCache.Load(Path.Combine(h.StoreDir, CollectionsLocalCache.FileName)).Kept["Team"].MarkedValues);

        // home publishes already, so the connector moving across is an ordinary save: no dialog, no
        // preview, and the only thing between the path and the document is the union.
        var folder = PublishFolder(h, "pubHome");
        Assert.Null(state.StartPublishing(home, folder, PublishIntent.None, new HashSet<string>(StringComparer.Ordinal)));
        var document = Path.Combine(folder, Slug.Make(home) + ".json");
        var before = File.ReadAllBytes(document);
        Assert.Null(state.Upsert("ledger", new McpEntry(NodeWith(MarkedPath)), null, home));
        Assert.Equal(AppState.KeptPathCarriedError("ledger", FieldName.Argument(1)), state.PublishError?.Message);
        Assert.Equal(PublishErrorKind.BlockedForReview, state.PublishError?.Kind);
        // The document in the folder is left as it was.
        Assert.Equal(before, File.ReadAllBytes(document));
        Assert.False(JsonText.FileContains(document, MarkedPath));
    }

    /// <summary>
    /// A published collection deleted, or stopped, on the author's other machine arrives as a store
    /// and a sidecar without it. Its binding goes with them, and what it kept back does not: the
    /// load that drops the binding leaves the same record a delete made here leaves.
    /// </summary>
    [Fact]
    public void APublishedCollectionDeletedOnAnotherMachineKeepsWhatItKeptBack()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var home = state.ActiveCollection;
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.Upsert("ledger", new McpEntry(NodeWith(MarkedPath)), null, "Team"));
        Assert.Null(state.StartPublishing("Team", PublishFolder(h, "pubTeam"), new PublishIntent(
            [],
            [new("ledger", new Dictionary<JsonPointer, PublishIntent.PathMark> { [ArgPointer(0)] = new("server_path", null, MarkedPath) })],
            []), new HashSet<string>([MarkedPath], StringComparer.Ordinal)));
        state.SwitchCollection(home);
        var folder = PublishFolder(h, "pubHome");
        Assert.Null(state.StartPublishing(home, folder, PublishIntent.None, new HashSet<string>(StringComparer.Ordinal)));
        var document = Path.Combine(folder, Slug.Make(home) + ".json");
        Assert.Contains(MarkedPath, state.KeptBack(home).Values);   // the mark is known before the delete

        // The other machine deletes Team: the master list and the sidecar arrive without it.
        var store = h.StoreOnDisk();
        store.Collections.Remove("Team");
        store.ActiveCollection = home;
        MasterStoreIO.Save(store, h.MasterStorePath);
        new CollectionsFile(state.CollectionsFile.Collections.Where(p => p.Key != "Team")
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal))
            .Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        state.Reload();
        // The sidecar no longer vouches for the binding, and what it kept back stayed.
        Assert.False(state.CollectionsCache.Published.ContainsKey("Team"));
        Assert.Equal([MarkedPath], state.CollectionsCache.Kept["Team"].MarkedValues);

        var before = File.ReadAllBytes(document);
        Assert.Null(state.Upsert("ledger", new McpEntry(NodeWith(MarkedPath)), null, home));
        Assert.Equal(AppState.KeptPathCarriedError("ledger", FieldName.Argument(1)), state.PublishError?.Message);
        Assert.Equal(PublishErrorKind.BlockedForReview, state.PublishError?.Kind);
        Assert.Equal(before, File.ReadAllBytes(document));
        Assert.False(JsonText.FileContains(document, MarkedPath));
    }

    /// <summary>
    /// A collection made with a deleted one's name is a different collection, and will publish under
    /// an origin of its own. The paths the old one kept back are still the author's, and still
    /// refused; its folders are another collection's here, released rather than written over, since
    /// ${COLLECTION_DIR} in this collection's document would stand for somewhere else.
    /// </summary>
    [Fact]
    public void ACollectionMadeWithADeletedOnesNameDoesNotInheritItsFolders()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var home = state.ActiveCollection;
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.Upsert("ledger", new McpEntry(NodeWith(MarkedPath)), null, "Team"));
        Assert.Null(state.StartPublishing("Team", PublishFolder(h, "pubTeam"), new PublishIntent(
            [],
            [new("ledger", new Dictionary<JsonPointer, PublishIntent.PathMark> { [ArgPointer(0)] = new("server_path", null, MarkedPath) })],
            []), new HashSet<string>([MarkedPath], StringComparer.Ordinal)));
        var bound = state.CollectionsCache.Published["Team"].Folder;
        // The binding carries the origin it publishes under, and every write keeps it.
        Assert.Equal(state.CollectionsFile.Collections["Team"].Publish?.Origin, state.CollectionsCache.Published["Team"].Origin);
        state.SwitchCollection(home);

        // Stopped, the collection is the same one: the folder it published into is still its own,
        // and ${COLLECTION_DIR} is the answer to a connector that carries it.
        state.StopPublishing("Team", deleteFile: false);
        Assert.Contains(bound, state.KeptBack("Team").Folders);

        Assert.Null(state.DeleteCollection("Team"));
        Assert.Null(state.CreateCollection("Team"));   // the way back the refused restore names
        var kept = state.KeptBack("Team");
        // A different collection: never released is not the rule for it, and it is still a folder
        // this machine binds, so it is releasable.
        Assert.DoesNotContain(bound, kept.Folders);
        Assert.Contains(bound, kept.Values);
        Assert.Contains(MarkedPath, kept.Values);   // and the path the old one marked is still the author's
    }

    /// <summary>
    /// Team published here with a path marked, then deleted: what every test below starts from.
    /// Returns the folder it published into.
    /// </summary>
    private static string PublishThenDeleteTeam(AppStateHarness h, AppState state)
    {
        var home = state.ActiveCollection;
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.Upsert("ledger", new McpEntry(NodeWith(MarkedPath)), null, "Team"));
        var folder = PublishFolder(h, "pubTeam");
        Assert.Null(state.StartPublishing("Team", folder, new PublishIntent(
            [],
            [new("ledger", new Dictionary<JsonPointer, PublishIntent.PathMark> { [ArgPointer(0)] = new("server_path", null, MarkedPath) })],
            []), new HashSet<string>([MarkedPath], StringComparer.Ordinal)));
        state.SwitchCollection(home);
        Assert.Null(state.DeleteCollection("Team"));
        return folder;
    }

    /// <summary>
    /// A record belongs to the collection that published it, and that collection leaving the store is
    /// what ends the claim — wherever it leaves from. A collection of the same name arriving from the
    /// author's other machine is as much a different collection as one made here.
    /// </summary>
    [Fact]
    public void ACollectionOfTheSameNameArrivingFromAnotherMachineInheritsNoFolders()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishThenDeleteTeam(h, state);
        // The other machine makes a collection called Team again, and the store syncs here.
        var store = h.StoreOnDisk();
        Assert.Null(store.AddCollection("Team", copyingCurrent: false));
        store.ActiveCollection = state.ActiveCollection;
        MasterStoreIO.Save(store, h.MasterStorePath);
        state.Reload();
        Assert.True(state.Store.Collections.ContainsKey("Team"));   // Team arrived
        var kept = state.KeptBack("Team");
        // It never published there, so the token stands for nothing; the folder is one this machine
        // binds, and releasable, and the path the old Team marked is still the author's.
        Assert.DoesNotContain(folder, kept.Folders);
        Assert.Contains(folder, kept.Values);
        Assert.Contains(MarkedPath, kept.Values);
    }

    /// <summary>
    /// A collection that only stopped publishing never left the store, so the folders it published
    /// into are still its own: the token stands for them, and publishing again takes them back.
    /// </summary>
    [Fact]
    public void AStoppedCollectionKeepsItsOwnFoldersThroughTheNextPublish()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var home = state.ActiveCollection;
        Assert.Null(state.StartPublishing(home, PublishFolder(h, "pub1"), PublishIntent.None,
            new HashSet<string>(StringComparer.Ordinal)));
        var old = state.CollectionsCache.Published[home].Folder;
        state.StopPublishing(home, deleteFile: false);
        // It never left the store, so the folder it published into is still its own.
        Assert.Contains(old, state.KeptBack(home).Folders);
        var second = PublishFolder(h, "pub2");
        Assert.Null(state.StartPublishing(home, second, PublishIntent.None, new HashSet<string>(StringComparer.Ordinal)));
        // And the binding takes the folders back with the record.
        Assert.Contains(old, state.CollectionsCache.Published[home].PublishedFolders);
        Assert.Contains(old, state.KeptBack(home).Folders);
        Assert.False(state.CollectionsCache.Kept.ContainsKey(home));   // the record is spent
    }

    /// <summary>
    /// The release the dialog offers for a re-used name holds. A record whose collection has gone
    /// takes its folders with it, so a new collection of that name does not get them back through its
    /// own binding: the author's answer stands, the first publish writes, and every save after it is
    /// an ordinary one.
    /// </summary>
    [Fact]
    public void AReleasedFolderStaysReleasedForACollectionMadeWithADeletedOnesName()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishThenDeleteTeam(h, state);
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.Upsert("tool", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String(folder + "/bin/tool")))), null, "Team"));
        var second = PublishFolder(h, "pubTeam2");
        var dialog = new PublishModel(state, "Team") { Folder = second };
        var entry = dialog.KeptPaths.Single(k => k.Value == folder);
        Assert.Equal(PublishModel.KeptPathKind.Path, entry.Kind);   // the old Team's folder, not this one's
        Assert.Null(dialog.ReleaseKeptPath(folder));                // Release is the answer the dialog offers
        Assert.True(dialog.CanPublish);
        Assert.Null(dialog.Publish());                              // and publishing holds to it
        var document = Path.Combine(second, Slug.Make("Team") + ".json");
        Assert.True(JsonText.FileContains(document, folder));        // released, it travels as written
        // And the old folder is not this collection's own, so it is not refused again.
        Assert.DoesNotContain(folder, state.CollectionsCache.Published["Team"].PublishedFolders);
        Assert.Contains(folder, state.CollectionsCache.Published["Team"].ReleasedValues);

        // An ordinary save after it: the answer the author gave still stands, with no banner. Before
        // this round the folder came back as the new binding's own and every save failed from here.
        var before = File.ReadAllBytes(document);
        Assert.Null(state.Upsert("other", new McpEntry(NodeWith("/tmp/other.js")), null, "Team"));
        Assert.Null(state.PublishError);
        Assert.NotEqual(before, File.ReadAllBytes(document));   // and the save reached the folder
    }

    /// <summary>
    /// A folder this machine published a collection into is its own to keep back for as long as the
    /// folder exists, whichever collection bears the name now. The record a deleted collection left
    /// belongs to none, so a new collection of its name publishing elsewhere leaves it where it is:
    /// the old folder is refused as a kept path, not taken as the new collection's own and not
    /// dropped with the record.
    /// </summary>
    [Fact]
    public void ADepartedCollectionsFolderIsStillKeptBackOnceItsNamePublishesAgain()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishThenDeleteTeam(h, state);
        Assert.Null(state.CreateCollection("Team"));
        var second = PublishFolder(h, "pubTeam2");
        Assert.Null(state.StartPublishing("Team", second, PublishIntent.None, new HashSet<string>(StringComparer.Ordinal)));
        // The old Team's record outlives the new one's publish, and belongs to no collection.
        var record = state.CollectionsCache.Kept["Team"];
        Assert.Equal([folder], record.PublishedFolders);
        Assert.Null(record.Origin);
        var kept = state.KeptBack("Team");
        Assert.Contains(folder, kept.Values);          // a path this machine keeps back
        Assert.DoesNotContain(folder, kept.Folders);   // not a folder of the new Team's own

        // The old folder comes back in a connector: an ingest, a restore or a hand edit. Before this
        // round the record went with the first publish, folders and all, and the folder travelled
        // with no banner.
        Assert.Null(state.Upsert("tool", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String(folder + "/bin/tool")))), null, "Team"));
        Assert.Equal(PublishErrorKind.BlockedForReview, state.PublishError?.Kind);
        Assert.False(JsonText.FileContains(Path.Combine(second, Slug.Make("Team") + ".json"), folder));
    }

    /// <summary>
    /// The release the dialog offers for a departed collection's folder holds through the new
    /// collection's own Stop and publish. Stopping merges the record it left into the new
    /// collection's, and the departed folder must not come out the other side as the new
    /// collection's own: it stays apart, still refused, still releasable, and released it travels.
    /// </summary>
    [Fact]
    public void ADepartedCollectionsFolderStaysReleasableAfterTheNameStopsAndPublishesAgain()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishThenDeleteTeam(h, state);
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.StartPublishing("Team", PublishFolder(h, "pubTeam2"), PublishIntent.None,
            new HashSet<string>(StringComparer.Ordinal)));
        state.StopPublishing("Team", deleteFile: false);
        var third = PublishFolder(h, "pubTeam3");
        Assert.Null(state.StartPublishing("Team", third, PublishIntent.None, new HashSet<string>(StringComparer.Ordinal)));
        // The record left behind holds the old Team's folder apart from the new one's, and the
        // binding did not take it.
        Assert.Equal([folder], state.CollectionsCache.Kept["Team"].DepartedFolders);
        Assert.DoesNotContain(folder, state.CollectionsCache.Published["Team"].PublishedFolders);
        var kept = state.KeptBack("Team");
        Assert.Contains(folder, kept.Values);
        Assert.DoesNotContain(folder, kept.Folders);

        Assert.Null(state.Upsert("tool", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String(folder + "/bin/tool")))), null, "Team"));
        Assert.Equal(PublishErrorKind.BlockedForReview, state.PublishError?.Kind);
        var dialog = new PublishModel(state, "Team");
        var entry = dialog.KeptPaths.Single(k => k.Value == folder);
        Assert.Equal(PublishModel.KeptPathKind.Path, entry.Kind);   // the old Team's folder, still releasable
        Assert.Null(dialog.ReleaseKeptPath(folder));
        Assert.True(dialog.CanPublish);
        Assert.Null(dialog.Publish());
        var document = Path.Combine(third, Slug.Make("Team") + ".json");
        Assert.True(JsonText.FileContains(document, folder));       // released, it travels as written
        Assert.Null(state.Upsert("other", new McpEntry(NodeWith("/tmp/other.js")), null, "Team"));
        Assert.Null(state.PublishError);                            // and a later save is an ordinary one
    }

    /// <summary>
    /// A rename lands on a name only a departed collection can have left a record under, the store
    /// refusing a live one's. That record is this machine's memory of the folder the departed
    /// collection published into, and the renamed collection's own record merges with it rather
    /// than writing over it: the folder is departed to the collection now bearing the name, and
    /// stays kept back from every document this machine publishes.
    /// </summary>
    [Fact]
    public void ARenameOntoADepartedCollectionsNameKeepsTheFolderItLeft()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var home = state.ActiveCollection;
        // Squad published and stopped, so its record is its own; Team published and was deleted, so
        // its record is a departed collection's.
        Assert.Null(state.CreateCollection("Squad"));
        var squadFolder = PublishFolder(h, "pubSquad");
        Assert.Null(state.StartPublishing("Squad", squadFolder, PublishIntent.None, new HashSet<string>(StringComparer.Ordinal)));
        var squadOrigin = state.CollectionsFile.Collections["Squad"].Publish?.Origin;
        Assert.NotNull(squadOrigin);
        state.StopPublishing("Squad", deleteFile: false);
        state.SwitchCollection(home);
        var folder = PublishThenDeleteTeam(h, state);
        var homeFolder = PublishFolder(h, "pubHome");
        Assert.Null(state.StartPublishing(home, homeFolder, PublishIntent.None, new HashSet<string>(StringComparer.Ordinal)));
        Assert.Contains(folder, state.KeptBack(home).Values);   // kept back before the rename

        Assert.Null(state.RenameCollection("Squad", "Team"));
        var record = state.CollectionsCache.Kept["Team"];
        Assert.Equal([squadFolder], record.PublishedFolders);   // the renamed collection's own folder
        Assert.Equal(squadOrigin, record.Origin);               // under the origin it published under
        Assert.Equal([folder], record.DepartedFolders);         // and the old Team's folder, departed to it
        Assert.False(state.CollectionsCache.Kept.ContainsKey("Squad"));
        Assert.Contains(folder, state.KeptBack(home).Values);   // and kept back after it
        // The old folder comes back in a connector of the collection this machine publishes. Before
        // this round the rename wrote one record over the other, and the folder travelled.
        Assert.Null(state.Upsert("tool", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String(folder + "/bin/tool")))), null, home));
        Assert.Equal(PublishErrorKind.BlockedForReview, state.PublishError?.Kind);
        Assert.False(JsonText.FileContains(Path.Combine(homeFolder, Slug.Make(home) + ".json"), folder));
    }

    /// <summary>
    /// The merged record is the renamed collection's own: it published under the origin the record
    /// carries, so publishing again takes its folders back as its own, while the departed
    /// collection's folder stays what it was to it — a path kept back, releasable, never the
    /// token's.
    /// </summary>
    [Fact]
    public void ACollectionRenamedOntoADepartedNameStillOwnsItsFoldersWhenItPublishesAgain()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var home = state.ActiveCollection;
        Assert.Null(state.CreateCollection("Squad"));
        var squadFolder = PublishFolder(h, "pubSquad");
        Assert.Null(state.StartPublishing("Squad", squadFolder, PublishIntent.None, new HashSet<string>(StringComparer.Ordinal)));
        state.StopPublishing("Squad", deleteFile: false);
        state.SwitchCollection(home);
        var folder = PublishThenDeleteTeam(h, state);
        Assert.Null(state.RenameCollection("Squad", "Team"));

        Assert.Null(state.StartPublishing("Team", PublishFolder(h, "pubTeam2"), PublishIntent.None,
            new HashSet<string>(StringComparer.Ordinal)));
        // The binding takes its own old folder back, which the token stands for; the departed
        // collection's is a path kept back, and stays behind for the next Stop.
        Assert.Contains(squadFolder, state.CollectionsCache.Published["Team"].PublishedFolders);
        var kept = state.KeptBack("Team");
        Assert.Contains(squadFolder, kept.Folders);
        Assert.Contains(folder, kept.Values);
        Assert.DoesNotContain(folder, kept.Folders);
        Assert.Equal([folder], state.CollectionsCache.Kept["Team"].DepartedFolders);
    }

    /// <summary>
    /// A collection the author publishes from their other machine marks its paths in the sidecar,
    /// which syncs with the master list. Those marks are this machine's to keep back too, so a copy of
    /// that connector reaching a collection published here is refused, with no binding involved.
    /// </summary>
    [Fact]
    public void APathMarkedForACollectionPublishedFromAnotherMachineIsKeptBackHere()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var home = state.ActiveCollection;
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.Upsert("ledger", new McpEntry(NodeWith(MarkedPath)), null, "Team"));
        state.SwitchCollection(home);
        var all = state.CollectionsFile.Collections.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        all["Team"] = new CollectionsFile.Entry(CollectionKind.Local, publish: new CollectionsFile.PublishRecord(
            "team", "team-origin", new PublishIntent(
                [],
                [new("ledger", new Dictionary<JsonPointer, PublishIntent.PathMark> { [ArgPointer(0)] = new("server_path", null, MarkedPath) })],
                [])));
        new CollectionsFile(all).Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        state.Reload();
        Assert.True(state.IsPublished("Team"));
        // The folder is the other machine's, not this one's.
        Assert.False(state.CollectionsCache.Published.ContainsKey("Team"));

        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(home, folder, PublishIntent.None, new HashSet<string>(StringComparer.Ordinal)));
        var document = Path.Combine(folder, Slug.Make(home) + ".json");
        var before = File.ReadAllBytes(document);
        Assert.Null(state.Upsert("ledger", new McpEntry(NodeWith(MarkedPath)), null, home));
        Assert.Equal(AppState.KeptPathCarriedError("ledger", FieldName.Argument(1)), state.PublishError?.Message);
        Assert.Equal(PublishErrorKind.BlockedForReview, state.PublishError?.Kind);
        Assert.Equal(before, File.ReadAllBytes(document));
        Assert.False(JsonText.FileContains(document, MarkedPath));
    }

    /// <summary>
    /// A connector an installer wrote straight into Claude's config while the app was off, and the
    /// other machine switched collections meanwhile: the collection that was applied keeps its own,
    /// and the new name still comes in to the collection now active.
    /// </summary>
    [Fact]
    public void ALaunchAfterTheActiveCollectionChangedStillTakesInWhatIsNew()
    {
        using var h = new AppStateHarness();
        string team;
        using (var first = h.Create())
        {
            team = first.ActiveCollection;
            Assert.Null(first.Upsert("a", new McpEntry(true, JsonValue.Object(("command", JsonValue.String("a")))), null));
            Assert.Null(first.CreateCollection("Second"));
            first.Remove("a", "Second");
            first.SwitchCollection(team);
        }
        var store = h.StoreOnDisk();
        store.ActiveCollection = "Second";
        MasterStoreIO.Save(store, h.MasterStorePath);
        var servers = new Dictionary<string, JsonValue>(h.ClaudeServers(), StringComparer.Ordinal)
        {
            ["installer"] = NodeWith("/opt/installer/srv.js"),
        };
        h.WriteClaudeServers(servers.Select(p => (p.Key, p.Value)).ToArray());

        using var relaunched = h.Create();
        // The hand-added connector came in, and Claude still runs it.
        Assert.True(relaunched.Store.Collections["Second"].Mcps.ContainsKey("installer"));
        Assert.True(h.ClaudeServers().ContainsKey("installer"));
        // What the applied collection renders stays there.
        Assert.False(relaunched.Store.Collections["Second"].Mcps.ContainsKey("a"));
        Assert.False(relaunched.Store.Collections[team].Mcps.ContainsKey("installer"));
    }

    /// <summary>
    /// The collection Claude's file was last applied from has been deleted meanwhile, here or on the
    /// other machine. It renders nothing to leave alone, so the names the last apply wrote stand in
    /// for its render: those stay where they are and everything else comes in, which keeps the
    /// connector an installer wrote into the file.
    /// </summary>
    [Fact]
    public void ALaunchIngestKeepsWhatIsNewWhenTheCollectionItAppliedIsGone()
    {
        using var h = new AppStateHarness();
        string home;
        using (var first = h.Create())
        {
            home = first.ActiveCollection;
            Assert.Null(first.CreateCollection("Second"));
            first.SwitchCollection(home);
        }
        // The record names the collection Claude's file came from. The store syncs and this machine's
        // cache does not, so the collection can be gone from one and named by the other.
        var cachePath = Path.Combine(h.StoreDir, CollectionsLocalCache.FileName);
        var cache = CollectionsLocalCache.Load(cachePath);
        // Only which collection is faked: the names that apply wrote are kept, as the Swift mirror
        // keeps them by mutating the record in place. A record with no names at all is the state
        // Ingestible now takes nothing in for.
        new CollectionsLocalCache(cache.Synced, cache.Published, cache.Kept, "Second", cache.LastAppliedNames).Save(cachePath);
        var store = h.StoreOnDisk();
        store.Collections.Remove("Second");
        store.ActiveCollection = home;
        MasterStoreIO.Save(store, h.MasterStorePath);
        var servers = new Dictionary<string, JsonValue>(h.ClaudeServers(), StringComparer.Ordinal)
        {
            ["installer"] = NodeWith("/opt/installer/srv.js"),
        };
        h.WriteClaudeServers(servers.Select(p => (p.Key, p.Value)).ToArray());

        using var relaunched = h.Create();
        // The hand-added connector came in, and Claude still runs it.
        Assert.True(relaunched.Store.Collections[home].Mcps.ContainsKey("installer"));
        Assert.True(h.ClaudeServers().ContainsKey("installer"));
    }

    /// <summary>
    /// The same launch for a user who publishes nothing: what the collection that is gone rendered is
    /// not poured into the active one, which is the whole reason the names are recorded.
    /// </summary>
    [Fact]
    public void ALaunchIngestLeavesTheDeletedCollectionsOwnConnectorsAlone()
    {
        using var h = new AppStateHarness();
        string home;
        List<string> before;
        using (var first = h.Create())
        {
            home = first.ActiveCollection;
            Assert.Null(first.CreateCollection("Team"));   // Team is active, so Claude's file holds Team
            Assert.Null(first.Upsert("t1", new McpEntry(true, JsonValue.Object(("command", JsonValue.String("t1")))), null, "Team"));
            first.Apply();   // Claude's file now holds t1, and the record says Team wrote it
            Assert.True(h.ClaudeServers().ContainsKey("t1"));
            before = first.Store.Collections[home].Mcps.Keys.Order(StringComparer.Ordinal).ToList();
        }
        // The other machine deletes Team. Claude's file still holds what Team rendered, and one
        // connector an installer wrote beside it while the app was off.
        var store = h.StoreOnDisk();
        store.Collections.Remove("Team");
        store.ActiveCollection = home;
        MasterStoreIO.Save(store, h.MasterStorePath);
        var servers = new Dictionary<string, JsonValue>(h.ClaudeServers(), StringComparer.Ordinal)
        {
            ["installer"] = NodeWith("/opt/installer/srv.js"),
        };
        h.WriteClaudeServers(servers.Select(p => (p.Key, p.Value)).ToArray());

        using var relaunched = h.Create();
        // Team's own connectors stayed out, and the new one came in.
        Assert.Equal(before.Append("installer").Order(StringComparer.Ordinal),
                     relaunched.Store.Collections[home].Mcps.Keys.Order(StringComparer.Ordinal));
        Assert.False(relaunched.Store.Collections[home].Mcps.ContainsKey("t1"));
    }

    /// <summary>
    /// The same launch where this machine publishes: the collection that is gone was published from
    /// the author's other machine with a path marked, so its connector reaching the collection
    /// published here would send that path as written. It is not taken in, and nothing is written.
    /// </summary>
    [Fact]
    public void ALaunchIngestDoesNotPublishADeletedCollectionsMarkedPath()
    {
        using var h = new AppStateHarness();
        string document;
        using (var first = h.Create())
        {
            Assert.Null(first.CreateCollection("Team"));   // Team is active, so Claude's file holds Team
            Assert.Null(first.Upsert("ledger", new McpEntry(true, NodeWith(MarkedPath)), null, "Team"));
            first.Apply();
            // Team is published from the author's other machine: the sidecar carries the mark and its value.
            var all = first.CollectionsFile.Collections.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            all["Team"] = new CollectionsFile.Entry(CollectionKind.Local, publish: new CollectionsFile.PublishRecord(
                "team", "team-origin", new PublishIntent([],
                    [new("ledger", new Dictionary<JsonPointer, PublishIntent.PathMark> { [ArgPointer(0)] = new("server_path", null, MarkedPath) })],
                    [])));
            new CollectionsFile(all).Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
            first.Reload();
            var folder = PublishFolder(h, "pubDefault");
            Assert.Null(first.StartPublishing("Default", folder, PublishIntent.None, new HashSet<string>(StringComparer.Ordinal)));
            document = Path.Combine(folder, Slug.Make("Default") + ".json");
        }

        // The other machine deletes Team while this one is off, so nothing here records its marks any
        // more: the store, the sidecar and the binding all arrive without it.
        var store = h.StoreOnDisk();
        store.Collections.Remove("Team");
        store.ActiveCollection = "Default";
        MasterStoreIO.Save(store, h.MasterStorePath);
        var file = CollectionsFile.Load(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        new CollectionsFile(file.Collections.Where(p => p.Key != "Team")
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal))
            .Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));

        using var relaunched = h.Create();
        // The deleted collection's own connector is not poured into the collection this machine publishes.
        Assert.False(relaunched.Store.Collections["Default"].Mcps.ContainsKey("ledger"));
        Assert.Null(relaunched.PublishError);
        Assert.False(JsonText.FileContains(document, MarkedPath));
    }

    /// <summary>A copy of the marked path in an <c>additional</c> field is kept back, and the refusal says where it sits rather than that the mark moved.</summary>
    [Fact]
    public void ACopyOfAMarkedPathSaysWhereItSits()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var file = PublishMarkedLedger(h, state, MarkedPath);
        var before = File.ReadAllBytes(file);
        Assert.Null(state.Upsert("ledger", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String(MarkedPath)])),
            ("description", JsonValue.String($"runs {MarkedPath}")))), "ledger"));
        Assert.Equal(AppState.KeptPathCarriedError("ledger", FieldName.Document("additional.description")), state.PublishError?.Message);
        Assert.Equal(before, File.ReadAllBytes(file));
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
        Assert.Equal(AppState.PublishFolderCarriedError("x", FieldName.Argument(1)), state.PublishError?.Message);
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
        Assert.Equal(AppState.PublishFolderCarriedError("x", FieldName.Argument(1)), relaunched.PublishError?.Message);
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
        Assert.Equal(AppState.PublishFolderCarriedError("typed", FieldName.Argument(1)), state.PublishError?.Message);
        // Answered in the Publish dialog, not another folder.
        Assert.Equal(PublishErrorKind.BlockedForReview, state.PublishError?.Kind);
        Assert.Equal(withSibling, File.ReadAllBytes(file));

        // The dialog lists the folder where it sits, and holds Export until it is answered.
        var dialog = new PublishModel(state, state.ActiveCollection);
        Assert.Equal([$"typed local.args[0] {bound} Folder"],
                     dialog.KeptPaths.Select(k => $"{k.Connector} {k.Field} {k.Value} {k.Kind}"));
        var output = h.Dir.File(Path.Combine("away", "copy.json"));
        Assert.Equal(PublishModel.PublishFolderNote("typed", FieldName.Argument(1)), dialog.Export(output));
        Assert.False(File.Exists(output));
    }

    /// <summary>
    /// A folder written out where no row reaches it: the refusal names the field, the dialog lists it
    /// and holds Publish, and Use ${COLLECTION_DIR} writes the token back into the connector itself,
    /// which Claude's config and the document then follow.
    /// </summary>
    [Fact]
    public void UseDirectoryTokenWritesTheTokenWhereTheFolderSits()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var bound = state.CollectionsCache.Published[state.ActiveCollection].Folder;
        var file = Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json");
        Assert.Null(state.Upsert("tool", new McpEntry(true, JsonValue.Object(
            ("command", JsonValue.String(bound + "/bin/tool")),
            ("env", JsonValue.Object(("PATH_EXTRA", JsonValue.String(bound + ":/opt/lib")))))), null));
        state.Apply();
        Assert.Equal(AppState.PublishFolderCarriedError("tool", FieldName.Command), state.PublishError?.Message);
        var before = File.ReadAllBytes(file);

        var dialog = new PublishModel(state, state.ActiveCollection);
        dialog.EnvRows.Single(r => r.Name == "PATH_EXTRA").Share = true;
        Assert.Equal(["tool env.PATH_EXTRA.value Folder", "tool local.command Folder"],
                     dialog.KeptPaths.Select(k => $"{k.Connector} {k.Field} {k.Kind}"));
        Assert.False(dialog.CanPublish);
        Assert.Equal(PublishModel.PublishFolderNote("tool", FieldName.EnvValue("PATH_EXTRA")), dialog.Publish());
        Assert.Equal(before, File.ReadAllBytes(file));

        foreach (var kept in dialog.KeptPaths)
        {
            Assert.Null(dialog.UseDirectoryToken(kept));
        }
        var token = Placeholder.DirectoryToken;
        Assert.Equal(JsonValue.Object(
            ("command", JsonValue.String($"{token}/bin/tool")),
            ("env", JsonValue.Object(("PATH_EXTRA", JsonValue.String($"{token}:/opt/lib"))))),
            state.Store.Collections[state.ActiveCollection].Mcps["tool"].Config);
        // Claude still runs the folder, which the token stands for here.
        Assert.Equal(JsonValue.Object(
            ("command", JsonValue.String(bound + "/bin/tool")),
            ("env", JsonValue.Object(("PATH_EXTRA", JsonValue.String(bound + ":/opt/lib"))))),
            h.ClaudeServers()["tool"]);
        Assert.Equal($"{token}:/opt/lib", dialog.EnvRows.Single(r => r.Name == "PATH_EXTRA").Value);
        Assert.Empty(dialog.KeptPaths);
        Assert.True(dialog.CanPublish);
        Assert.Null(dialog.Publish());
        Assert.Null(state.PublishError);
        Assert.False(JsonText.FileContains(file, bound));
        Assert.True(JsonText.FileContains(file, token));
    }

    /// <summary>
    /// Release is no answer for a folder of the collection's own, whatever the view offers: the entry
    /// stays, Publish and Export stay held, and the folder reaches no document. Only writing the token
    /// takes it out of the preview.
    /// </summary>
    [Fact]
    public void ReleasingAFolderEntryIsRefused()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var bound = state.CollectionsCache.Published[state.ActiveCollection].Folder;
        var file = Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json");
        Assert.Null(state.Upsert("tool", new McpEntry(true, JsonValue.Object(("command", JsonValue.String(bound + "/bin/tool")))), null));
        var before = File.ReadAllBytes(file);
        var note = PublishModel.PublishFolderNote("tool", FieldName.Command);

        var dialog = new PublishModel(state, state.ActiveCollection);
        var kept = dialog.KeptPaths[0];
        Assert.Equal(PublishModel.KeptPathKind.Folder, kept.Kind);
        var preview = dialog.Preview;
        Assert.Equal(note, dialog.ReleaseKeptPath(kept.Value));
        Assert.Equal([kept], dialog.KeptPaths);
        // The refused release changed nothing.
        Assert.Equal(preview, dialog.Preview);
        Assert.False(dialog.CanPublish);
        Assert.False(dialog.CanExport);
        Assert.Equal(note, dialog.Publish());
        var output = h.Dir.File(Path.Combine("away", "copy.json"));
        Assert.Equal(note, dialog.Export(output));
        Assert.False(File.Exists(output));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.False(JsonText.FileContains(file, bound));

        // Nor does the state let it go when asked directly, or when the list says so.
        var releasing = new HashSet<string>([bound], StringComparer.Ordinal);
        Assert.Equal(AppState.PublishFolderCarriedError("tool", FieldName.Command),
                     state.WriteExport(state.ActiveCollection, dialog.Intent, output, released: releasing));
        Assert.False(File.Exists(output));
        Assert.Equal(AppState.PublishFolderCarriedError("tool", FieldName.Command),
                     state.UpdatePublishIntent(state.ActiveCollection, dialog.Intent, new HashSet<string>(StringComparer.Ordinal), releasing));
        Assert.False(JsonText.FileContains(file, bound));

        Assert.True(JsonText.Contains(dialog.Preview, bound));
        Assert.Null(dialog.UseDirectoryToken(kept));
        Assert.False(JsonText.Contains(dialog.Preview, bound));
        Assert.True(dialog.CanPublish);
    }

    /// <summary>The folder in the author's own hint is the dialog's to rewrite: the token goes into the hint.</summary>
    [Fact]
    public void UseDirectoryTokenRewritesAHintInTheSheet()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishFolder(h);
        Assert.Null(state.Upsert("svc", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("svc")), ("env", JsonValue.Object(("TOKEN", JsonValue.String("sk-1")))))), null));
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var bound = state.CollectionsCache.Published[state.ActiveCollection].Folder;
        var dialog = new PublishModel(state, state.ActiveCollection);
        var row = dialog.EnvRows.Single(r => r.Connector == "svc" && r.Name == "TOKEN");
        row.Hint = $"see {bound}/README";
        var kept = dialog.KeptPaths[0];
        Assert.Equal("svc env.TOKEN.hint Folder", $"{kept.Connector} {kept.Field} {kept.Kind}");
        Assert.Null(dialog.UseDirectoryToken(kept));
        Assert.Equal($"see {Placeholder.DirectoryToken}/README", row.Hint);
        Assert.Empty(dialog.KeptPaths);
        Assert.Null(dialog.Publish());
    }

    /// <summary>
    /// The author moved the publish folder, then restored a backup taken before the move: the store
    /// keeps its token, since the backup renders as the store does with the folder of the day, and the
    /// earlier folder written out anywhere else is kept back as the current one is.
    /// </summary>
    [Fact]
    public void AnEarlierPublishFolderIsTheCollectionsOwnToo()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var token = $"{Placeholder.DirectoryToken}/tools/srv.js";
        Assert.Null(state.Upsert("x", new McpEntry(true, NodeWith(token)), null));
        state.Apply();
        Assert.Null(state.StartPublishing(state.ActiveCollection, PublishFolder(h, "pub1"), PublishIntent.None,
                                          new HashSet<string>(StringComparer.Ordinal)));
        var oldFolder = state.CollectionsCache.Published[state.ActiveCollection].Folder;
        var backup = h.Dir.File("backup.json");
        File.Copy(h.ClaudeConfigPath, backup);
        var second = PublishFolder(h, "pub2");
        Assert.Null(state.ChangePublishFolder(state.ActiveCollection, second));
        var newFolder = state.CollectionsCache.Published[state.ActiveCollection].Folder;
        Assert.Equal([oldFolder, newFolder], state.CollectionsCache.Published[state.ActiveCollection].PublishedFolders.Order(StringComparer.Ordinal));
        var file = Path.Combine(second, Slug.Make(state.ActiveCollection) + ".json");

        state.RestoreClaudeConfig(backup);
        // The backup renders as the store does with the folder it had then.
        Assert.Equal([token], ArgsOf(state.Store.Collections[state.ActiveCollection].Mcps["x"].Config));
        Assert.Null(state.PublishError);
        Assert.False(JsonText.FileContains(file, oldFolder));

        Assert.Null(state.Upsert("old", new McpEntry(NodeWith("--root", oldFolder)), null));
        Assert.Equal(AppState.PublishFolderCarriedError("old", FieldName.Argument(2)), state.PublishError?.Message);
        Assert.False(JsonText.FileContains(file, oldFolder));
        var dialog = new PublishModel(state, state.ActiveCollection);
        var kept = dialog.KeptPaths[0];
        Assert.Equal($"local.args[1] {oldFolder} Folder", $"{kept.Field} {kept.Value} {kept.Kind}");
        Assert.Null(dialog.UseDirectoryToken(kept));
        Assert.Null(state.PublishError);
        Assert.False(JsonText.FileContains(file, oldFolder));
    }

    /// <summary>The folder followed by a list separator is still the folder; followed by what continues a file name it is another name.</summary>
    [Fact]
    public void ThePublishFolderIsFoundBesideAnySeparator()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var bound = state.CollectionsCache.Published[state.ActiveCollection].Folder;
        var file = Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json");
        foreach (var written in new[] { $"{bound}:/opt/lib", $"{bound};/opt/lib", $"{bound},x", $"x {bound}" })
        {
            Assert.Null(state.Upsert("py", new McpEntry(JsonValue.Object(("command", JsonValue.String("python3")),
                ("args", JsonValue.Array([JsonValue.String("--path"), JsonValue.String(written)])))), null));
            Assert.Equal(AppState.PublishFolderCarriedError("py", FieldName.Argument(2)), state.PublishError?.Message);
            Assert.False(JsonText.FileContains(file, bound));
            state.Remove("py");
        }
        foreach (var other in new[] { $"{bound}.bak", $"{bound}_old/x", $"{bound}é/x" })
        {
            Assert.Null(state.Upsert("py", new McpEntry(JsonValue.Object(("command", JsonValue.String("python3")),
                ("args", JsonValue.Array([JsonValue.String("--path"), JsonValue.String(other)])))), null));
            Assert.Null(state.PublishError);
            state.Remove("py");
        }
    }

    /// <summary>
    /// Another collection's publish folder is a path this machine keeps back, not this one's own: the
    /// token would stand for the wrong folder, so its answer is Release.
    /// </summary>
    [Fact]
    public void AnotherCollectionsPublishFolderIsReleasedNotRewritten()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var team = state.ActiveCollection;
        Assert.Null(state.StartPublishing(team, PublishFolder(h, "pubTeam"), PublishIntent.None));
        var teamFolder = state.CollectionsCache.Published[team].Folder;
        Assert.Null(state.CreateCollection("Clients"));
        Assert.Null(state.Upsert("shared", new McpEntry(NodeWith(teamFolder + "/tools/x.js")), null, "Clients"));
        var clients = PublishFolder(h, "pubClients");
        Assert.Equal(AppState.KeptPathCarriedError("shared", FieldName.Argument(1)),
                     state.StartPublishing("Clients", clients, PublishIntent.None, new HashSet<string>(StringComparer.Ordinal)));
        var dialog = new PublishModel(state, "Clients");
        var kept = dialog.KeptPaths.Single(k => k.Connector == "shared");
        Assert.Equal(PublishModel.KeptPathKind.Path, kept.Kind);
        // Whose folder it is, before the author sends it.
        Assert.Equal(PublishModel.OtherFolderNote("shared", FieldName.Argument(1), team), dialog.Note(kept));
        // The token stands for no folder here.
        Assert.Equal(dialog.Note(kept), dialog.UseDirectoryToken(kept));
        Assert.Null(dialog.ReleaseKeptPath(kept.Value));
        Assert.Null(dialog.Publish());
        // The author's explicit choice.
        Assert.True(JsonText.FileContains(Path.Combine(clients, Slug.Make("Clients") + ".json"), teamFolder));
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

    // A copy whose name the target already holds: the author's answer decides. Replace takes the
    // target's entry over, skip copies nothing, and the default is still to land beside it.
    [Fact]
    public void CopyingAConnectorHonoursTheCollisionChoice()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Source"));
        Assert.Null(state.Upsert("github", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("/new/github")))), null, "Source"));
        Assert.Null(state.Upsert("github", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("/old/github")))), null, "Default"));

        Assert.Null(state.MakeLocalCopy(["github"], "Source", "Default", Choices(("github", ImportChoice.Skip))));
        // Skip copies nothing.
        Assert.Equal(["github"], AppStateHarness.Keys(
            state.Store.Collections["Default"].Mcps.Keys.Where(k => k.StartsWith("github", StringComparison.Ordinal))));
        // And leaves the target alone.
        Assert.Equal(JsonValue.Object(("command", JsonValue.String("/old/github"))),
            state.Store.Collections["Default"].Mcps["github"].Config);

        Assert.Null(state.MakeLocalCopy(["github"], "Source", "Default", Choices(("github", ImportChoice.Replace))));
        // Replace makes no second entry.
        Assert.Equal(["github"], AppStateHarness.Keys(
            state.Store.Collections["Default"].Mcps.Keys.Where(k => k.StartsWith("github", StringComparison.Ordinal))));
        // And takes the source's config.
        Assert.Equal(JsonValue.Object(("command", JsonValue.String("/new/github"))),
            state.Store.Collections["Default"].Mcps["github"].Config);
        // A replaced copy is still off.
        Assert.False(state.Store.Collections["Default"].Mcps["github"].Enabled);

        Assert.Null(state.MakeLocalCopy(["github"], "Source", "Default"));
        // And with no choice it still lands beside.
        Assert.Equal(["github", "github 2"], AppStateHarness.Keys(
            state.Store.Collections["Default"].Mcps.Keys.Where(k => k.StartsWith("github", StringComparison.Ordinal))));
    }

    // Removing several connectors is one write, not one per connector: a loop would rotate a
    // backup and republish for each. Each one's publish ticks and path marks go with it.
    [Fact]
    public void RemovingSeveralConnectorsPersistsOnce()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        foreach (var name in new[] { "alpha", "beta", "gamma" })
        {
            Assert.Null(state.Upsert(name, new McpEntry(JsonValue.Object(
                ("command", JsonValue.String("/bin/" + name)))), null, "Default"));
        }
        var before = BackupCount(h, "mcps");

        state.Remove(["alpha", "gamma"], "Default");

        Assert.False(state.Store.Collections["Default"].Mcps.ContainsKey("alpha"));
        Assert.False(state.Store.Collections["Default"].Mcps.ContainsKey("gamma"));
        Assert.True(state.Store.Collections["Default"].Mcps.ContainsKey("beta"));
        // One write, so one backup rotation.
        Assert.Equal(before + 1, BackupCount(h, "mcps"));
    }

    // A name the collection does not hold is skipped rather than failing, and removing nothing
    // writes nothing.
    [Fact]
    public void RemovingNoConnectorsWritesNothing()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.Upsert("alpha", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("/bin/alpha")))), null, "Default"));
        var before = BackupCount(h, "mcps");

        state.Remove([], "Default");
        // Nothing to do, nothing written.
        Assert.Equal(before, BackupCount(h, "mcps"));

        state.Remove(["nosuch"], "Default");
        // A name it never held is skipped.
        Assert.True(state.Store.Collections["Default"].Mcps.ContainsKey("alpha"));
    }

    // How many files the named backup series holds, for proving a write happened once.
    private static int BackupCount(AppStateHarness h, string series) =>
        Directory.Exists(h.BackupsDir)
            ? Directory.GetFiles(h.BackupsDir).Count(f => Path.GetFileName(f).StartsWith(series, StringComparison.Ordinal))
            : 0;
}
