namespace ConnectorControl.Core;

/// <summary>
/// The store is the source of truth; Claude's config is downstream of it.
/// Reconciliation therefore performs exactly one file→store flow: ingesting
/// entries the store has never heard of (installer scripts and hand-edits
/// writing straight into claude_desktop_config.json). Known entries are never
/// modified by the file — edits, re-adds of disabled connectors, and removals
/// are all resolved by the caller regenerating the file from
/// <see cref="MasterStore.EnabledServers"/>.
/// </summary>
public static class Reconciler
{
    /// <param name="directory">
    /// The folder <c>${COLLECTION_DIR}</c> stands for in the active collection on this machine,
    /// which Claude's file holds in the token's place. An ingested config has it written back as the
    /// token, so a published collection never takes the author's own folder in.
    /// </param>
    public static ReconcileOutcome Reconcile(
        MasterStore store,
        IReadOnlyDictionary<string, JsonValue> claudeServers,
        IReadOnlyDictionary<string, JsonValue>? baseline = null,
        string? directory = null)
    {
        var result = store.Clone();
        bool changed = false;
        foreach (var (name, config) in claudeServers)
        {
            if (result.Mcps.ContainsKey(name))
            {
                continue;
            }
            if (IsExternalAddition(name, config, baseline))
            {
                result.Mcps[name] = new McpEntry(true, Collapsed(config, directory));
                changed = true;
            }
            // else: matches the baseline but is gone from the store — a PENDING
            // REMOVAL awaiting Apply. Re-importing would resurrect a deletion.
        }
        return new ReconcileOutcome(result, changed);
    }

    /// <summary>
    /// A file entry unknown to the store is imported only when it's genuinely
    /// external: no baseline (fresh launch) or an entry that differs from the baseline.
    /// </summary>
    private static bool IsExternalAddition(string name, JsonValue config, IReadOnlyDictionary<string, JsonValue>? baseline) =>
        baseline is null || !baseline.TryGetValue(name, out var known) || known != config;

    /// <summary>
    /// Adopts a deliberately restored Claude-config snapshot INTO the store —
    /// the one case where the file legitimately rewrites store truth, because
    /// the user chose that snapshot. Entries in the snapshot are upserted
    /// (config, enabled; view memory preserved for known names); known entries
    /// absent from it are disabled, never deleted. The result renders exactly
    /// the snapshot, so no divergence survives the restore.
    /// </summary>
    /// <param name="directory">
    /// As <see cref="Reconcile"/> takes it: a config whose store copy already expands to what the
    /// snapshot holds keeps the store copy, token and all, and any other has the folder written back
    /// as the token. Every apply backs Claude's file up first, so a snapshot taken while a collection
    /// publishes holds the author's folder, and adopting it as written would publish it.
    /// </param>
    public static ReconcileOutcome AdoptSnapshot(MasterStore store, IReadOnlyDictionary<string, JsonValue> servers,
                                                 string? directory = null)
    {
        var result = store.Clone();
        var toDisable = result.Mcps
            .Where(p => p.Value.Enabled && !servers.ContainsKey(p.Key))
            .Select(p => p.Key)
            .ToList();
        foreach (var name in toDisable)
        {
            result.Mcps[name] = result.Mcps[name] with { Enabled = false };
        }
        foreach (var (name, config) in servers)
        {
            var held = result.Mcps.TryGetValue(name, out var existing);
            var entry = held ? existing! : new McpEntry(true, config);
            // The store's copy already renders as the snapshot does: it keeps its token.
            var kept = held && directory is not null && Placeholder.ExpandDirectoryToken(entry.Config, directory) == config;
            result.Mcps[name] = entry with { Config = kept ? entry.Config : Collapsed(config, directory), Enabled = true };
        }
        return new ReconcileOutcome(result, !result.Equals(store));
    }

    private static JsonValue Collapsed(JsonValue config, string? directory) =>
        directory is null ? config : Placeholder.CollapseDirectory(config, directory);
}
