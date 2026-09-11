import Foundation

public struct BackupManager: Sendable {
    /// Same-millisecond backups tried before giving up and overwriting the last one.
    private static let collisionBound = 100
    public static let defaultKeepCount = 20

    public let backupsDir: URL
    public let keepCount: Int
    /// Where temp files are staged before the atomic rename (see `AtomicFile.write`);
    /// nil in tests/tools that don't care where a temp file is briefly born.
    public let stagingDir: URL?

    public init(backupsDir: URL, keepCount: Int = BackupManager.defaultKeepCount, stagingDir: URL? = nil) {
        self.backupsDir = backupsDir
        self.keepCount = keepCount
        self.stagingDir = stagingDir
    }

    /// First-run snapshot; written once, never pruned.
    public func ensureOriginalSnapshot(of url: URL) throws {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else { return }
        let series = url.deletingPathExtension().lastPathComponent
        let dest = originalSnapshotPath(series: series)
        guard !fm.fileExists(atPath: dest.path) else { return }
        // Written like every other private file (created 0600, atomically), not
        // copied: copyItem would inherit the source's mode — Claude's config is
        // usually 0644 — and, for a symlinked config, copy the link itself
        // rather than the bytes it points at.
        try AtomicFile.write(try Data(contentsOf: url), to: dest, staging: stagingDir)
    }

    /// The `.original.json` snapshot's URL for a series — what `ensureOriginalSnapshot`
    /// writes once and never prunes — when it exists on disk; nil otherwise.
    public func originalSnapshotURL(series: String) -> URL? {
        let dest = originalSnapshotPath(series: series)
        return FileManager.default.fileExists(atPath: dest.path) ? dest : nil
    }

    private func originalSnapshotPath(series: String) -> URL {
        backupsDir.appendingPathComponent("\(series).original.json")
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
        let existing = try backups(series: series)
        let current = try Data(contentsOf: url)
        if let newest = existing.first, (try? Data(contentsOf: newest)) == current {
            return newest
        }
        var dest = backupsDir
            .appendingPathComponent("\(series).\(BackupTimestamp.string(from: now)).json")
        var counter = 2
        while fm.fileExists(atPath: dest.path), counter <= BackupManager.collisionBound {
            dest = backupsDir.appendingPathComponent(
                "\(series).\(BackupTimestamp.string(from: now))-\(counter).json")
            counter += 1
        }
        // Bound exhausted (collisionBound same-millisecond backups already
        // exist): overwrite rather than throw.
        if fm.fileExists(atPath: dest.path) {
            try fm.removeItem(at: dest)
        }
        // See ensureOriginalSnapshot: a private, atomic write of the bytes, not a
        // copy of the file (or of a symlink to it). AtomicFile creates the
        // backups directory 0700 when it does not exist yet.
        try AtomicFile.write(current, to: dest, staging: stagingDir)
        try prune(series: series, listing: existing + [dest])
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

    /// `listing` is the caller's own pre-write directory read plus the file it just
    /// wrote — sorted the same way `backups(series:)` would — so pruning does not
    /// re-list a directory `backUp` already just listed.
    private func prune(series: String, listing: [URL]) throws {
        let all = listing.sorted { $0.lastPathComponent > $1.lastPathComponent }
        for stale in all.dropFirst(keepCount) {
            try FileManager.default.removeItem(at: stale)
        }
    }
}
