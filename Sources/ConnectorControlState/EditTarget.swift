import Foundation
import ConnectorControlCore

/// What an editor window edits. An existing connector's id is its collection, `\u{001F}`, then
/// its name, so there is one window per connector per collection; a new one's is a fresh UUID.
/// Codable and Hashable for the SwiftUI WindowGroup value.
///
/// Mirror: windows/src/ConnectorControl.Core/State/EditTarget.cs
public struct EditTarget: Identifiable, Codable, Hashable, Sendable {
    /// The editor `WindowGroup`'s id — shared by `ConnectorControlApp`'s declaration and
    /// `CollectionsWindowView`'s `openWindow` calls so they cannot drift apart.
    public static let editorWindowID = "editor"
    public static let addTitle = "Add Connector"

    public static func editTitle(_ name: String) -> String { "Edit “\(name)”" }

    public let id: String
    public var name: String
    public var entry: MCPEntry
    public var isNew: Bool
    public var forcesRemote: Bool
    /// Which collection this window edits. Always named: a window that followed whichever
    /// collection is active would move its read-only state, its header and its save target
    /// whenever the active collection changed under it.
    public var collection: String

    public init(id: String, name: String, entry: MCPEntry, isNew: Bool, forcesRemote: Bool = false,
                collection: String) {
        self.id = id
        self.name = name
        self.entry = entry
        self.isNew = isNew
        self.forcesRemote = forcesRemote
        self.collection = collection
    }

    public static func existing(name: String, entry: MCPEntry, in collection: String) -> EditTarget {
        EditTarget(id: identifier(name, in: collection), name: name, entry: entry, isNew: false,
                   collection: collection)
    }

    /// Add-Remote flow: the template has an empty URL that detect() cannot
    /// classify, so the remote form is forced explicitly.
    public static func newRemote(in collection: String) -> EditTarget {
        EditTarget(id: UUID().uuidString, name: "",
                   entry: MCPEntry(config: RemotePattern.make(url: "")), isNew: true, forcesRemote: true,
                   collection: collection)
    }

    /// The same connector in two collections is two windows, so the collection is part of the
    /// identity. The separator is a control character rather than a slash or a dot, neither of
    /// which a collection or a connector name is stopped from containing.
    static func identifier(_ name: String, in collection: String) -> String {
        collection + "\u{001F}" + name
    }

    /// Fixed at open time.
    public var windowTitle: String { isNew ? EditTarget.addTitle : EditTarget.editTitle(name) }
}
