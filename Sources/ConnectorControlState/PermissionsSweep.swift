import Foundation
import ConnectorControlCore

/// One-time repair of files written before owner-only permissions were
/// enforced, gated by the sweepVersion setting so launches stay cheap. Every
/// error is ignored (try?), as before.
///
/// The sweep touches only what this app writes. The store directory can be a
/// folder the user chose — a git checkout, iCloud Drive, Documents — so only
/// mcps.json and the corrupt-file asides beside it are repaired there, never
/// anything else in the folder and never anything below it, and the folder's
/// own mode is tightened only while it is the app's default location. The
/// backups directory is always the app's own (machine-local, never the synced
/// folder), so everything under it is the app's to repair.
///
/// Two one-shot passes share this: pass 1 (mode) and pass 2 (ACL strip),
/// tracked by a single `sweepVersion` rather than one flag each, so a future
/// third pass needs only a `currentVersion` bump and one more `< N` check.
/// There is no migration off the old `permissionsSweepDone`/`aclSweepDone`
/// flags: both passes are idempotent and cheap, so an upgraded install simply
/// re-runs the whole sweep once more under the new key.
///
/// Each pass keeps its own attempted/applied count: `sweepVersion` lands on
/// the number of CONTIGUOUS done passes from the start, so a mode pass that
/// fails on every file cannot be hidden behind an ACL pass that happens to
/// succeed in the same run — the failed pass, and anything after it, is
/// retried on the next launch.
public enum PermissionsSweep {
    /// Bump this, and add the new pass's `< currentVersion` check below, to add a pass.
    public static let currentVersion = 2

    /// True when the sweep ran (first time only).
    @discardableResult
    @MainActor
    public static func runOnce(
        settings: AppSettings, paths: AppPaths,
        repairModes: @MainActor (URL, Int) -> Bool = { (try? FileManager.default.setAttributes(
            [.posixPermissions: $1], ofItemAtPath: $0.path)) != nil },
        repairACLs: @MainActor (URL) -> Bool = { (try? AtomicFile.stripACL(atPath: $0.path)) != nil }
    ) -> Bool {
        let modes = settings.sweepVersion < 1
        let acls = settings.sweepVersion < 2
        guard modes || acls else { return false }
        let fm = FileManager.default
        // Each pass is counted on its own: a launch where the mode pass fails
        // on every file while the ACL pass succeeds must not hide that failure
        // behind the ACL pass's success in a single combined counter.
        var attempted1 = 0, applied1 = 0
        var attempted2 = 0, applied2 = 0
        func repair(_ url: URL, mode: Int) {
            if modes {
                attempted1 += 1
                if repairModes(url, mode) { applied1 += 1 }
            }
            if acls {
                attempted2 += 1
                if repairACLs(url) { applied2 += 1 }
            }
        }
        // A directory that does not exist yet (a fresh install, swept before
        // the first reload() has written anything) has nothing to protect: it
        // is not an attempt that failed, it is nothing to do.
        let storeDir = paths.storeDirURL
        if (settings.masterStoreDir ?? "").isEmpty, fm.fileExists(atPath: storeDir.path) {
            repair(storeDir, mode: 0o700)
        }
        if let names = try? fm.contentsOfDirectory(atPath: storeDir.path) {
            for name in names where isStoreFile(name) {
                repair(storeDir.appendingPathComponent(name), mode: 0o600)
            }
        }
        let backups = paths.backupsDirURL
        if fm.fileExists(atPath: backups.path) {
            repair(backups, mode: 0o700)
        }
        if let files = fm.enumerator(at: backups, includingPropertiesForKeys: [.isDirectoryKey]) {
            for case let file as URL in files {
                let isDir = (try? file.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory ?? false
                repair(file, mode: isDir ? 0o700 : 0o600)
            }
        }
        // A pass that tried and achieved nothing is not done: leave the version
        // where it was so the next launch retries it, instead of recording
        // success. sweepVersion lands on the number of CONTIGUOUS done passes
        // from the start, so a failed pass 1 blocks pass 2 from ever counting,
        // even if pass 2's own attempts all happened to succeed.
        let pass1Done = attempted1 == 0 || applied1 > 0
        let pass2Done = pass1Done && (attempted2 == 0 || applied2 > 0)
        settings.sweepVersion = pass2Done ? 2 : (pass1Done ? 1 : 0)
        return true
    }

    /// mcps.json and the `mcps.corrupt.<timestamp>.json` asides MasterStoreIO leaves beside it.
    static func isStoreFile(_ name: String) -> Bool {
        name == "mcps.json" || (name.hasPrefix("mcps.corrupt.") && name.hasSuffix(".json"))
    }
}
