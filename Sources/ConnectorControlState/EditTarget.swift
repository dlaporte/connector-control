import Foundation
import ConnectorControlCore

/// What an editor window edits. Existing connectors use id ==
/// name (one window each); new ones a fresh UUID. Codable and Hashable for
/// the SwiftUI WindowGroup value.
public struct EditTarget: Identifiable, Codable, Hashable, Sendable {
    /// The editor `WindowGroup`'s id — shared by `ConnectorControlApp`'s
    /// declaration and `PopoverView`'s `openWindow` call so they cannot drift apart.
    public static let editorWindowID = "editor"
    public static let addTitle = "Add Connector"

    public static func editTitle(_ name: String) -> String { "Edit “\(name)”" }

    public let id: String
    public var name: String
    public var entry: MCPEntry
    public var isNew: Bool
    public var forcesRemote: Bool
    /// Which collection this window edits; nil is "whatever is active", which is what every
    /// editor opened from the popover has always meant.
    public var collection: String?

    public init(id: String, name: String, entry: MCPEntry, isNew: Bool, forcesRemote: Bool = false,
                collection: String? = nil) {
        self.id = id
        self.name = name
        self.entry = entry
        self.isNew = isNew
        self.forcesRemote = forcesRemote
        self.collection = collection
    }

    public static func existing(name: String, entry: MCPEntry, in collection: String? = nil) -> EditTarget {
        EditTarget(id: identifier(name, in: collection), name: name, entry: entry, isNew: false,
                   collection: collection)
    }

    /// Add-Remote flow: the template has an empty URL that detect() cannot
    /// classify, so the remote form is forced explicitly.
    public static func newRemote(in collection: String? = nil) -> EditTarget {
        EditTarget(id: UUID().uuidString, name: "",
                   entry: MCPEntry(config: RemotePattern.make(url: "")), isNew: true, forcesRemote: true,
                   collection: collection)
    }

    /// The same connector in two collections is two windows, so the collection is part of the
    /// identity. A nil collection keeps the bare name the id has always been. The separator is
    /// a control character rather than a slash or a dot, neither of which a collection or a
    /// connector name is stopped from containing.
    static func identifier(_ name: String, in collection: String?) -> String {
        guard let collection else { return name }
        return collection + "\u{001F}" + name
    }

    /// Fixed at open time.
    public var windowTitle: String { isNew ? EditTarget.addTitle : EditTarget.editTitle(name) }
}
