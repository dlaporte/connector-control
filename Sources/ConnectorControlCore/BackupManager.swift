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
        // Written like every other private file (created 0600, atomically), not
        // copied: copyItem would inherit the source's mode — Claude's config is
        // usually 0644 — and, for a symlinked config, copy the link itself
        // rather than the bytes it points at.
        try AtomicFile.write(try Data(contentsOf: url), to: dest)
    }

    /// Returns the existing newest backup instead of writing a duplicate when
    /// the file's content is unchanged: regeneration backs up without user
    /// action, and identical snapshots would only churn real history out of
    /// the retention window. Dedup is against the newest snapshot only, so an
    /// A → B → A sequence still records the return to A.
    @discardableResult
    public func backUp(fileAt url: URL, series: String, now: Date = Date()) throws -> URL? {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else { return nil }
        if let newest = try backups(series: series).first,
           let current = try? Data(contentsOf: url),
           (try? Data(contentsOf: newest)) == current {
            return newest
        }
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
        // See ensureOriginalSnapshot: a private, atomic write of the bytes, not a
        // copy of the file (or of a symlink to it). AtomicFile creates the
        // backups directory 0700 when it does not exist yet.
        try AtomicFile.write(try Data(contentsOf: url), to: dest)
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
