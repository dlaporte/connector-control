import Foundation

public struct BackupManager: Sendable {
    /// Same-millisecond backups tried before giving up and overwriting the last one.
    private static let collisionBound = 100
    public static let defaultKeepCount = 20
    /// How long a pinned backup (`BackupPins`) outlives the keep count.
    public static let pinDays = 30

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
        let dest = originalPath(series: series)
        guard !fm.fileExists(atPath: dest.path) else { return }
        // Written like every other private file (created 0600, atomically), not
        // copied: copyItem would inherit the source's mode - Claude's config is
        // usually 0644 - and, for a symlinked config, copy the link itself
        // rather than the bytes it points at.
        try AtomicFile.write(try Data(contentsOf: url), to: dest, staging: stagingDir)
    }

    /// The `.original.json` snapshot's URL for a series - what `ensureOriginalSnapshot`
    /// writes once and never prunes - when it exists on disk; nil otherwise.
    public func originalSnapshotURL(series: String) -> URL? {
        let dest = originalPath(series: series)
        return FileManager.default.fileExists(atPath: dest.path) ? dest : nil
    }

    /// Every backup the Restore sheet offers for a series: the timestamped ones newest first, then
    /// the first-run original when there is one.
    public func restorableBackups(series: String) throws -> [URL] {
        var found = try backups(series: series)
        if let original = originalSnapshotURL(series: series) { found.append(original) }
        return found
    }

    private func originalPath(series: String) -> URL {
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
        // A replaced last candidate is already listed, and counting it twice would prune one too many.
        let listed = existing.contains { $0.lastPathComponent == dest.lastPathComponent }
        prune(listing: listed ? existing : existing + [dest], now: now)
        return dest
    }

    /// Timestamped backups for a series, newest first. Excludes `.original`.
    public func backups(series: String) throws -> [URL] {
        let fm = FileManager.default
        guard fm.fileExists(atPath: backupsDir.path) else { return [] }
        // Regular files only, as Windows lists files: a folder named like a backup is never pruned.
        return try fm.contentsOfDirectory(at: backupsDir, includingPropertiesForKeys: [.isRegularFileKey])
            .filter {
                $0.lastPathComponent.hasPrefix("\(series).")
                    && !$0.lastPathComponent.contains(".original.")
                    && (try? $0.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true
            }
            .sorted(by: BackupManager.newestFirst)
    }

    /// Newest first: by stamp, then by the counter a same-millisecond backup takes, a name without
    /// one being the first of its millisecond. Sorting the names as text would not do: `-` sorts
    /// before `.`, so `<stamp>-2.json` would list below `<stamp>.json`, and `-10` below `-2`.
    static func newestFirst(_ a: URL, _ b: URL) -> Bool {
        let x = order(of: a.lastPathComponent), y = order(of: b.lastPathComponent)
        return x.stamp != y.stamp ? y.stamp.ordinallyPrecedes(x.stamp) : x.counter > y.counter
    }

    /// When a backup was taken, read from its name: `<series>.<stamp>.json`, or `-<n>` after the
    /// stamp. nil for a name that carries no stamp, such as the first-run original.
    public static func takenAt(_ backup: URL) -> Date? {
        let stamp = order(of: backup.lastPathComponent).stamp
        return stamp.split(separator: ".").last.flatMap { BackupTimestamp.date(from: String($0)) }
    }

    /// A backup name's stamp and counter: `<series>.<stamp>.json` is counter 1, and
    /// `<series>.<stamp>-<n>.json` counter n. The stamp ends in `Z`, so the counter is what follows it.
    private static func order(of name: String) -> (stamp: String, counter: Int) {
        let base = name.hasSuffix(".json") ? String(name.dropLast(".json".count)) : name
        guard let z = base.lastIndex(of: "Z") else { return (base, 1) }
        let rest = base[base.index(after: z)...]
        // Digits only, as Windows parses it: Int alone would take a sign.
        let digits = rest.dropFirst()
        guard rest.hasPrefix("-"), !digits.isEmpty, digits.allSatisfy({ ("0"..."9").contains($0) }),
              let counter = Int(digits) else { return (base, 1) }
        return (String(base[...z]), counter)
    }

    /// Keeps the `keepCount` newest, and every pinned backup (`BackupPins`) taken less than
    /// `pinDays` before `now`: a pinned one still among the newest counts as one of them, and only
    /// keeps itself once it has fallen past. A pin older than that prunes as any other backup.
    ///
    /// `listing` is the caller's own pre-write directory read plus the file it just
    /// wrote - sorted the same way `backups(series:)` would - so pruning does not
    /// re-list a directory `backUp` already just listed.
    private func prune(listing: [URL], now: Date) {
        let all = listing.sorted(by: BackupManager.newestFirst)
        let stale = all.dropFirst(keepCount)
        guard !stale.isEmpty else { return }
        let pinned = BackupPins.pinned(in: backupsDir)
        let cutoff = now.addingTimeInterval(-Double(BackupManager.pinDays) * 86_400)
        for old in stale {
            if pinned.contains(old.lastPathComponent), let taken = BackupManager.takenAt(old), taken > cutoff { continue }
            // Best-effort: a locked or read-only stale backup must not fail the
            // user's save; the next rotation retries.
            try? FileManager.default.removeItem(at: old)
        }
    }

    /// What each backup is called where several are listed together, keyed by file name: its
    /// local time to the second. Backups that share a second carry milliseconds, read from the
    /// name's stamp; those that still share one add " (n)" from the counter in the name, the first
    /// of a millisecond taking none. A name with no time is its own label.
    public static func labels(for backups: [URL]) -> [String: String] {
        var result: [String: String] = [:]
        var timed: [(name: String, seconds: String, millis: String, counter: Int)] = []
        for backup in backups {
            let name = backup.lastPathComponent
            guard let taken = takenAt(backup) else { result[name] = name; continue }
            let parts = order(of: name)
            let digits = parts.stamp.dropLast().suffix(3)
            timed.append((name, IsoTimestamp.localDateTimeSeconds(from: taken), String(digits), parts.counter))
        }
        var perSecond: [String: Int] = [:]
        for entry in timed { perSecond[entry.seconds, default: 0] += 1 }
        let fine = timed.map { entry in
            (entry, perSecond[entry.seconds]! > 1 ? "\(entry.seconds).\(entry.millis)" : entry.seconds)
        }
        var groups: [String: [(name: String, counter: Int)]] = [:]
        for (entry, label) in fine { groups[label, default: []].append((entry.name, entry.counter)) }
        for (label, members) in groups {
            let ordered = members.sorted { $0.counter < $1.counter }
            for (index, member) in ordered.enumerated() {
                result[member.name] = index == 0 ? label : "\(label) (\(member.counter))"
            }
        }
        return result
    }
}
