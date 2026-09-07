import Foundation
import ConnectorControlCore

/// One-time repair of files written before owner-only permissions were
/// enforced (catalog §1.15), gated by the permissionsSweepDone setting so
/// launches stay cheap. Every error is ignored (try?), as before.
public enum PermissionsSweep {
    /// True when the sweep ran (first time only).
    @discardableResult
    public static func runOnce(settings: AppSettings, paths: AppPaths) -> Bool {
        guard !settings.permissionsSweepDone else { return false }
        let fm = FileManager.default
        for root in [paths.storeDirURL, paths.backupsDirURL] {
            try? fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: root.path)
            guard let files = fm.enumerator(at: root, includingPropertiesForKeys: [.isDirectoryKey]) else { continue }
            for case let file as URL in files {
                let isDir = (try? file.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory ?? false
                try? fm.setAttributes([.posixPermissions: isDir ? 0o700 : 0o600], ofItemAtPath: file.path)
            }
        }
        settings.permissionsSweepDone = true
        return true
    }
}
