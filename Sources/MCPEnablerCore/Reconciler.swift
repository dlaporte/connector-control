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
        store: MasterStore, claudeServers: [String: JSONValue],
        baseline: [String: JSONValue]? = nil
    ) -> ReconcileOutcome {
        var result = store
        var changed = false

        for (name, config) in claudeServers {
            if var entry = result.mcps[name] {
                if entry.config != config
                    && isExternalChange(name: name, config: config, baseline: baseline) {
                    entry.config = config
                    changed = true
                }
                if !entry.enabled && isExternalReappearance(
                    name: name, config: config, baseline: baseline) {
                    entry.enabled = true
                    changed = true
                }
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

    /// A disabled-in-store server found in Claude's file is a PENDING DISABLE
    /// (awaiting Apply) when the file entry matches what we last knew the file
    /// to contain. It is an external re-add only when it differs from — or is
    /// absent from — the last-known baseline. With no baseline (fresh launch),
    /// the user's disable intent is preserved.
    private static func isExternalReappearance(
        name: String, config: JSONValue, baseline: [String: JSONValue]?
    ) -> Bool {
        guard let baseline else { return false }
        return baseline[name] != config
    }

    /// A store/file config mismatch is a PENDING EDIT (awaiting Apply) when the
    /// file entry still matches the last-known baseline. The file wins only on
    /// evidence of an external change — or with no baseline (fresh launch),
    /// where hand-edits made while the app wasn't running take precedence.
    private static func isExternalChange(
        name: String, config: JSONValue, baseline: [String: JSONValue]?
    ) -> Bool {
        guard let baseline else { return true }
        return baseline[name] != config
    }
}
