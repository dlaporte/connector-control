import ConnectorControlCore

/// What a regenerated Claude config now runs that it did not before, by
/// connector name (catalog §1.8). An mcpServers entry is a command Claude
/// executes, so a notification about an adopted synced change names the
/// entries rather than saying only that "the list changed".
public struct ServerDelta: Equatable, Sendable {
    public var added: [String]
    public var removed: [String]
    public var changed: [String]

    public init(added: [String] = [], removed: [String] = [], changed: [String] = []) {
        self.added = added
        self.removed = removed
        self.changed = changed
    }

    public init(from before: [String: JSONValue], to after: [String: JSONValue]) {
        added = after.keys.filter { before[$0] == nil }.sorted()
        removed = before.keys.filter { after[$0] == nil }.sorted()
        changed = after.keys.filter { before[$0] != nil && before[$0] != after[$0] }.sorted()
    }

    public var isEmpty: Bool { added.isEmpty && removed.isEmpty && changed.isEmpty }

    /// "adds a, b; removes c; changes d" — at most `limit` names per part, then "and N more".
    public func summary(limit: Int = 4) -> String {
        var parts: [String] = []
        if !added.isEmpty { parts.append("adds " + ServerDelta.list(added, limit: limit)) }
        if !removed.isEmpty { parts.append("removes " + ServerDelta.list(removed, limit: limit)) }
        if !changed.isEmpty { parts.append("changes " + ServerDelta.list(changed, limit: limit)) }
        return parts.joined(separator: "; ")
    }

    static func list(_ names: [String], limit: Int) -> String {
        guard names.count > limit else { return names.joined(separator: ", ") }
        return names.prefix(limit).joined(separator: ", ") + " and \(names.count - limit) more"
    }
}
