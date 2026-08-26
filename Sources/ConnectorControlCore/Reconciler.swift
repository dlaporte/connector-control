import Foundation

public struct ReconcileOutcome: Equatable {
    public var store: MasterStore
    public var storeChanged: Bool
}

/// The store is the source of truth; Claude's config is downstream of it.
/// Reconciliation therefore performs exactly one file→store flow: ingesting
/// entries the store has never heard of (installer scripts and hand-edits
/// writing straight into claude_desktop_config.json). Known entries are never
/// modified by the file — edits, re-adds of disabled connectors, and removals
/// are all resolved by the caller regenerating the file from
/// `store.enabledServers`.
public enum Reconciler {
    public static func reconcile(
        store: MasterStore, claudeServers: [String: JSONValue],
        baseline: [String: JSONValue]? = nil
    ) -> ReconcileOutcome {
        var result = store
        var changed = false

        for (name, config) in claudeServers where result.mcps[name] == nil {
            if isExternalAddition(name: name, config: config, baseline: baseline) {
                result.mcps[name] = MCPEntry(enabled: true, config: config)
                changed = true
            }
            // else: the entry matches the baseline but is gone from the store —
            // a PENDING REMOVAL awaiting Apply. Re-importing it here would
            // silently resurrect a connector the user just deleted.
        }

        return ReconcileOutcome(store: result, storeChanged: changed)
    }

    /// A file entry unknown to the store is imported only when it's genuinely
    /// external: no baseline (fresh launch) or an entry that differs from the
    /// baseline. When it matches the baseline exactly, the store-side absence
    /// means the user deleted it and Apply hasn't landed yet.
    private static func isExternalAddition(
        name: String, config: JSONValue, baseline: [String: JSONValue]?
    ) -> Bool {
        guard let baseline else { return true }
        return baseline[name] != config
    }

    /// Adopts a deliberately restored Claude-config snapshot INTO the store —
    /// the one case where the file legitimately rewrites store truth, because
    /// the user chose that snapshot. Entries in the snapshot are upserted
    /// (snapshot's config, enabled, view memory preserved for known names);
    /// known entries absent from it are disabled, never deleted. The result
    /// renders exactly the snapshot, so no divergence survives the restore.
    public static func adoptSnapshot(
        store: MasterStore, servers: [String: JSONValue]
    ) -> ReconcileOutcome {
        var result = store
        for (name, entry) in result.mcps where entry.enabled && servers[name] == nil {
            result.mcps[name]?.enabled = false
        }
        for (name, config) in servers {
            var entry = result.mcps[name] ?? MCPEntry(enabled: true, config: config)
            entry.config = config
            entry.enabled = true
            result.mcps[name] = entry
        }
        return ReconcileOutcome(store: result, storeChanged: result != store)
    }
}
