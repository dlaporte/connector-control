public struct ReconcileOutcome {
    public var store: MasterStore
    public var storeChanged: Bool
    /// The names the file's additions are kept under, in the collection `reconcile` took them into.
    public var ingested: [String] = []
}

/// The store is the source of truth; Claude's config is downstream of it.
/// Reconciliation therefore performs exactly one file→store flow: ingesting
/// entries the store has never heard of (installer scripts and hand-edits
/// writing straight into claude_desktop_config.json). Known entries are never
/// modified by the file — edits, re-adds of disabled connectors, and removals
/// are all resolved by the caller regenerating the file from
/// `store.enabledServers`.
public enum Reconciler {
    /// `target` (nil: the active collection) is where the additions land. What counts as one is
    /// always measured against the active collection, the one Claude's file renders; another
    /// collection is created as the first addition lands in it, and what it already holds is never
    /// overwritten (`keptName(_:_:in:)`).
    public static func reconcile(
        store: MasterStore, claudeServers: [String: JSONValue],
        baseline: [String: JSONValue]? = nil, into target: String? = nil
    ) -> ReconcileOutcome {
        var result = store
        var changed = false
        var ingested: [String] = []
        let destination = target ?? store.activeCollection

        for (name, config) in claudeServers where result.mcps[name] == nil {
            if isExternalAddition(name: name, config: config, baseline: baseline) {
                let kept = keptName(name, config, in: result.collections[destination]?.mcps ?? [:])
                if result.collections[destination]?.mcps[kept] == nil {
                    result.collections[destination, default: Collection()].mcps[kept] = MCPEntry(enabled: true, config: config)
                    changed = true
                }
                ingested.append(kept)
            }
            // else: the entry matches the baseline but is gone from the store —
            // a PENDING REMOVAL awaiting Apply. Re-importing it here would
            // silently resurrect a connector the user just deleted.
        }

        return ReconcileOutcome(store: result, storeChanged: changed,
                                ingested: ingested.sorted { $0.ordinallyPrecedes($1) })
    }

    /// `name`, or "name 2", "name 3", … — the first name under which `held` has nothing or has
    /// this very config. The active collection never holds the name, so there it is `name`
    /// itself; another collection may hold a connector of its own under it, which stays as it is.
    private static func keptName(_ name: String, _ config: JSONValue, in held: [String: MCPEntry]) -> String {
        var candidate = name
        var suffix = 2
        while let entry = held[candidate], entry.config != config {
            candidate = "\(name) \(suffix)"
            suffix += 1
        }
        return candidate
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
    ///
    /// `publishFolder` is the folder this machine publishes the active collection into, which
    /// Claude's file holds where the store holds `${COLLECTION_DIR}`: every apply backs Claude's
    /// file up first, so a snapshot taken while the collection publishes holds the author's own
    /// folder. A connector whose store copy expands to exactly what the snapshot holds keeps the
    /// store copy, token and all. Anything else is the snapshot's own, a genuine edit, and is
    /// adopted as written; a publish that would carry the folder is refused further on.
    /// `earlierFolders`, the folders the collection published into before this one, count the
    /// same way: a backup taken before the folder moved holds the folder it had then.
    public static func adoptSnapshot(
        store: MasterStore, servers: [String: JSONValue], publishFolder: String? = nil,
        earlierFolders: [String] = []
    ) -> ReconcileOutcome {
        let folders = publishFolder.map { [$0] + earlierFolders } ?? []
        var result = store
        for (name, entry) in result.mcps where entry.enabled && servers[name] == nil {
            result.mcps[name]?.enabled = false
        }
        for (name, config) in servers {
            let held = result.mcps[name]
            var entry = held ?? MCPEntry(enabled: true, config: config)
            let rendersAsSnapshot = held.map { held in
                folders.contains { Placeholder.expandDirectoryToken(in: held.config, directory: $0) == config }
            } ?? false
            if !rendersAsSnapshot { entry.config = config }
            entry.enabled = true
            result.mcps[name] = entry
        }
        return ReconcileOutcome(store: result, storeChanged: result != store)
    }
}
