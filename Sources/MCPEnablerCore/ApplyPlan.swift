import Foundation

/// Human-readable summary of what Apply would change in Claude's config:
/// the enabled subset of the master store vs. the file's current servers.
public enum ApplyPlan {
    public static func changes(
        store: MasterStore, current: [String: JSONValue]
    ) -> [String] {
        let desired = store.mcps.filter(\.value.enabled).mapValues(\.config)
        var lines: [String] = []
        for name in desired.keys.sorted() where current[name] == nil {
            lines.append("Add “\(name)”")
        }
        for name in current.keys.sorted() where desired[name] == nil {
            lines.append("Remove “\(name)”")
        }
        for name in desired.keys.sorted() {
            if let existing = current[name], existing != desired[name]! {
                lines.append("Update “\(name)”")
            }
        }
        return lines
    }
}
