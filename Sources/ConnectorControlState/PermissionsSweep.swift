import Foundation
import ConnectorControlCore

/// One-time repair of files written before owner-only permissions were
/// enforced (catalog §1.15), gated by the permissionsSweepDone setting so
/// launches stay cheap. Every error is ignored (try?), as before.
///
/// The sweep touches only what this app writes. The store directory can be a
/// folder the user chose — a git checkout, iCloud Drive, Documents — so only
/// mcps.json and the corrupt-file asides beside it are repaired there, never
/// anything else in the folder and never anything below it, and the folder's
/// own mode is tightened only while it is the app's default location. The
/// backups directory is always the app's own (machine-local, never the synced
/// folder), so everything under it is the app's to repair.
public enum PermissionsSweep {
    /// True when the sweep ran (first time only).
    @discardableResult
    public static func runOnce(settings: AppSettings, paths: AppPaths) -> Bool {
        guard !settings.permissionsSweepDone else { return false }
        let fm = FileManager.default
        let storeDir = paths.storeDirURL
        if settings.masterStoreDir == nil {
            try? fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: storeDir.path)
        }
        if let names = try? fm.contentsOfDirectory(atPath: storeDir.path) {
            for name in names where isStoreFile(name) {
                try? fm.setAttributes([.posixPermissions: 0o600],
                                      ofItemAtPath: storeDir.appendingPathComponent(name).path)
            }
        }
        let backups = paths.backupsDirURL
        try? fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: backups.path)
        if let files = fm.enumerator(at: backups, includingPropertiesForKeys: [.isDirectoryKey]) {
            for case let file as URL in files {
                let isDir = (try? file.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory ?? false
                try? fm.setAttributes([.posixPermissions: isDir ? 0o700 : 0o600], ofItemAtPath: file.path)
            }
        }
        settings.permissionsSweepDone = true
        return true
    }

    /// mcps.json and the `mcps.corrupt.<timestamp>.json` asides MasterStoreIO leaves beside it.
    static func isStoreFile(_ name: String) -> Bool {
        name == "mcps.json" || (name.hasPrefix("mcps.corrupt.") && name.hasSuffix(".json"))
    }
}
