namespace ConnectorControl.Core;

/// <summary>
/// Orchestrates every stateful operation, guaranteeing the backup-before-write
/// invariant. The UI layer calls only this type for file operations.
/// </summary>
public sealed class ConfigService
{
    public AppPaths Paths { get; }
    public BackupManager Backups { get; }

    public ConfigService(AppPaths paths, int keepCount = BackupManager.DefaultKeepCount)
    {
        Paths = paths;
        Backups = new BackupManager(paths.BackupsDir, keepCount);
    }

    /// <summary>
    /// Load the master store (handling corruption), read Claude's servers,
    /// reconcile, persist the store if reconciliation changed it.
    /// </summary>
    /// <remarks>
    /// The master store is loaded FIRST so it is always available: if Claude's
    /// config turns out to be malformed, reconciliation is skipped entirely
    /// (nothing is written) and the store just loaded is returned as-is, so the
    /// UI keeps showing the user's MCP list instead of going blank.
    /// With <paramref name="storeAuthoritative"/>, the file's own current
    /// servers act as the baseline, so every reconciliation rule resolves
    /// store-wins — used when adopting a pre-existing (e.g. synced) store that
    /// must not be overwritten by this machine's state.
    /// </remarks>
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
        // A corrupt store is rebuilt with fresh-launch (null-baseline) import
        // semantics: reconciling the empty replacement against a baseline would
        // classify every server as a pending removal, rebuild an empty list,
        // and set up the next apply to wipe Claude's config.
        IReadOnlyDictionary<string, JsonValue>? effectiveBaseline;
        if (corruptPath is not null)
        {
            effectiveBaseline = null;
        }
        else if (storeAuthoritative)
        {
            // The caller's baseline (last-applied servers) still classifies
            // additions correctly during an adoption: an entry matching it is
            // this machine's own applied state (never imported into the
            // adopted store), one differing from it is a genuine external
            // addition racing the adoption — ingest it rather than letting the
            // regeneration erase it. Without a baseline (adoption of a
            // repointed store before any apply), the file itself is the
            // baseline: nothing is imported, the adopted store wins totally.
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

    /// <summary>Backup mcps.json (if present), then atomically save the store. Reports whether the file is owner-only.</summary>
    public AtomicWriteResult SaveStore(MasterStore store)
    {
        Backups.BackUp(Paths.MasterStorePath, "mcps");
        return MasterStoreIO.Save(store, Paths.MasterStorePath);
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
            root = ClaudeConfigIO.ParseRoot(data);
        }
        catch (ClaudeConfigException ex)
        {
            throw new ClaudeConfigException($"backup {name} is not a valid config file ({ex.Detail})");
        }
        var rawServers = root["mcpServers"];
        if (rawServers is not null && rawServers.Kind != JsonKind.Object)
        {
            throw new ClaudeConfigException($"backup {name} has an invalid mcpServers section");
        }
        Backups.BackUp(Paths.ClaudeConfigPath, "claude_desktop_config");
        AtomicFile.Write(data, Paths.ClaudeConfigPath);
        // The bytes just written are what was already parsed above — reading the servers back off
        // the disk file would just reparse the same bytes a second time.
        IReadOnlyDictionary<string, JsonValue> servers = rawServers is null
            ? new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            : rawServers.ObjectProperties;
        var outcome = Reconciler.AdoptSnapshot(store, servers);
        if (outcome.StoreChanged)
        {
            SaveStore(outcome.Store);
        }
        return servers;
    }
}
