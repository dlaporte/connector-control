using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

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
        service.Apply(store);
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
    public void WipeRecoveryFlow()
    {
        var store = service.LoadAndReconcile().Store;
        File.WriteAllText(paths.ClaudeConfigPath, "{\"preferences\": {}}");   // issue #32345 shape
        var result = service.LoadAndReconcile();
        Assert.Equal(3, result.Store.Mcps.Count);
        Assert.NotEqual(result.Store.EnabledServers, result.ClaudeServers);   // divergence visible to the caller
        service.Apply(store);
        Assert.Equal(3, ClaudeConfigIO.ReadMcpServers(paths.ClaudeConfigPath).Count);
    }

    [Fact]
    public void CorruptMasterStoreIsRebuiltWithNote()
    {
        service.LoadAndReconcile();
        File.WriteAllText(paths.MasterStorePath, "garbage");
        var result = service.LoadAndReconcile();
        Assert.Equal(3, result.Store.Mcps.Count);
        Assert.Single(result.Notes);
        Assert.Contains("mcps.corrupt.", result.Notes[0]);
    }

    [Fact]
    public void CorruptStoreAndMalformedClaudeConfigBothNotesSurface()
    {
        service.LoadAndReconcile();
        File.WriteAllText(paths.MasterStorePath, "garbage");
        File.WriteAllText(paths.ClaudeConfigPath, "{oops");
        var result = service.LoadAndReconcile();
        Assert.Equal(2, result.Notes.Count);
        Assert.Contains(result.Notes, n => n.Contains("mcps.corrupt."));
        Assert.Contains(result.Notes, n => n.Contains("Backups"));
    }

    [Fact]
    public void NoteTextsMatchTheMacApp()
    {
        service.LoadAndReconcile();
        File.WriteAllText(paths.MasterStorePath, "garbage");
        File.WriteAllText(paths.ClaudeConfigPath, "{oops");
        var result = service.LoadAndReconcile();
        Assert.StartsWith("The MCP list file was unreadable; it was preserved as mcps.corrupt.", result.Notes[0], StringComparison.Ordinal);
        Assert.EndsWith(".json and rebuilt from Claude's config.", result.Notes[0], StringComparison.Ordinal);
        Assert.Equal("Claude's config file is not valid JSON. Your MCP list is safe; use Backups ▸ Restore… to repair the file.", result.Notes[1]);
    }

    [Fact]
    public void RestoreClaudeConfigFromBackup()
    {
        var store = service.LoadAndReconcile().Store;
        store.Mcps["aws-mcp"] = store.Mcps["aws-mcp"] with { Enabled = false };
        service.Apply(store);
        var backup = service.Backups.Backups("claude_desktop_config")[0];
        service.RestoreClaudeConfig(backup, store);
        Assert.Equal(3, ClaudeConfigIO.ReadMcpServers(paths.ClaudeConfigPath).Count);
    }

    [Fact]
    public void RestoreClaudeConfigAdoptsSnapshotIntoStore()
    {
        var store = service.LoadAndReconcile().Store;
        store.Mcps["aws-mcp"] = store.Mcps["aws-mcp"] with { Enabled = false };
        service.Apply(store);
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
        Assert.Contains("Backups", result.Notes[0]);
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
        service.Apply(store);
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
        Assert.Equal("backup bad-backup.json is not a valid config file", ex.Detail);
        Assert.Equal(before, File.ReadAllBytes(paths.ClaudeConfigPath));
    }
}
