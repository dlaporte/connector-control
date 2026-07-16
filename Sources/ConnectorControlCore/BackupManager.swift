import Foundation

public struct BackupManager {
    public let backupsDir: URL
    public let keepCount: Int

    public init(backupsDir: URL, keepCount: Int = 20) {
        self.backupsDir = backupsDir
        self.keepCount = keepCount
    }

    /// First-run snapshot; written once, never pruned.
    public func ensureOriginalSnapshot(of url: URL) throws {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else { return }
        let base = url.deletingPathExtension().lastPathComponent
        let dest = backupsDir.appendingPathComponent("\(base).original.json")
        guard !fm.fileExists(atPath: dest.path) else { return }
        try fm.createDirectory(at: backupsDir, withIntermediateDirectories: true)
        try fm.copyItem(at: url, to: dest)
        // Backups can hold env-var secrets — keep them owner-only.
        try? fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: dest.path)
    }

    @discardableResult
    public func backUp(fileAt url: URL, series: String, now: Date = Date()) throws -> URL? {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else { return nil }
        try fm.createDirectory(at: backupsDir, withIntermediateDirectories: true)
        var dest = backupsDir
            .appendingPathComponent("\(series).\(BackupTimestamp.string(from: now)).json")
        var counter = 2
        while fm.fileExists(atPath: dest.path), counter <= 100 {
            dest = backupsDir.appendingPathComponent(
                "\(series).\(BackupTimestamp.string(from: now))-\(counter).json")
            counter += 1
        }
        // Bound exhausted (100 same-millisecond backups already exist): overwrite
        // rather than throw.
        if fm.fileExists(atPath: dest.path) {
            try fm.removeItem(at: dest)
        }
        try fm.copyItem(at: url, to: dest)
        // Backups can hold env-var secrets — keep them owner-only.
        try? fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: dest.path)
        try prune(series: series)
        return dest
    }

    /// Timestamped backups for a series, newest first. Excludes `.original`.
    public func backups(series: String) throws -> [URL] {
        let fm = FileManager.default
        guard fm.fileExists(atPath: backupsDir.path) else { return [] }
        return try fm.contentsOfDirectory(at: backupsDir, includingPropertiesForKeys: nil)
            .filter {
                $0.lastPathComponent.hasPrefix("\(series).")
                    && !$0.lastPathComponent.contains(".original.")
            }
            .sorted { $0.lastPathComponent > $1.lastPathComponent }
    }

    private func prune(series: String) throws {
        let all = try backups(series: series)
        for stale in all.dropFirst(keepCount) {
            try FileManager.default.removeItem(at: stale)
        }
    }
}
