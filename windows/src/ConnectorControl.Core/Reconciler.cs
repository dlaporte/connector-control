namespace ConnectorControl.Core;

/// <summary>
/// The store is the source of truth; Claude's config is downstream of it.
/// Reconciliation performs exactly one file→store flow: ingesting entries the
/// store has never heard of. Known entries are never modified by the file.
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
    /// Adopts a deliberately restored Claude-config snapshot INTO the store: snapshot
    /// entries are upserted (config, enabled; view memory preserved for known names);
    /// known entries absent from it are disabled, never deleted.
    /// </summary>
    public static ReconcileOutcome AdoptSnapshot(MasterStore store, IReadOnlyDictionary<string, JsonValue> servers)
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
            var entry = result.Mcps.TryGetValue(name, out var existing) ? existing : new McpEntry(true, config);
            result.Mcps[name] = entry with { Config = config, Enabled = true };
        }
        return new ReconcileOutcome(result, !result.Equals(store));
    }
}
