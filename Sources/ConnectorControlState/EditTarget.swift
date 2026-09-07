import Foundation
import ConnectorControlCore

/// Catalog §3.1: what an editor window edits. Existing connectors use id ==
/// name (one window each); new ones a fresh UUID. Codable and Hashable for
/// the SwiftUI WindowGroup value.
public struct EditTarget: Identifiable, Codable, Hashable {
    public static let addTitle = "Add Connector"

    public static func editTitle(_ name: String) -> String { "Edit “\(name)”" }

    public let id: String
    public var name: String
    public var entry: MCPEntry
    public var isNew: Bool
    public var forcesRemote: Bool

    public init(id: String, name: String, entry: MCPEntry, isNew: Bool, forcesRemote: Bool = false) {
        self.id = id
        self.name = name
        self.entry = entry
        self.isNew = isNew
        self.forcesRemote = forcesRemote
    }

    public static func existing(name: String, entry: MCPEntry) -> EditTarget {
        EditTarget(id: name, name: name, entry: entry, isNew: false)
    }

    public static func new(template: JSONValue) -> EditTarget {
        EditTarget(id: UUID().uuidString, name: "", entry: MCPEntry(config: template), isNew: true)
    }

    /// Add-Remote flow: the template has an empty URL that detect() cannot
    /// classify, so the remote form is forced explicitly.
    public static func newRemote() -> EditTarget {
        EditTarget(id: UUID().uuidString, name: "",
                   entry: MCPEntry(config: RemotePattern.make(url: "")), isNew: true, forcesRemote: true)
    }

    /// Catalog §3.12: fixed at open time.
    public var windowTitle: String { isNew ? EditTarget.addTitle : EditTarget.editTitle(name) }
}
