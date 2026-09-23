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
    ///
    /// <paramref name="lastAppliedCollection"/> is the collection Claude's file was last written from
    /// on this machine, and <paramref name="lastAppliedNames"/> the connector names that apply wrote.
    /// Once the active collection has changed elsewhere, the names that collection renders are left
    /// where they are rather than poured into the active one; everything else the file holds is
    /// still taken in (<see cref="Ingestible"/>). The caller then applies the active collection over
    /// the file.
    /// </remarks>
    public LoadResult LoadAndReconcile(
        IReadOnlyDictionary<string, JsonValue>? baseline = null,
        bool storeAuthoritative = false,
        string? lastAppliedCollection = null,
        IReadOnlySet<string>? lastAppliedNames = null)
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
                + "use Backups ▸ Restore to repair the file.");
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
        var outcome = Reconciler.Reconcile(
            store, Ingestible(servers, lastAppliedCollection, lastAppliedNames, corruptPath is not null, store),
            effectiveBaseline);
        if (outcome.StoreChanged || corruptPath is not null)
        {
            SaveStore(outcome.Store);
        }
        return new LoadResult(outcome.Store, notes, servers);
    }

    /// <summary>
    /// What a load may take out of Claude's file and into the active collection.
    /// <para>
    /// All of it while the record of the last apply is the active collection, is missing — a first
    /// launch — or the store was corrupt and is being rebuilt from the file. Otherwise the file holds
    /// another collection's connectors, and the names that collection renders are left alone:
    /// pouring them into the active collection is what the record is for. Everything else is
    /// genuinely new — an installer's connector, a hand edit — and belongs to the collection the app
    /// is about to apply, whichever that is.
    /// </para>
    /// <para>
    /// A record naming a collection the store no longer has — deleted here, or on another machine,
    /// which is how a collection disappears from a store that syncs — has no render to compare
    /// against. The names the last apply wrote are that render, so those are left alone and
    /// everything else comes in: a connector an installer or a hand edit added survives, and a
    /// deleted collection's own connectors are not poured into the active one. Without those names —
    /// a cache written before they were kept — nothing here tells the two apart, and nothing is taken
    /// in rather than all of it.
    /// </para>
    /// </summary>
    internal static IReadOnlyDictionary<string, JsonValue> Ingestible(
        IReadOnlyDictionary<string, JsonValue> servers, string? lastApplied, IReadOnlySet<string>? lastAppliedNames,
        bool corrupt, MasterStore store)
    {
        if (corrupt || lastApplied is null || string.Equals(lastApplied, store.ActiveCollection, StringComparison.Ordinal))
        {
            return servers;
        }
        if (!store.Collections.TryGetValue(lastApplied, out var collection))
        {
            // Nothing here says what that collection rendered, so nothing in the file can be told
            // from it: taking it all in would pour a deleted collection's connectors, marked paths
            // and all, into the active one. Taking none is the safe half of that trade, and costs
            // only a hand-added connector, in the one state that reaches it — a cache from a build
            // that recorded the collection without the names.
            return lastAppliedNames is null
                ? new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                : servers.Where(p => !lastAppliedNames.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        }
        var rendered = collection.Mcps.Where(p => p.Value.Enabled).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        return servers.Where(p => !rendered.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    /// <summary>Backup mcps.json (if present), then atomically save the store. Reports whether the file is owner-only.</summary>
    public AtomicWriteResult SaveStore(MasterStore store)
    {
        Backups.BackUp(Paths.MasterStorePath, "mcps");
        return MasterStoreIO.Save(store, Paths.MasterStorePath);
    }

    /// <summary>Snapshot original (first run), backup Claude's config, then write the given servers into it.</summary>
    /// <param name="backedUpFrom">
    /// The collection the file being backed up was last applied from, recorded against the backup
    /// (<see cref="BackupCollections"/>) so a restore of it goes back into that collection. A failed
    /// record never fails the apply: the backup then restores as an unrecorded one.
    /// </param>
    public void Apply(IReadOnlyDictionary<string, JsonValue> servers, string? backedUpFrom = null)
    {
        Backups.EnsureOriginalSnapshot(Paths.ClaudeConfigPath);
        RecordBackup(Backups.BackUp(Paths.ClaudeConfigPath, "claude_desktop_config"), backedUpFrom);
        ClaudeConfigIO.Write(servers, Paths.ClaudeConfigPath);
    }

    private void RecordBackup(string? backup, string? collection)
    {
        if (backup is null || collection is null)
        {
            return;
        }
        try
        {
            BackupCollections.Record(collection, backup, Paths.BackupsDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort, as the summary says: the backup itself is already written.
        }
    }

    /// <summary>The active collection's enabled subset — see <see cref="Apply(IReadOnlyDictionary{string,JsonValue},string?)"/>.</summary>
    public void Apply(MasterStore store) => Apply(store.EnabledServers);

    /// <summary>The sidecar beside the master list; a missing or unreadable file loads as empty (see <see cref="CollectionsFile.Load"/>).</summary>
    public CollectionsFile LoadCollections() => CollectionsFile.Load(Paths.CollectionsFilePath);

    /// <summary>Backup the existing sidecar (skipped when it doesn't exist yet), then atomically save the new one.</summary>
    public AtomicWriteResult SaveCollections(CollectionsFile file)
    {
        Backups.BackUp(Paths.CollectionsFilePath, "collections");
        return file.Save(Paths.CollectionsFilePath);
    }

    /// <summary>
    /// Backup the current file, copy the chosen backup over it, then adopt the
    /// snapshot into the store. The backup is validated BEFORE the live file is
    /// touched. Returns the restored file's servers (the caller's new baseline).
    /// </summary>
    /// <param name="publishFolder">
    /// The folder this machine publishes the active collection into; a connector whose store copy
    /// renders exactly as the snapshot keeps the store copy (<see cref="Reconciler.AdoptSnapshot"/>).
    /// </param>
    /// <param name="backedUpFrom">As <see cref="Apply(IReadOnlyDictionary{string,JsonValue},string?)"/> takes it: records the file this restore overwrites.</param>
    /// <param name="activating">
    /// The snapshot is adopted into <paramref name="store"/>'s active collection; the caller makes that
    /// the collection the backup was taken from, and says so here when that is not the collection the
    /// saved store has active.
    /// </param>
    /// <param name="earlierFolders">The folders it published into before, which <see cref="Reconciler.AdoptSnapshot"/> counts the same way.</param>
    public IReadOnlyDictionary<string, JsonValue> RestoreClaudeConfig(string backupPath, MasterStore store,
                                                                     string? publishFolder = null,
                                                                     IReadOnlyList<string>? earlierFolders = null,
                                                                     string? backedUpFrom = null,
                                                                     bool activating = false)
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
        RecordBackup(Backups.BackUp(Paths.ClaudeConfigPath, "claude_desktop_config"), backedUpFrom);
        AtomicFile.Write(data, Paths.ClaudeConfigPath);
        // The bytes just written are what was already parsed above — reading the servers back off
        // the disk file would just reparse the same bytes a second time.
        IReadOnlyDictionary<string, JsonValue> servers = rawServers is null
            ? new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            : rawServers.ObjectProperties;
        var outcome = Reconciler.AdoptSnapshot(store, servers, publishFolder, earlierFolders);
        // A backup restored into a collection other than the active one makes that collection
        // active, so Claude's file and the store agree on where its connectors live — even when the
        // adoption itself changed nothing.
        if (outcome.StoreChanged || activating)
        {
            SaveStore(outcome.Store);
        }
        return servers;
    }
}
