using System.Text.Json;

namespace ConnectorControl.Core;

/// <summary>
/// Orchestrates every stateful operation, guaranteeing the backup-before-write
/// invariant. The UI layer calls only this type for file operations.
/// </summary>
public sealed class ConfigService
{
    public AppPaths Paths { get; }
    public BackupManager Backups { get; }

    public ConfigService(AppPaths paths, int keepCount = 20)
    {
        Paths = paths;
        Backups = new BackupManager(paths.BackupsDir, keepCount);
    }

    /// <summary>
    /// Load the master store (handling corruption), read Claude's servers,
    /// reconcile, persist the store if reconciliation changed it. A malformed
    /// Claude config skips reconciliation entirely and returns the store as-is.
    /// With <paramref name="storeAuthoritative"/>, the file's own servers act as
    /// the baseline so every rule resolves store-wins (adopting a synced store).
    /// </summary>
    public LoadResult LoadAndReconcile(
        IReadOnlyDictionary<string, JsonValue>? baseline = null,
        bool storeAuthoritative = false)
    {
        var notes = new List<string>();
        var (store, corruptPath) = MasterStoreIO.Load(Paths.MasterStorePath);
        if (corruptPath is not null)
        {
            notes.Add("The MCP list file was unreadable; it was preserved as "
                + $"{Path.GetFileName(corruptPath)} and rebuilt from Claude's config.");
        }
        IReadOnlyDictionary<string, JsonValue> servers;
        try
        {
            servers = ClaudeConfigIO.ReadMcpServers(Paths.ClaudeConfigPath);
        }
        catch (ClaudeConfigException)
        {
            notes.Add("Claude's config file is not valid JSON. Your MCP list is safe; "
                + "use Backups ▸ Restore… to repair the file.");
            return new LoadResult(store, notes, null);
        }
        // A corrupt store is rebuilt with fresh-launch (null-baseline) import semantics.
        IReadOnlyDictionary<string, JsonValue>? effectiveBaseline;
        if (corruptPath is not null)
        {
            effectiveBaseline = null;
        }
        else if (storeAuthoritative)
        {
            effectiveBaseline = baseline ?? servers;
        }
        else
        {
            effectiveBaseline = baseline;
        }
        var outcome = Reconciler.Reconcile(store, servers, effectiveBaseline);
        if (outcome.StoreChanged || corruptPath is not null)
        {
            SaveStore(outcome.Store);
        }
        return new LoadResult(outcome.Store, notes, servers);
    }

    /// <summary>Backup mcps.json (if present), then atomically save the store.</summary>
    public void SaveStore(MasterStore store)
    {
        Backups.BackUp(Paths.MasterStorePath, "mcps");
        MasterStoreIO.Save(store, Paths.MasterStorePath);
    }

    /// <summary>Snapshot original (first run), backup Claude's config, then write the enabled subset into it.</summary>
    public void Apply(MasterStore store)
    {
        Backups.EnsureOriginalSnapshot(Paths.ClaudeConfigPath);
        Backups.BackUp(Paths.ClaudeConfigPath, "claude_desktop_config");
        ClaudeConfigIO.Write(store.EnabledServers, Paths.ClaudeConfigPath);
    }

    /// <summary>
    /// Backup the current file, copy the chosen backup over it, then adopt the
    /// snapshot into the store. The backup is validated BEFORE the live file is
    /// touched. Returns the restored file's servers (the caller's new baseline).
    /// </summary>
    public IReadOnlyDictionary<string, JsonValue> RestoreClaudeConfig(string backupPath, MasterStore store)
    {
        var data = File.ReadAllBytes(backupPath);
        var name = Path.GetFileName(backupPath);
        JsonValue root;
        try
        {
            root = JsonValue.Parse(data);
        }
        catch (JsonException)
        {
            throw new ClaudeConfigException($"backup {name} is not a valid config file");
        }
        if (root.Kind != JsonKind.Object)
        {
            throw new ClaudeConfigException($"backup {name} is not a valid config file");
        }
        var rawServers = root["mcpServers"];
        if (rawServers is not null && rawServers.Kind != JsonKind.Object)
        {
            throw new ClaudeConfigException($"backup {name} has an invalid mcpServers section");
        }
        Backups.BackUp(Paths.ClaudeConfigPath, "claude_desktop_config");
        AtomicFile.Write(data, Paths.ClaudeConfigPath);
        var servers = ClaudeConfigIO.ReadMcpServers(Paths.ClaudeConfigPath);
        var outcome = Reconciler.AdoptSnapshot(store, servers);
        if (outcome.StoreChanged)
        {
            SaveStore(outcome.Store);
        }
        return servers;
    }
}
