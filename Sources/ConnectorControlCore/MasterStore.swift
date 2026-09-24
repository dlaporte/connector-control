import Foundation

public enum EditView: String, Codable, Hashable, Sendable {
    case form, json
}

public struct MCPEntry: Equatable, Hashable, Codable, Sendable {
    public var enabled: Bool
    public var config: JSONValue
    public var lastEditView: EditView

    public init(enabled: Bool = true, config: JSONValue, lastEditView: EditView = .form) {
        self.enabled = enabled
        self.config = config
        self.lastEditView = lastEditView
    }
}

/// A full, independent snapshot of connectors: its own configs + enabled flags.
public struct Collection: Equatable, Codable, Sendable {
    public var mcps: [String: MCPEntry]
    public init(mcps: [String: MCPEntry] = [:]) { self.mcps = mcps }
}

/// Schema v2 only — no v1 fallback. A v1 (or otherwise malformed) file on
/// disk fails to decode and is handled by `MasterStoreIO.load`'s existing
/// corrupt-file path: moved aside and rebuilt fresh from Claude's config.
public struct MasterStore: Equatable, Codable, Sendable {
    public var version: Int
    public var activeCollection: String
    public var collections: [String: Collection]

    // The file keeps the v2 key names: machines on the current release share it through the
    // synced master-list folder, and their decoder knows only these two keys.
    enum CodingKeys: String, CodingKey {
        case version
        case activeCollection = "activeProfile"
        case collections = "profiles"
    }

    /// The active collection's connectors — the view the entire app operates on.
    public var mcps: [String: MCPEntry] {
        get { collections[activeCollection]?.mcps ?? [:] }
        set { collections[activeCollection, default: Collection()].mcps = newValue }
    }

    /// Claude's `mcpServers` section rendered from this store — the enabled
    /// subset's configs. The store is the source of truth; Claude's config is
    /// downstream, and any divergence from this render is regenerated away.
    public var enabledServers: [String: JSONValue] {
        mcps.filter(\.value.enabled).mapValues(\.config)
    }

    public static let empty = MasterStore(
        activeCollection: "Default",
        collections: ["Default": Collection()])

    public init(activeCollection: String, collections: [String: Collection]) {
        self.version = 2
        self.activeCollection = activeCollection
        self.collections = collections
    }

    /// The name a collection is kept under for the one typed: spaces trimmed from both ends.
    /// Adding and renaming both apply it, so a caller that follows the collection it just named
    /// asks here rather than trimming again.
    public static func collectionName(_ typed: String) -> String { typed.trimmingCharacters(in: .whitespaces) }

    /// nil on success, else a user-facing error message. The new collection becomes the active
    /// one unless `activating` is false.
    public mutating func addCollection(named name: String, copyingCurrent: Bool, activating: Bool = true) -> String? {
        let trimmed = Self.collectionName(name)
        guard !trimmed.isEmpty else { return "Name must not be empty." }
        guard collections[trimmed] == nil else {
            return "A collection named \u{201C}\(trimmed)\u{201D} already exists."
        }
        collections[trimmed] = copyingCurrent ? Collection(mcps: mcps) : Collection()
        if activating { activeCollection = trimmed }
        return nil
    }

    /// nil on success, else a user-facing error message. Renaming the active
    /// collection keeps it active under its new name.
    public mutating func renameCollection(_ name: String, to newName: String) -> String? {
        guard collections[name] != nil else { return Self.noCollectionError(name) }
        let trimmed = Self.collectionName(newName)
        guard !trimmed.isEmpty else { return "Name must not be empty." }
        if trimmed != name, collections[trimmed] != nil {
            return "A collection named \u{201C}\(trimmed)\u{201D} already exists."
        }
        guard let current = collections.removeValue(forKey: name) else { return nil }
        collections[trimmed] = current
        if activeCollection == name { activeCollection = trimmed }
        return nil
    }

    /// nil on success, else a user-facing error message. Refuses to delete the
    /// last remaining collection. Deleting the active collection hands the
    /// sorted-first remaining collection the active spot.
    public mutating func deleteCollection(named name: String) -> String? {
        guard collections[name] != nil else { return Self.noCollectionError(name) }
        guard collections.count > 1 else { return "Can\u{2019}t delete the last collection." }
        let successor = activeAfterDeleting(name)
        collections.removeValue(forKey: name)
        if activeCollection == name { activeCollection = successor ?? "Default" }
        return nil
    }

    /// The collection that takes the active spot if `name` is deleted: the sorted-first of the
    /// rest, or nil when none remain. The one rule, so the Delete confirmation that names it
    /// cannot disagree with the delete that picks it.
    public func activeAfterDeleting(_ name: String) -> String? {
        collections.keys.filter { $0 != name }.min { $0.ordinallyPrecedes($1) }
    }

    /// The one wording for a name no collection has, shared by switch, rename and delete.
    static func noCollectionError(_ name: String) -> String { "No collection named \u{201C}\(name)\u{201D}." }

    public mutating func switchCollection(to name: String) -> String? {
        guard collections[name] != nil else { return Self.noCollectionError(name) }
        activeCollection = name
        return nil
    }
}

public enum MasterStoreIO {
    /// Missing file → empty store. Corrupt file → moved aside to
    /// `mcps.corrupt.<timestamp>.json` (returned) and an empty store; the caller
    /// repopulates it by reconciling against Claude's config.
    public static func load(
        from url: URL, now: Date = Date()
    ) -> (store: MasterStore, corruptFileURL: URL?) {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else { return (.empty, nil) }
        do {
            let data = try Data(contentsOf: url)
            var store = try JSONDecoder().decode(MasterStore.self, from: data)
            // Self-heal a decoded-but-inconsistent activeCollection (hand-edited
            // or corrupted file) — never crash; fall back to an existing
            // collection (sorted first), or a fresh Default if none remain.
            if store.collections[store.activeCollection] == nil {
                if let fallback = store.collections.keys.sorted().first {
                    store.activeCollection = fallback
                } else {
                    store.collections["Default"] = Collection()
                    store.activeCollection = "Default"
                }
            }
            return (store, nil)
        } catch {
            let stamp = BackupTimestamp.string(from: now)
            let aside = url.deletingLastPathComponent()
                .appendingPathComponent("mcps.corrupt.\(stamp).json")
            do {
                try fm.moveItem(at: url, to: aside)
                return (.empty, aside)
            } catch {
                // Couldn't move it aside; the corrupt file stays in place.
                return (.empty, url)
            }
        }
    }

    public static func save(_ store: MasterStore, to url: URL, staging: URL? = nil) throws {
        try AtomicFile.write(try JSONEncoder.canonical.encode(store), to: url, staging: staging)
    }

    /// Side-effect-free peek: nil when the file is missing or undecodable.
    /// Unlike `load`, never moves a corrupt file aside — used by the store
    /// watcher to classify an on-disk change (own write echo, external edit,
    /// or a sync tool's mid-write partial) before deciding to adopt it.
    public static func read(from url: URL) -> MasterStore? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(MasterStore.self, from: data)
    }
}
