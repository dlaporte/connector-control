import Foundation

public struct ReconcileOutcome: Equatable {
    public var store: MasterStore
    /// Names enabled in the store but absent from Claude's file — the
    /// "Claude wiped my config" recovery flag. Sorted for stable display.
    public var missingEnabled: [String]
    public var storeChanged: Bool
}

public enum Reconciler {
    public static func reconcile(
        store: MasterStore, claudeServers: [String: JSONValue]
    ) -> ReconcileOutcome {
        var result = store
        var changed = false

        for (name, config) in claudeServers {
            if var entry = result.mcps[name] {
                if entry.config != config { entry.config = config; changed = true }
                if !entry.enabled { entry.enabled = true; changed = true }
                result.mcps[name] = entry
            } else {
                result.mcps[name] = MCPEntry(enabled: true, config: config)
                changed = true
            }
        }

        let missing = store.mcps
            .filter { $0.value.enabled && claudeServers[$0.key] == nil }
            .keys.sorted()

        return ReconcileOutcome(store: result, missingEnabled: missing,
                                storeChanged: changed)
    }
}
