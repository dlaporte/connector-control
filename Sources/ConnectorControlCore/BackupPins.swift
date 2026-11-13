import Foundation

/// Which backups are kept past the keep count. The master-list backup taken just before a delete
/// is the only copy of what the delete removed, and the count-based pruning rotates it out within
/// a day of normal use, so `BackupManager` keeps a pinned backup for `BackupManager.pinDays`. The
/// pins are one small index beside the backups, so every backup stays a byte copy of its file; a
/// pin names the backup by file name.
///
/// The name is chosen so no backup series lists it: every series is matched by "<series>.".
///
/// Mirror: windows/src/ConnectorControl.Core/BackupPins.cs
public enum BackupPins {
    public static let fileName = "backup-pins.json"
    static let formatVersion = 1

    /// Pins `backup`, dropping pins whose backup has since been pruned. Saves only when the index
    /// changes, so pinning the backup an unchanged file kept rewrites nothing.
    public static func pin(_ backup: URL, in backupsDir: URL, staging: URL?) throws {
        let loaded = pinned(in: backupsDir)
        let fm = FileManager.default
        var index = loaded.filter { fm.fileExists(atPath: backupsDir.appendingPathComponent($0).path) }
        index.insert(backup.lastPathComponent)
        guard index != loaded else { return }
        try save(index, in: backupsDir, staging: staging)
    }

    /// The pinned backups' file names. A missing or unreadable index pins nothing.
    public static func pinned(in backupsDir: URL) -> Set<String> {
        guard let data = try? Data(contentsOf: backupsDir.appendingPathComponent(fileName)),
              case .object(let root)? = try? JSONValue.parse(data),
              case .int(formatVersion)? = root["version"],
              case .array(let pins)? = root["pins"] else { return [] }
        return Set(pins.compactMap { value in
            if case .string(let name) = value { return name }
            return nil
        })
    }

    private static func save(_ index: Set<String>, in backupsDir: URL, staging: URL?) throws {
        let root: JSONValue = .object([
            "version": .int(formatVersion),
            "pins": .array(index.sorted { $0.ordinallyPrecedes($1) }.map(JSONValue.string)),
        ])
        try AtomicFile.write(root.serialized(), to: backupsDir.appendingPathComponent(fileName), staging: staging)
    }
}
