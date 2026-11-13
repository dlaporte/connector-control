import Foundation

/// What putting a collection back exactly as a backup holds it changes, connector by connector.
/// Names compare as the store compares them (`Collection`): a connector held under another spelling
/// of its name is the same connector. A connector that comes back or changes is named as the backup
/// spells it, one that goes as the store does.
///
/// Mirror: windows/src/ConnectorControl.Core/CollectionRestore.cs
public struct CollectionRestore: Equatable, Sendable {
    public enum Change: Equatable, Sendable {
        /// Not held now: the restore brings it back, on or off.
        case comesBack(on: Bool)
        /// Held now with another config or switch: on or off after the restore.
        case changes(on: Bool)
        /// Held now and not in the backup: the restore removes it.
        case goes
    }

    /// Every connector the restore brings back, changes or removes, by name.
    public let byName: [String: Change]
    /// How many connectors the restore leaves as they are.
    public let unchanged: Int

    /// True when the collection is already as the backup holds it.
    public var isEmpty: Bool { byName.isEmpty }

    /// `restored` against `current`, nil for a collection that is gone. A connector is unchanged
    /// when its config and its switch are: which view its editor last showed is not something a
    /// restore is asked to put back, so it alone never makes a collection differ.
    public static func changes(current: Collection?, restored: Collection) -> CollectionRestore {
        let held = current?.mcps ?? [:]
        var byName: [String: Change] = [:]
        var unchanged = 0
        for (name, entry) in restored.mcps {
            if let now = held[name] {
                if now.config == entry.config && now.enabled == entry.enabled {
                    unchanged += 1
                } else {
                    byName[name] = .changes(on: entry.enabled)
                }
            } else {
                byName[name] = .comesBack(on: entry.enabled)
            }
        }
        for name in held.keys where restored.mcps[name] == nil { byName[name] = .goes }
        return CollectionRestore(byName: byName, unchanged: unchanged)
    }
}
