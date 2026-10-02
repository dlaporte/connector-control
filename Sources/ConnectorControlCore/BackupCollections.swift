import Foundation

/// Which collection each Claude-config backup was taken from. Claude's file holds whichever
/// collection it was last applied from, so a restore belongs in that collection and no other. The
/// record is one small index beside the backups, so every backup stays a byte copy of Claude's
/// file. A backup the index does not name — one from before it was kept, the first-run original, a
/// file chosen from elsewhere — has no recorded collection.
///
/// The name is chosen so no backup series lists it: every series is matched by "<series>.".
///
/// Mirror: windows/src/ConnectorControl.Core/BackupCollections.cs
public enum BackupCollections {
    public static let fileName = "backup-collections.json"
    static let formatVersion = 1

    /// The collection `backup` was taken from, when it is one of the backups in `backupsDir` and
    /// the index names it.
    public static func collection(of backup: URL, in backupsDir: URL) -> String? {
        guard backup.deletingLastPathComponent().standardizedFileURL.path == backupsDir.standardizedFileURL.path else {
            return nil
        }
        return load(backupsDir)[backup.lastPathComponent]
    }

    /// Records `collection` for `backup`, dropping entries whose backup has since been pruned.
    /// An unchanged file keeps its one backup, which then belongs to the collection that wrote it
    /// last: its bytes are that collection's as much as the earlier one's.
    public static func record(_ collection: String, for backup: URL, in backupsDir: URL, staging: URL?) throws {
        let loaded = load(backupsDir)
        let fm = FileManager.default
        var index = loaded.filter { fm.fileExists(atPath: backupsDir.appendingPathComponent($0.key).path) }
        index[backup.lastPathComponent] = collection
        guard index != loaded else { return }
        try save(index, in: backupsDir, staging: staging)
    }

    /// Renames `collection` wherever the index records it, so a backup taken before a rename still
    /// restores into the collection it came from.
    public static func rename(_ collection: String, to newName: String, in backupsDir: URL, staging: URL?) throws {
        let loaded = load(backupsDir)
        let index = loaded.mapValues { $0 == collection ? newName : $0 }
        guard index != loaded else { return }
        try save(index, in: backupsDir, staging: staging)
    }

    private static func save(_ index: [String: String], in backupsDir: URL, staging: URL?) throws {
        let root: JSONValue = .object([
            "version": .int(formatVersion),
            "backups": .object(index.mapValues(JSONValue.string)),
        ])
        try AtomicFile.write(root.serialized(), to: backupsDir.appendingPathComponent(fileName), staging: staging)
    }

    /// A missing or unreadable index names nothing: a restore then falls back to the active
    /// collection, as every restore did before the index was kept.
    static func load(_ backupsDir: URL) -> [String: String] {
        guard let data = try? Data(contentsOf: backupsDir.appendingPathComponent(fileName)),
              case .object(let root)? = try? JSONValue.parse(data),
              case .int(formatVersion)? = root["version"],
              case .object(let backups)? = root["backups"] else { return [:] }
        return backups.compactMapValues { value in
            if case .string(let collection) = value { return collection }
            return nil
        }
    }
}
