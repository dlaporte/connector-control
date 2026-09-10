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
///
/// Two one-shot passes share this: the mode repair (permissionsSweepDone) and the later ACL
/// strip (aclSweepDone); an install that already had the first gets only the second.
public enum PermissionsSweep {
    /// True when the sweep ran (first time only).
    @discardableResult
    public static func runOnce(settings: AppSettings, paths: AppPaths) -> Bool {
        let modes = !settings.permissionsSweepDone
        let acls = !settings.aclSweepDone
        guard modes || acls else { return false }
        let fm = FileManager.default
        func repair(_ url: URL, mode: Int) {
            if modes { try? fm.setAttributes([.posixPermissions: mode], ofItemAtPath: url.path) }
            if acls { try? AtomicFile.stripACL(atPath: url.path) }
        }
        let storeDir = paths.storeDirURL
        if settings.masterStoreDir == nil {
            repair(storeDir, mode: 0o700)
        }
        if let names = try? fm.contentsOfDirectory(atPath: storeDir.path) {
            for name in names where isStoreFile(name) {
                repair(storeDir.appendingPathComponent(name), mode: 0o600)
            }
        }
        let backups = paths.backupsDirURL
        repair(backups, mode: 0o700)
        if let files = fm.enumerator(at: backups, includingPropertiesForKeys: [.isDirectoryKey]) {
            for case let file as URL in files {
                let isDir = (try? file.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory ?? false
                repair(file, mode: isDir ? 0o700 : 0o600)
            }
        }
        settings.permissionsSweepDone = true
        settings.aclSweepDone = true
        return true
    }

    /// mcps.json and the `mcps.corrupt.<timestamp>.json` asides MasterStoreIO leaves beside it.
    static func isStoreFile(_ name: String) -> Bool {
        name == "mcps.json" || (name.hasPrefix("mcps.corrupt.") && name.hasSuffix(".json"))
    }
}
