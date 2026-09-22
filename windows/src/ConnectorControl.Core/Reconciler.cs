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
    public static ReconcileOutcome Reconcile(
        MasterStore store,
        IReadOnlyDictionary<string, JsonValue> claudeServers,
        IReadOnlyDictionary<string, JsonValue>? baseline = null)
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
                result.Mcps[name] = new McpEntry(true, config);
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
    /// <param name="publishFolder">
    /// The folder this machine publishes the active collection into, which Claude's file holds where
    /// the store holds <c>${COLLECTION_DIR}</c>: every apply backs Claude's file up first, so a
    /// snapshot taken while the collection publishes holds the author's own folder. A connector
    /// whose store copy expands to exactly what the snapshot holds keeps the store copy, token and
    /// all. Anything else is the snapshot's own, a genuine edit, and is adopted as written; a publish
    /// that would carry the folder is refused further on.
    /// </param>
    public static ReconcileOutcome AdoptSnapshot(MasterStore store, IReadOnlyDictionary<string, JsonValue> servers,
                                                 string? publishFolder = null)
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
            var rendersAsSnapshot = held && publishFolder is not null
                && Placeholder.ExpandDirectoryToken(entry.Config, publishFolder) == config;
            result.Mcps[name] = entry with { Config = rendersAsSnapshot ? entry.Config : config, Enabled = true };
        }
        return new ReconcileOutcome(result, !result.Equals(store));
    }
}
