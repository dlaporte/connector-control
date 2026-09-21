/// What a synced collection's source would change, and how the change lands.
///
/// Mirror: windows/src/ConnectorControl.Core/CollectionDiff.cs
public struct CollectionDiff: Equatable, Sendable {
    public var added: [String]
    public var removed: [String]
    public var changed: [String]
    public var isEmpty: Bool { added.isEmpty && removed.isEmpty && changed.isEmpty }

    public init(added: [String], removed: [String], changed: [String]) {
        self.added = added
        self.removed = removed
        self.changed = changed
    }

    /// `rendered` vs `current`, ignoring enabled flags; a rendered string leaf that contains a
    /// marker matches whatever string the current config has at that pointer. A connector this
    /// platform excludes never appears in `rendered.connectors`, so it never shows as added.
    public static func pending(rendered: RenderedCollection, current: [String: MCPEntry]) -> CollectionDiff {
        let renderedNames = Set(rendered.connectors.keys)
        let currentNames = Set(current.keys)
        var changed: [String] = []
        for name in renderedNames.intersection(currentNames) {
            if !matches(rendered: rendered.connectors[name]!.config, current: current[name]!.config) { changed.append(name) }
        }
        return CollectionDiff(added: renderedNames.subtracting(currentNames).sorted(),
                              removed: currentNames.subtracting(renderedNames).sorted(),
                              changed: changed.sorted())
    }

    /// Equal after every marker-bearing leaf of `rendered` is replaced by whatever string
    /// `current` holds at the same pointer; a missing leaf on the current side is a change.
    static func matches(rendered: JSONValue, current: JSONValue) -> Bool {
        var normalized = rendered
        for (pointer, _) in Placeholder.markers(in: rendered) {
            guard case .string(let filled)? = current.value(at: pointer) else { return false }
            normalized = normalized.replacing(at: pointer, with: .string(filled)) ?? normalized
        }
        return normalized == current
    }

    /// "adds a, b; removes c; changes d" — same shape as `ServerDelta.summary`, kept as its own
    /// copy here since Core does not depend on the State module that type lives in.
    public func summary(limit: Int = 4) -> String {
        var parts: [String] = []
        if !added.isEmpty { parts.append("adds " + Self.list(added, limit: limit)) }
        if !removed.isEmpty { parts.append("removes " + Self.list(removed, limit: limit)) }
        if !changed.isEmpty { parts.append("changes " + Self.list(changed, limit: limit)) }
        return parts.joined(separator: "; ")
    }

    static func list(_ names: [String], limit: Int) -> String {
        names.count <= limit ? names.joined(separator: ", ") : names.prefix(limit).joined(separator: ", ") + " and \(names.count - limit) more"
    }
}

public struct CollectionApplyResult: Equatable, Sendable {
    public var entries: [String: MCPEntry]
    public var needs: [String: [String: CollectionsFile.Need]]

    public init(entries: [String: MCPEntry], needs: [String: [String: CollectionsFile.Need]]) {
        self.entries = entries
        self.needs = needs
    }
}

/// The new content of a synced collection: rendered configs with the user's filled values carried
/// by marker name (found through the previous needs' pointers), enabled flags kept, added
/// connectors disabled.
public enum CollectionApply {
    public static func apply(rendered: RenderedCollection, current: [String: MCPEntry],
                             previousNeeds: [String: [String: CollectionsFile.Need]]) -> CollectionApplyResult {
        var entries: [String: MCPEntry] = [:]
        var needs: [String: [String: CollectionsFile.Need]] = [:]
        for (name, connector) in rendered.connectors {
            var config = connector.config
            var connectorNeeds: [String: CollectionsFile.Need] = [:]
            // Sorted (ordinal) so the outcome never depends on dictionary iteration order, which
            // is unspecified on both platforms. Two marker names can render into the same leaf
            // (e.g. "${CC_NEEDS:a}-${CC_NEEDS:b}"); a leaf with two markers carries the
            // alphabetically first filled value; Publish assigns unique names, so this is a
            // tie-break, not a feature.
            for needName in connector.needs.keys.sorted() {
                let need = connector.needs[needName]!
                connectorNeeds[needName] = CollectionsFile.Need(hint: need.hint, pointer: need.pointer)
                // A value the user filled in under this name stays filled, wherever the marker
                // moved to: the previous needs record where it was. Skip a leaf an earlier
                // (sorted-first) need already carried a value into, so it isn't clobbered.
                guard case .string(let currentLeaf)? = config.value(at: need.pointer), Placeholder.containsMarker(currentLeaf) else { continue }
                if let previousPointer = previousNeeds[name]?[needName]?.pointer,
                   case .string(let filled)? = current[name]?.config.value(at: previousPointer),
                   !Placeholder.containsMarker(filled) {
                    config = config.replacing(at: need.pointer, with: .string(filled)) ?? config
                }
            }
            entries[name] = MCPEntry(enabled: current[name]?.enabled ?? false, config: config,
                                     lastEditView: current[name]?.lastEditView ?? .form)
            if !connectorNeeds.isEmpty { needs[name] = connectorNeeds }
        }
        return CollectionApplyResult(entries: entries, needs: needs)
    }
}
