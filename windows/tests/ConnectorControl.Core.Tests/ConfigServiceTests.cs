using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/ConfigServiceTests.swift</summary>
public class ConfigServiceTests : IDisposable
{
    private readonly TempDir dir = new("svc");
    private readonly AppPaths paths;
    private readonly ConfigService service;

    public ConfigServiceTests()
    {
        var claudeDir = dir.File("Claude");
        Directory.CreateDirectory(claudeDir);
        paths = new AppPaths(Path.Combine(claudeDir, "claude_desktop_config.json"), dir.File("Connector Control"));
        File.WriteAllText(paths.ClaudeConfigPath, Fixtures.RealisticClaudeConfig);
        service = new ConfigService(paths);
    }

    public void Dispose() => dir.Dispose();

    private static HashSet<string> Set(IEnumerable<string> keys) => keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Claude's file holds the collection it was last applied from, so what that collection renders
    /// is left where it is; with nothing recorded — a first launch — everything is ingested.
    /// </summary>
    [Fact]
    public void TheIngestLeavesTheRecordedCollectionsOwnServersWhereTheyAre()
    {
        // The recorded collection is the active one.
        Assert.Equal(Set(["scoutbook", "aws-mcp", "service-now"]),
                     Set(service.LoadAndReconcile(lastAppliedCollection: "Default").Store.Mcps.Keys));

        // Claude's file holds Team's connectors and one an installer wrote while the app was off.
        var store = service.LoadAndReconcile().Store;
        store.Collections["Team"] = store.Collections["Default"].Clone();
        store.Collections["Team"].Mcps.Remove("scoutbook");
        store.Collections["Default"].Mcps.Remove("aws-mcp");
        service.SaveStore(store);
        var servers = new Dictionary<string, JsonValue>(ClaudeConfigIO.ReadMcpServers(paths.ClaudeConfigPath), StringComparer.Ordinal)
        {
            ["installer"] = JsonValue.Object(("command", JsonValue.String("node"))),
        };
        service.Apply(servers);

        var loaded = service.LoadAndReconcile(lastAppliedCollection: "Team");
        // Team renders it, so it is not poured into the active collection.
        Assert.False(loaded.Store.Collections[loaded.Store.ActiveCollection].Mcps.ContainsKey("aws-mcp"));
        // A name no collection renders is genuinely new and comes in, and only into the active one.
        Assert.True(loaded.Store.Collections[loaded.Store.ActiveCollection].Mcps.ContainsKey("installer"));
        Assert.False(loaded.Store.Collections["Team"].Mcps.ContainsKey("installer"));

        // A record naming a collection this store does not have — deleted here, or on another
        // machine — has no render to compare against, so the names that apply wrote stand in for it.
        File.Delete(paths.MasterStorePath);
        // What the collection that is gone rendered is left where it is, and the rest comes in.
        Assert.Equal(Set(["service-now", "installer"]),
                     Set(service.LoadAndReconcile(lastAppliedCollection: "Gone",
                             lastAppliedNames: Set(["scoutbook", "aws-mcp"])).Store.Mcps.Keys));
        // Without those names nothing in the file can be told from what that collection rendered, so
        // none of it comes in and the store keeps exactly what the call before left it.
        var kept = Set(service.LoadAndReconcile(lastAppliedCollection: "Gone").Store.Mcps.Keys);
        Assert.Equal(Set(["service-now", "installer"]), kept);
        // The collection that is gone rendered it, and it is not guessed at.
        Assert.DoesNotContain("scoutbook", kept);
    }

    /// <summary>
    /// A subscribed collection holds what its author published and nothing more, so what the file
    /// adds while one is active goes to a local collection: the one last applied when that is local,
    /// else the first local one by name, else a new one.
    /// </summary>
    [Fact]
    public void TheIngestTargetIsNeverASubscribedCollection()
    {
        var store = new MasterStore([]);
        store.Collections["Team"] = new Collection();
        store.Collections["Beta"] = new Collection();
        store.ActiveCollection = "Team";
        var synced = new CollectionsFile.Entry(CollectionKind.Synced);
        var teamSynced = new CollectionsFile(new Dictionary<string, CollectionsFile.Entry> { ["Team"] = synced });
        Assert.Equal("Beta", ConfigService.IngestTarget(store, teamSynced, "Team"));
        // The local collection Claude's file was last applied from.
        Assert.Equal("Default", ConfigService.IngestTarget(store, teamSynced, "Default"));
        Assert.Equal("Beta", ConfigService.IngestTarget(store, teamSynced, "Gone"));
        // A local active collection takes it as it always has, and an unreadable sidecar says nothing about kinds.
        Assert.Equal("Team", ConfigService.IngestTarget(store, new CollectionsFile(new Dictionary<string, CollectionsFile.Entry>()), null));
        Assert.Equal("Team", ConfigService.IngestTarget(store, null, null));
        var allSynced = new CollectionsFile(new Dictionary<string, CollectionsFile.Entry>
        {
            ["Team"] = synced, ["Beta"] = synced, ["Default"] = synced,
        });
        // No local collection: a new one, under a free name.
        Assert.Equal("Default 2", ConfigService.IngestTarget(store, allSynced, "Default"));
        store.Collections.Remove("Default");
        Assert.Equal("Default", ConfigService.IngestTarget(store, allSynced, null));
    }

    [Fact]
    public void ALoadWithASubscribedCollectionActiveKeepsTheAdditionsElsewhere()
    {
        var store = new MasterStore([new KeyValuePair<string, McpEntry>("scoutbook", new McpEntry(true, JsonValue.Object(("command", JsonValue.String("old")))))]);
        store.Collections["Team"] = new Collection([new KeyValuePair<string, McpEntry>("aws-mcp", new McpEntry(true, JsonValue.Object(("command", JsonValue.String("t")))))]);
        store.ActiveCollection = "Team";
        service.SaveStore(store);
        service.SaveCollections(new CollectionsFile(new Dictionary<string, CollectionsFile.Entry> { ["Team"] = new(CollectionKind.Synced) }));

        var loaded = service.LoadAndReconcile(lastAppliedCollection: "Team");
        // The subscribed collection stays exactly as its author published it.
        Assert.Equal(Set(["aws-mcp"]), Set(loaded.Store.Collections["Team"].Mcps.Keys));
        Assert.Equal(Set(["scoutbook", "scoutbook 2", "service-now"]), Set(loaded.Store.Collections["Default"].Mcps.Keys));
        Assert.Equal(new IngestedElsewhere("Default", ["scoutbook 2", "service-now"]), loaded.IngestedElsewhere);
        // And it was saved.
        Assert.Equal(loaded.Store, MasterStoreIO.Read(paths.MasterStorePath));
        var again = service.LoadAndReconcile(lastAppliedCollection: "Team");
        // What is already there is not taken in twice.
        Assert.Equal(loaded.Store, again.Store);

        // A local active collection takes what is new itself, and nothing is said about it.
        service.SaveCollections(new CollectionsFile(new Dictionary<string, CollectionsFile.Entry>()));
        var local = service.LoadAndReconcile(lastAppliedCollection: "Team");
        Assert.Null(local.IngestedElsewhere);
        Assert.True(local.Store.Collections["Team"].Mcps.ContainsKey("service-now"));
    }

    [Fact]
    public void EachBackupRecordsTheCollectionItWasAppliedFrom()
    {
        service.Apply(new Dictionary<string, JsonValue> { ["x"] = JsonValue.Object(("command", JsonValue.String("x"))) }, "Team");
        var backup = Assert.Single(service.Backups.Backups("claude_desktop_config"));   // the record is not listed as a backup
        Assert.Equal("Team", BackupCollections.CollectionOf(backup, paths.BackupsDir));
        // The backup itself stays a byte copy of Claude's file.
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(Fixtures.RealisticClaudeConfig), File.ReadAllBytes(backup));
        service.Apply(new Dictionary<string, JsonValue>());
        var after = service.Backups.Backups("claude_desktop_config");
        Assert.Equal(2, after.Count);
        // The second backup is the one the first listing lacks, which holds whichever millisecond
        // the two applies land in.
        var second = Assert.Single(after, p => p != backup);
        // An apply that names no collection records none.
        Assert.Null(BackupCollections.CollectionOf(second, paths.BackupsDir));
        // A file of the same name elsewhere is not the backup.
        Assert.Null(BackupCollections.CollectionOf(dir.File(Path.GetFileName(backup)), paths.BackupsDir));
    }

    [Fact]
    public void FirstLoadImportsAllServersEnabled()
    {
        var result = service.LoadAndReconcile();
        Assert.Equal(Set(["scoutbook", "aws-mcp", "service-now"]), Set(result.Store.Mcps.Keys));
        Assert.All(result.Store.Mcps.Values, e => Assert.True(e.Enabled));
        Assert.Equal(3, result.ClaudeServers!.Count);
        Assert.Equal(result.Store, MasterStoreIO.Load(paths.MasterStorePath).Store);   // persisted
    }

    [Fact]
    public void ApplyWritesEnabledSubsetWithBackups()
    {
        var store = service.LoadAndReconcile().Store;
        store.Mcps["aws-mcp"] = store.Mcps["aws-mcp"] with { Enabled = false };
        service.Apply(store.EnabledServers);
        Assert.Equal(Set(["scoutbook", "service-now"]), Set(ClaudeConfigIO.ReadMcpServers(paths.ClaudeConfigPath).Keys));
        var root = JsonValue.Parse(File.ReadAllBytes(paths.ClaudeConfigPath));
        Assert.NotNull(root["preferences"]);
        Assert.NotNull(root["someFutureKey"]);
        Assert.Single(service.Backups.Backups("claude_desktop_config"));
        Assert.True(File.Exists(Path.Combine(service.Backups.BackupsDir, "claude_desktop_config.original.json")));
    }

    [Fact]
    public void SaveStoreBacksUpPreviousVersion()
    {
        var store = service.LoadAndReconcile().Store;
        service.SaveStore(store);
        Assert.Single(service.Backups.Backups("mcps"));
    }

    [Fact]
    public void SaveCollectionsBacksUpTheSidecarAndLoadsItBack()
    {
        var file = new CollectionsFile([
            new KeyValuePair<string, CollectionsFile.Entry>("X", new CollectionsFile.Entry(CollectionKind.Synced, fileName: "x.json")),
        ]);
        service.SaveCollections(file);
        service.SaveCollections(new CollectionsFile([]));
        Assert.Equal(new CollectionsFile([]), service.LoadCollections());
        // the first save had nothing to back up; the second backed up the first
        Assert.Single(service.Backups.Backups("collections"));
    }

    [Fact]
    public void ApplyServersWritesExactlyWhatItIsGiven()
    {
        service.Apply(new Dictionary<string, JsonValue> { ["a"] = JsonValue.Object(("command", JsonValue.String("x"))) });
        Assert.Equal(new Dictionary<string, JsonValue> { ["a"] = JsonValue.Object(("command", JsonValue.String("x"))) },
            ClaudeConfigIO.ReadMcpServers(paths.ClaudeConfigPath));
    }

    [Fact]
    public void WipeRecoveryFlow()
    {
        var store = service.LoadAndReconcile().Store;
        File.WriteAllText(paths.ClaudeConfigPath, "{\"preferences\": {}}");   // issue #32345 shape
        var result = service.LoadAndReconcile();
        Assert.Equal(3, result.Store.Mcps.Count);
        Assert.NotEqual(result.Store.EnabledServers, result.ClaudeServers);   // divergence visible to the caller
        service.Apply(store.EnabledServers);
        Assert.Equal(3, ClaudeConfigIO.ReadMcpServers(paths.ClaudeConfigPath).Count);
    }

    [Fact]
    public void CorruptMasterStoreIsRebuiltWithNote()
    {
        service.LoadAndReconcile();
        File.WriteAllText(paths.MasterStorePath, "garbage");
        var result = service.LoadAndReconcile();
        Assert.Equal(3, result.Store.Mcps.Count);   // rebuilt from Claude's config
        Assert.Single(result.Notes);
        Assert.StartsWith("The MCP list file was unreadable; it was preserved as mcps.corrupt.", result.Notes[0], StringComparison.Ordinal);
        Assert.EndsWith(".json and rebuilt from Claude's config.", result.Notes[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A master list that cannot be read comes back from the newest <c>mcps</c> backup that can, so
    /// every collection it held survives; only with no such backup is it rebuilt from Claude's
    /// config. The unreadable file is kept aside either way.
    /// </summary>
    [Fact]
    public void ACorruptStoreIsRestoredFromTheNewestBackupThatDecodes()
    {
        var saved = service.LoadAndReconcile().Store;
        saved.Collections["Team"] = saved.Collections["Default"].Clone();
        service.SaveStore(saved);            // backs up the first store, which has no Team
        saved.Collections["Team"].Mcps.Remove("scoutbook");
        service.SaveStore(saved);            // backs up the store with Team in it
        var backups = service.Backups.Backups("mcps");
        Assert.Equal(2, backups.Count);
        var withTeam = MasterStoreIO.Read(backups[0]);
        Assert.True(withTeam!.Collections.ContainsKey("Team"));
        File.WriteAllText(paths.MasterStorePath, "garbage");

        var result = service.LoadAndReconcile();
        // The newest backup, with every collection it held, and it was saved.
        Assert.Equal(withTeam, result.Store);
        Assert.Equal(withTeam, MasterStoreIO.Read(paths.MasterStorePath));
        var aside = Path.GetFileName(Assert.Single(Directory.GetFiles(paths.StoreDir, "mcps.corrupt.*")));
        Assert.Equal("garbage", File.ReadAllText(Path.Combine(paths.StoreDir, aside)));
        var taken = BackupManager.TakenAt(backups[0]);
        Assert.NotNull(taken);
        Assert.Equal([$"The MCP list file was unreadable; it was preserved as {aside} and restored "
                      + $"from the backup of {IsoTimestamp.LocalDateTime(taken.Value)}."], result.Notes);
    }

    [Fact]
    public void ACorruptStoreSkipsANewestBackupThatIsCorruptToo()
    {
        var saved = service.LoadAndReconcile().Store;
        saved.Collections["Team"] = saved.Collections["Default"].Clone();
        service.SaveStore(saved);            // backs up the first store, which has no Team
        service.SaveStore(saved);            // backs up the store with Team in it
        var backups = service.Backups.Backups("mcps");
        Assert.Equal(2, backups.Count);
        var older = MasterStoreIO.Read(backups[1]);
        File.WriteAllText(backups[0], "{\"half");
        File.WriteAllText(paths.MasterStorePath, "garbage");

        var result = service.LoadAndReconcile();
        // The newest backup that decodes, which here is the older one.
        Assert.Equal(older, result.Store);
        Assert.False(result.Store.Collections.ContainsKey("Team"));
        var taken = BackupManager.TakenAt(backups[1]);
        Assert.NotNull(taken);
        Assert.EndsWith($" and restored from the backup of {IsoTimestamp.LocalDateTime(taken.Value)}.", result.Notes[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A backup whose active collection names one it does not hold — a hand edit, a foreign
    /// machine's — makes the first existing collection in ordinal order active, and the reconcile
    /// takes Claude's connectors there rather than into a new collection under the missing name.
    /// </summary>
    [Fact]
    public void ARestoredBackupWhoseActiveCollectionIsMissingActivatesTheOrdinalFirst()
    {
        var saved = service.LoadAndReconcile().Store;
        var ghost = new MasterStore(MasterStore.CurrentVersion, "～ Team", [
            new KeyValuePair<string, Collection>("～ Team", new Collection()),
            new KeyValuePair<string, Collection>("\U0001F600 Team", new Collection(saved.Mcps)),
        ]);
        ghost.ActiveCollection = "Ghost";
        service.SaveStore(ghost);
        service.SaveStore(ghost);            // backs up the store naming Ghost
        File.WriteAllText(paths.MasterStorePath, "garbage");

        var result = service.LoadAndReconcile();
        Assert.Equal("\U0001F600 Team", result.Store.ActiveCollection);
        // No collection is created.
        Assert.Equal(["\U0001F600 Team", "～ Team"], result.Store.Collections.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new Collection(saved.Mcps), result.Store.Collections["\U0001F600 Team"]);
    }

    [Fact]
    public void ACorruptStoreWithNoBackupThatDecodesIsRebuiltFromClaudesConfig()
    {
        var saved = service.LoadAndReconcile().Store;
        saved.Collections["Team"] = saved.Collections["Default"].Clone();
        service.SaveStore(saved);
        foreach (var backup in service.Backups.Backups("mcps"))
        {
            File.WriteAllText(backup, "garbage");
        }
        File.WriteAllText(paths.MasterStorePath, "garbage");

        var result = service.LoadAndReconcile();
        // Rebuilt from Claude's config.
        Assert.Equal(["Default"], result.Store.Collections.Keys);
        Assert.Equal(3, result.Store.Mcps.Count);
        Assert.EndsWith(".json and rebuilt from Claude's config.", result.Notes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptStoreAndMalformedClaudeConfigBothNotesSurface()
    {
        service.LoadAndReconcile();
        File.WriteAllText(paths.MasterStorePath, "garbage");
        File.WriteAllText(paths.ClaudeConfigPath, "{oops");
        var result = service.LoadAndReconcile();
        // Both sentences, in reconcile order; the second is the one that says what to do.
        Assert.Equal(2, result.Notes.Count);
        Assert.StartsWith("The MCP list file was unreadable; it was preserved as mcps.corrupt.", result.Notes[0], StringComparison.Ordinal);
        Assert.EndsWith(".json and rebuilt from Claude's config.", result.Notes[0], StringComparison.Ordinal);
        Assert.Equal("Claude’s config file is not valid JSON. Your MCP list is safe; use Backups ▸ Restore to repair the file.", result.Notes[1]);
    }

    [Fact]
    public void RestoreClaudeConfigFromBackup()
    {
        var store = service.LoadAndReconcile().Store;
        store.Mcps["aws-mcp"] = store.Mcps["aws-mcp"] with { Enabled = false };
        service.Apply(store.EnabledServers);
        var backup = service.Backups.Backups("claude_desktop_config")[0];
        service.RestoreClaudeConfig(backup, store);
        Assert.Equal(3, ClaudeConfigIO.ReadMcpServers(paths.ClaudeConfigPath).Count);
    }

    [Fact]
    public void RestoreClaudeConfigAdoptsSnapshotIntoStore()
    {
        var store = service.LoadAndReconcile().Store;
        store.Mcps["aws-mcp"] = store.Mcps["aws-mcp"] with { Enabled = false };
        service.Apply(store.EnabledServers);
        var backup = service.Backups.Backups("claude_desktop_config")[0];
        service.RestoreClaudeConfig(backup, store);
        var persisted = MasterStoreIO.Load(paths.MasterStorePath).Store;
        Assert.True(persisted.Mcps["aws-mcp"].Enabled);
        Assert.Equal(persisted.EnabledServers, ClaudeConfigIO.ReadMcpServers(paths.ClaudeConfigPath));
    }

    [Fact]
    public void RestoreDisablesEntriesAbsentFromSnapshot()
    {
        var store = service.LoadAndReconcile().Store;
        var snapshot = dir.File("snap.json");
        File.WriteAllText(snapshot, "{\"mcpServers\": {\"scoutbook\": {\"command\": \"npx\"}}}");
        service.RestoreClaudeConfig(snapshot, store);
        var persisted = MasterStoreIO.Load(paths.MasterStorePath).Store;
        Assert.Equal(3, persisted.Mcps.Count);
        Assert.False(persisted.Mcps["aws-mcp"].Enabled);
        Assert.False(persisted.Mcps["service-now"].Enabled);
        Assert.True(persisted.Mcps["scoutbook"].Enabled);
        Assert.Equal(JsonValue.Object(("command", JsonValue.String("npx"))), persisted.Mcps["scoutbook"].Config);
    }

    [Fact]
    public void MalformedClaudeConfigStillReturnsStore()
    {
        var first = service.LoadAndReconcile();
        Assert.Equal(3, first.Store.Mcps.Count);
        var backupsBefore = service.Backups.Backups("mcps").Count;
        File.WriteAllText(paths.ClaudeConfigPath, "{oops");
        var result = service.LoadAndReconcile();
        Assert.Equal(3, result.Store.Mcps.Count);
        Assert.Single(result.Notes);
        Assert.Equal("Claude’s config file is not valid JSON. Your MCP list is safe; use Backups ▸ Restore to repair the file.", result.Notes[0]);
        Assert.Null(result.ClaudeServers);
        Assert.Equal(backupsBefore, service.Backups.Backups("mcps").Count);
    }

    [Fact]
    public void KeepCountIsHonored()
    {
        var limited = new ConfigService(paths, keepCount: 2);
        limited.LoadAndReconcile();
        var baseTime = DateTime.UtcNow;
        for (int i = 0; i < 3; i++)
        {
            File.WriteAllText(paths.MasterStorePath, $"v{i}");
            limited.Backups.BackUp(paths.MasterStorePath, "mcps", baseTime.AddSeconds(i));
        }
        Assert.Equal(2, limited.Backups.Backups("mcps").Count);
    }

    [Fact]
    public void StoreAuthoritativeReconcileKeepsAdoptedStore()
    {
        service.LoadAndReconcile();
        var adopted = MasterStore.Empty();
        adopted.Mcps["scoutbook"] = new McpEntry(true, JsonValue.Object(("command", JsonValue.String("changed"))));
        MasterStoreIO.Save(adopted, paths.MasterStorePath);
        var backupsBefore = service.Backups.Backups("mcps").Count;
        var result = service.LoadAndReconcile(storeAuthoritative: true);
        Assert.Single(result.Store.Mcps);
        Assert.Equal(JsonValue.Object(("command", JsonValue.String("changed"))), result.Store.Mcps["scoutbook"].Config);
        Assert.Equal(backupsBefore, service.Backups.Backups("mcps").Count);   // no persist churn
    }

    [Fact]
    public void StoreAuthoritativeIngestsAdditionUnknownToBaseline()
    {
        var first = service.LoadAndReconcile();
        var adopted = MasterStore.Empty();
        adopted.Mcps["scoutbook"] = first.Store.Mcps["scoutbook"];
        MasterStoreIO.Save(adopted, paths.MasterStorePath);
        var baseline = first.ClaudeServers!;
        var newcomer = JsonValue.Object(("command", JsonValue.String("installer-added")));
        var fileServers = new Dictionary<string, JsonValue>(baseline) { ["newcomer"] = newcomer };
        ClaudeConfigIO.Write(fileServers, paths.ClaudeConfigPath);
        var result = service.LoadAndReconcile(baseline, storeAuthoritative: true);
        Assert.Equal(newcomer, result.Store.Mcps["newcomer"].Config);
        Assert.False(result.Store.Mcps.ContainsKey("aws-mcp"));
    }

    [Fact]
    public void RestoreReturnsRestoredServers()
    {
        var store = service.LoadAndReconcile().Store;
        service.Apply(store.EnabledServers);
        var backup = service.Backups.Backups("claude_desktop_config")[0];
        var servers = service.RestoreClaudeConfig(backup, store);
        Assert.Equal(ClaudeConfigIO.ReadMcpServers(paths.ClaudeConfigPath), servers);
    }

    [Fact]
    public void CorruptStoreMidSessionStillReimportsEverything()
    {
        var first = service.LoadAndReconcile();
        File.WriteAllText(paths.MasterStorePath, "garbage");
        var result = service.LoadAndReconcile(first.ClaudeServers);
        Assert.Equal(3, result.Store.Mcps.Count);
    }

    [Fact]
    public void RestoreRefusesWrongTypedMcpServersBeforeWriting()
    {
        service.LoadAndReconcile();
        var bad = dir.File("bad-servers.json");
        File.WriteAllText(bad, "{\"mcpServers\": \"oops\"}");
        var before = File.ReadAllBytes(paths.ClaudeConfigPath);
        var ex = Assert.Throws<ClaudeConfigException>(() => service.RestoreClaudeConfig(bad, MasterStore.Empty()));
        Assert.Equal("backup bad-servers.json has an invalid mcpServers section", ex.Detail);
        Assert.Equal(before, File.ReadAllBytes(paths.ClaudeConfigPath));
    }

    [Fact]
    public void RestoreRefusesMalformedBackup()
    {
        var store = service.LoadAndReconcile().Store;
        var bad = dir.File("bad-backup.json");
        File.WriteAllText(bad, "{not json");
        var before = File.ReadAllBytes(paths.ClaudeConfigPath);
        var ex = Assert.Throws<ClaudeConfigException>(() => service.RestoreClaudeConfig(bad, store));
        // The parenthesised detail is .NET's own JSON error text, as the Mac's is Foundation's.
        Assert.Equal(
            "backup bad-backup.json is not a valid config file "
                + "('n' is an invalid start of a property name. Expected a '\"'. LineNumber: 0 | BytePositionInLine: 1.)",
            ex.Detail);
        Assert.Equal(before, File.ReadAllBytes(paths.ClaudeConfigPath));
    }

    /// <summary>
    /// ParseRoot treats zero bytes as an empty root (the same rule ClaudeConfigIO's own read path
    /// already applies), so a zero-byte backup — the same crash/truncation artifact — restores to
    /// an empty config instead of being refused as malformed.
    /// </summary>
    [Fact]
    public void RestoreFromAnEmptyBackupTreatsItAsAnEmptyConfig()
    {
        var store = service.LoadAndReconcile().Store;
        var empty = dir.File("empty-backup.json");
        File.WriteAllBytes(empty, []);
        var servers = service.RestoreClaudeConfig(empty, store);
        Assert.Empty(servers);
        Assert.Empty(ClaudeConfigIO.ReadMcpServers(paths.ClaudeConfigPath));
    }
}

internal static class ConfigServiceLoads
{
    /// <summary>
    /// A load that reads the sidecar itself, as the app's first load does. The app passes the file it
    /// read, or the one it already holds (<c>AppState.Reload</c>).
    /// </summary>
    public static LoadResult LoadAndReconcile(
        this ConfigService service,
        IReadOnlyDictionary<string, JsonValue>? baseline = null,
        bool storeAuthoritative = false,
        string? lastAppliedCollection = null,
        IReadOnlySet<string>? lastAppliedNames = null) =>
        service.LoadAndReconcile(service.LoadCollections(), baseline, storeAuthoritative, lastAppliedCollection, lastAppliedNames);
}
