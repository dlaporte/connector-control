import XCTest
import ConnectorControlTestSupport
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/BackupManagerTests.cs
final class BackupManagerTests: XCTestCase {
    var tempDir: TempDir!
    var dir: URL!
    var source: URL!
    var manager: BackupManager!

    override func setUpWithError() throws {
        tempDir = TempDir(prefix: "backups")
        dir = tempDir.url
        source = dir.appendingPathComponent("claude_desktop_config.json")
        try Data(#"{"mcpServers": {}}"#.utf8).write(to: source)
        manager = BackupManager(backupsDir: dir.appendingPathComponent("backups"), keepCount: 3)
    }

    override func tearDownWithError() throws {
        tempDir.dispose()
    }

    func testBackupsOfMissingDirIsEmpty() throws {
        XCTAssertEqual(try manager.backups(series: "claude_desktop_config"), [])
    }

    func testBackupsDirectoryIsCreatedPrivate() throws {
        _ = try manager.backUp(fileAt: source, series: "claude_desktop_config")
        let mode = try XCTUnwrap(FileManager.default
            .attributesOfItem(atPath: manager.backupsDir.path)[.posixPermissions] as? Int)
        XCTAssertEqual(mode, 0o700)
    }

    func testBackUpCreatesTimestampedCopy() throws {
        let made = try XCTUnwrap(manager.backUp(
            fileAt: source, series: "claude_desktop_config",
            now: Date(timeIntervalSince1970: 1_752_600_000)))
        XCTAssertTrue(made.lastPathComponent.hasPrefix("claude_desktop_config."))
        XCTAssertTrue(made.lastPathComponent.hasSuffix(".json"))
        XCTAssertEqual(try Data(contentsOf: made), try Data(contentsOf: source))
    }

    func testBackUpSkipsWhenIdenticalToNewest() throws {
        let first = try XCTUnwrap(manager.backUp(
            fileAt: source, series: "claude_desktop_config",
            now: Date(timeIntervalSince1970: 1_752_600_000)))
        let second = try manager.backUp(
            fileAt: source, series: "claude_desktop_config",
            now: Date(timeIntervalSince1970: 1_752_600_001))
        // resolvingSymlinksInPath: the directory listing returns /private/var
        // URLs while the write path builds /var ones - same file.
        XCTAssertEqual(second?.resolvingSymlinksInPath(), first.resolvingSymlinksInPath(),
                       "identical content returns the existing newest backup")
        XCTAssertEqual(try manager.backups(series: "claude_desktop_config").count, 1)
        try Data("changed".utf8).write(to: source)
        let third = try XCTUnwrap(manager.backUp(
            fileAt: source, series: "claude_desktop_config",
            now: Date(timeIntervalSince1970: 1_752_600_002)))
        XCTAssertNotEqual(third, first)
        XCTAssertEqual(try manager.backups(series: "claude_desktop_config").count, 2)
    }

    func testBackUpDedupsOnlyAgainstNewest() throws {
        // A → B → back to A: the return to A still records - dedup compares
        // against the newest snapshot only, not the whole history.
        for (i, content) in ["A", "B", "A"].enumerated() {
            try Data(content.utf8).write(to: source)
            try manager.backUp(fileAt: source, series: "claude_desktop_config",
                               now: Date(timeIntervalSince1970: Double(1_752_600_000 + i)))
        }
        XCTAssertEqual(try manager.backups(series: "claude_desktop_config").count, 3)
    }

    func testBackUpMissingSourceReturnsNil() throws {
        let missing = dir.appendingPathComponent("nope.json")
        XCTAssertNil(try manager.backUp(fileAt: missing, series: "claude_desktop_config"))
    }

    func testRotationKeepsNewestKeepCount() throws {
        for i in 0..<5 {
            try Data("v\(i)".utf8).write(to: source)
            try manager.backUp(fileAt: source, series: "claude_desktop_config",
                               now: Date(timeIntervalSince1970: Double(1_752_600_000 + i)))
        }
        let kept = try manager.backups(series: "claude_desktop_config")
        XCTAssertEqual(kept.count, 3)
        XCTAssertEqual(try String(contentsOf: kept[0], encoding: .utf8), "v4")
        XCTAssertEqual(try String(contentsOf: kept[2], encoding: .utf8), "v2")
    }

    /// Only files are backups: a folder named like the oldest one is neither listed nor pruned, nor is
    /// anything inside it, as on Windows.
    func testAFolderNamedLikeABackupIsNeitherListedNorPruned() throws {
        let folder = dir.appendingPathComponent("backups/claude_desktop_config.\(BackupTimestamp.string(from: Date(timeIntervalSince1970: 1_752_500_000))).json")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data("keep".utf8).write(to: folder.appendingPathComponent("inside.txt"))
        for i in 0..<4 {
            try Data("v\(i)".utf8).write(to: source)
            try manager.backUp(fileAt: source, series: "claude_desktop_config",
                               now: Date(timeIntervalSince1970: Double(1_752_600_000 + i)))
        }
        let kept = try manager.backups(series: "claude_desktop_config")
        XCTAssertEqual(try kept.map { try String(contentsOf: $0, encoding: .utf8) }, ["v3", "v2", "v1"])
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("inside.txt"), encoding: .utf8), "keep")
    }

    /// A stale backup pruning cannot delete (locked, read-only, whatever the
    /// cause) must not fail the save that triggered the prune; it is simply
    /// left behind for the next rotation to retry.
    func testRotationSkipsAStaleBackupThatCannotBeDeleted() throws {
        let limited = BackupManager(backupsDir: dir.appendingPathComponent("backups"), keepCount: 2)
        for i in 0..<2 {
            try Data("v\(i)".utf8).write(to: source)
            try limited.backUp(fileAt: source, series: "claude_desktop_config",
                               now: Date(timeIntervalSince1970: Double(1_752_600_000 + i)))
        }
        let stale = try XCTUnwrap(try limited.backups(series: "claude_desktop_config").last)
        XCTAssertEqual(chflags(stale.path, UInt32(UF_IMMUTABLE)), 0)
        defer { chflags(stale.path, 0) }
        // The fixture must actually block deletion, or this test proves nothing.
        XCTAssertThrowsError(try FileManager.default.removeItem(at: stale))

        try Data("v2".utf8).write(to: source)
        let made = try limited.backUp(fileAt: source, series: "claude_desktop_config",
                                      now: Date(timeIntervalSince1970: 1_752_600_002))
        XCTAssertNotNil(made, "the save must succeed even though pruning the stale backup failed")
        let remaining = try limited.backups(series: "claude_desktop_config")
        XCTAssertTrue(remaining.contains(stale), "the undeletable backup survives, to be retried next rotation")
        XCTAssertEqual(remaining.count, 3, "keepCount 2, plus the one stuck backup that could not be pruned")
    }

    func testOriginalSnapshotWrittenOnceAndNeverPruned() throws {
        try manager.ensureOriginalSnapshot(of: source)
        try Data("changed".utf8).write(to: source)
        try manager.ensureOriginalSnapshot(of: source)  // second call: no-op
        let original = manager.backupsDir
            .appendingPathComponent("claude_desktop_config.original.json")
        XCTAssertEqual(try String(contentsOf: original, encoding: .utf8),
                       #"{"mcpServers": {}}"#)
        for i in 0..<5 {
            try manager.backUp(fileAt: source, series: "claude_desktop_config",
                               now: Date(timeIntervalSince1970: Double(1_752_700_000 + i)))
        }
        XCTAssertTrue(FileManager.default.fileExists(atPath: original.path))
        XCTAssertFalse(try manager.backups(series: "claude_desktop_config")
            .contains { $0.lastPathComponent.contains(".original.") })
    }

    func testSameMillisecondBackupsBothSucceed() throws {
        let now = Date(timeIntervalSince1970: 1_752_600_000.123)
        try Data("v0".utf8).write(to: source)
        let first = try XCTUnwrap(manager.backUp(
            fileAt: source, series: "claude_desktop_config", now: now))
        try Data("v1".utf8).write(to: source)
        let second = try XCTUnwrap(manager.backUp(
            fileAt: source, series: "claude_desktop_config", now: now))
        XCTAssertNotEqual(first, second)
        XCTAssertTrue(FileManager.default.fileExists(atPath: first.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: second.path))
        XCTAssertEqual(try manager.backups(series: "claude_desktop_config").count, 2)
    }

    /// Two backups in the same millisecond: the second takes the `-2` name and is the newer, for
    /// the listing, for dedup's newest-snapshot comparison and for prune's keep-newest.
    func testSameMillisecondBackupsListNewestFirstAndPruneTheOldest() throws {
        let limited = BackupManager(backupsDir: dir.appendingPathComponent("backups"), keepCount: 2)
        let now = Date(timeIntervalSince1970: 1_752_600_000.123)
        for (content, at) in [("v0", now.addingTimeInterval(-1)), ("v1", now), ("v2", now)] {
            try Data(content.utf8).write(to: source)
            try limited.backUp(fileAt: source, series: "claude_desktop_config", now: at)
        }
        let kept = try limited.backups(series: "claude_desktop_config")
        XCTAssertEqual(try kept.map { try String(contentsOf: $0, encoding: .utf8) }, ["v2", "v1"],
                       "newest first, and the oldest is the one pruned")
        XCTAssertEqual(kept.first?.lastPathComponent, "claude_desktop_config.\(BackupTimestamp.string(from: now))-2.json")
        XCTAssertEqual(try limited.backUp(fileAt: source, series: "claude_desktop_config", now: now), kept.first,
                       "an unchanged file dedups against the -2 backup, the newest")
        XCTAssertEqual(try limited.backups(series: "claude_desktop_config"), kept)
    }

    /// Once every same-millisecond name is taken the write replaces the last one, which is already
    /// in the listing: prune counts it once, so exactly keepCount backups stay.
    func testAnExhaustedCollisionBoundPrunesNoBackupTooMany() throws {
        let full = BackupManager(backupsDir: dir.appendingPathComponent("backups"), keepCount: 100)
        let now = Date(timeIntervalSince1970: 1_752_600_000.123)
        for i in 0...100 {
            try Data("v\(i)".utf8).write(to: source)
            try full.backUp(fileAt: source, series: "claude_desktop_config", now: now)
        }
        let kept = try full.backups(series: "claude_desktop_config")
        XCTAssertEqual(kept.count, 100)
        XCTAssertEqual(try String(contentsOf: kept[0], encoding: .utf8), "v100", "the last write replaced the -100 backup")
    }

    /// The counter compares as a number: the tenth backup of a millisecond is newer than the second.
    func testASameMillisecondCounterComparesAsANumber() throws {
        let roomy = BackupManager(backupsDir: dir.appendingPathComponent("backups"), keepCount: 20)
        let now = Date(timeIntervalSince1970: 1_752_600_000.123)
        for i in 0..<11 {
            try Data("v\(i)".utf8).write(to: source)
            try roomy.backUp(fileAt: source, series: "claude_desktop_config", now: now)
        }
        XCTAssertEqual(try roomy.backups(series: "claude_desktop_config").map { try String(contentsOf: $0, encoding: .utf8) },
                       (0..<11).reversed().map { "v\($0)" })
    }

    /// A counter is digits and nothing else, as on Windows: a hand-made name with a sign after the
    /// stamp carries no counter, so its stamp is not one and nothing reads a time from it.
    func testACounterWithASignIsNotACounter() {
        let stamp = BackupTimestamp.string(from: Date(timeIntervalSince1970: 1_752_600_000.123))
        XCTAssertNotNil(BackupManager.takenAt(dir.appendingPathComponent("mcps.\(stamp)-5.json")))
        XCTAssertNil(BackupManager.takenAt(dir.appendingPathComponent("mcps.\(stamp)-+5.json")))
        XCTAssertNil(BackupManager.takenAt(dir.appendingPathComponent("mcps.\(stamp)--5.json")))
    }

    func testBackupsArePrivate() throws {
        let made = try XCTUnwrap(manager.backUp(fileAt: source, series: "mcps"))
        let mode = try XCTUnwrap(FileManager.default
            .attributesOfItem(atPath: made.path)[.posixPermissions] as? Int)
        XCTAssertEqual(mode, 0o600)
    }

    func testRestorableBackupsAreNewestFirstWithTheOriginalLast() throws {
        XCTAssertEqual(try manager.restorableBackups(series: "claude_desktop_config"), [])
        try manager.ensureOriginalSnapshot(of: source)
        let older = try XCTUnwrap(try manager.backUp(fileAt: source, series: "claude_desktop_config",
                                                     now: Date(timeIntervalSince1970: 1_752_600_000)))
        try Data(#"{"mcpServers": {"a": {}}}"#.utf8).write(to: source)
        let newer = try XCTUnwrap(try manager.backUp(fileAt: source, series: "claude_desktop_config",
                                                     now: Date(timeIntervalSince1970: 1_752_600_001)))
        let original = try XCTUnwrap(manager.originalSnapshotURL(series: "claude_desktop_config"))
        // resolvingSymlinksInPath: the directory listing returns /private/var URLs while the write
        // paths build /var ones - the same files.
        XCTAssertEqual(try manager.restorableBackups(series: "claude_desktop_config").map { $0.resolvingSymlinksInPath() },
                       [newer, older, original].map { $0.resolvingSymlinksInPath() })
    }

    /// Claude's own config is 0644; a copy would inherit that. The snapshot is
    /// written like every other private file instead.
    func testOriginalSnapshotOfAWorldReadableSourceIsPrivate() throws {
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: source.path)
        try manager.ensureOriginalSnapshot(of: source)
        let original = manager.backupsDir.appendingPathComponent("claude_desktop_config.original.json")
        let mode = try XCTUnwrap(FileManager.default
            .attributesOfItem(atPath: original.path)[.posixPermissions] as? Int)
        XCTAssertEqual(mode, 0o600)
    }

    /// A config symlinked into a dotfiles repo: the backup must be a snapshot
    /// of the bytes, not a copy of the link (which would read the live file
    /// forever, so no restore could ever go back).
    func testBackupOfASymlinkedSourceIsARealSnapshot() throws {
        let fm = FileManager.default
        let real = dir.appendingPathComponent("dotfiles/claude.json")
        try fm.createDirectory(at: real.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("v1".utf8).write(to: real)
        let link = dir.appendingPathComponent("linked_config.json")
        try fm.createSymbolicLink(at: link, withDestinationURL: real)
        let made = try XCTUnwrap(manager.backUp(fileAt: link, series: "claude_desktop_config"))
        XCTAssertNil(try? fm.destinationOfSymbolicLink(atPath: made.path), "the backup is a regular file")
        try Data("v2".utf8).write(to: real)
        XCTAssertEqual(try String(contentsOf: made, encoding: .utf8), "v1", "the snapshot does not follow the live file")
    }

    func testSeriesAreIndependent() throws {
        try manager.backUp(fileAt: source, series: "claude_desktop_config")
        try manager.backUp(fileAt: source, series: "mcps")
        XCTAssertEqual(try manager.backups(series: "claude_desktop_config").count, 1)
        XCTAssertEqual(try manager.backups(series: "mcps").count, 1)
    }

    // MARK: - Pinned backups

    private let day: TimeInterval = 86_400
    private let t0 = Date(timeIntervalSince1970: 1_752_600_000)

    /// Backs up `source` holding `content`, `at` seconds after `t0`.
    @discardableResult
    private func backUp(_ content: String, at offset: TimeInterval, series: String = "mcps") throws -> URL {
        try Data(content.utf8).write(to: source)
        return try XCTUnwrap(manager.backUp(fileAt: source, series: series, now: t0.addingTimeInterval(offset)))
    }

    func testAPinnedBackupSurvivesPastTheKeepCount() throws {
        let first = try backUp("1", at: 0)
        try BackupPins.pin(first, in: manager.backupsDir, staging: nil)
        for i in 2...6 { try backUp("\(i)", at: Double(i)) }
        let names = try manager.backups(series: "mcps").map(\.lastPathComponent)
        XCTAssertEqual(names.count, 4, "the three newest and the pinned one")
        XCTAssertTrue(names.contains(first.lastPathComponent))
        XCTAssertEqual(BackupPins.pinned(in: manager.backupsDir), [first.lastPathComponent])
    }

    func testAPinnedBackupStillAmongTheNewestCountsAsOneOfThem() throws {
        let first = try backUp("1", at: 0)
        try BackupPins.pin(first, in: manager.backupsDir, staging: nil)
        try backUp("2", at: 1)
        try backUp("3", at: 2)
        XCTAssertEqual(try manager.backups(series: "mcps").count, 3)
        try backUp("4", at: 3)   // the pinned one is now the fourth newest and stays
        XCTAssertEqual(try manager.backups(series: "mcps").count, 4)
    }

    func testAPinExpiresThirtyDaysAfterTheBackupWasTaken() throws {
        let first = try backUp("1", at: 0)
        try BackupPins.pin(first, in: manager.backupsDir, staging: nil)
        try backUp("2", at: 1)
        try backUp("3", at: 2)
        try backUp("4", at: 29 * day)
        XCTAssertTrue(FileManager.default.fileExists(atPath: first.path), "29 days on, it is still kept")
        try backUp("5", at: 31 * day)
        XCTAssertFalse(FileManager.default.fileExists(atPath: first.path), "31 days on, it prunes as any other")
    }

    func testAPinForAPrunedOrMissingFileIsDroppedOnTheNextPin() throws {
        let first = try backUp("1", at: 0)
        try BackupPins.pin(first, in: manager.backupsDir, staging: nil)
        for i in 2...5 { try backUp("\(i)", at: Double(i)) }
        try BackupPins.pin(manager.backupsDir.appendingPathComponent("mcps.nowhere.json"), in: manager.backupsDir, staging: nil)
        XCTAssertEqual(BackupPins.pinned(in: manager.backupsDir), ["mcps.nowhere.json", first.lastPathComponent])
        try FileManager.default.removeItem(at: first)
        let newest = try XCTUnwrap(try manager.backups(series: "mcps").first)
        try BackupPins.pin(newest, in: manager.backupsDir, staging: nil)
        XCTAssertEqual(BackupPins.pinned(in: manager.backupsDir), [newest.lastPathComponent],
                       "both the deleted file's pin and the missing one's are gone")
    }

    func testAnUnreadablePinIndexPinsNothingAndDoesNotFailABackup() throws {
        let first = try backUp("1", at: 0)
        try Data("{not json".utf8).write(to: manager.backupsDir.appendingPathComponent(BackupPins.fileName))
        XCTAssertEqual(BackupPins.pinned(in: manager.backupsDir), [])
        for i in 2...6 { try backUp("\(i)", at: Double(i)) }
        XCTAssertFalse(FileManager.default.fileExists(atPath: first.path), "nothing was pinned, so it pruned")
        XCTAssertEqual(try manager.backups(series: "mcps").count, 3)
    }

    func testPinningAnAlreadyPinnedBackupWritesNothing() throws {
        let first = try backUp("1", at: 0)
        try BackupPins.pin(first, in: manager.backupsDir, staging: nil)
        let file = manager.backupsDir.appendingPathComponent(BackupPins.fileName)
        let before = try Data(contentsOf: file)
        try BackupPins.pin(first, in: manager.backupsDir, staging: nil)
        XCTAssertEqual(try Data(contentsOf: file), before)
        XCTAssertEqual(try manager.backups(series: "mcps").count, 1, "the index is not listed as a backup")
    }

    // MARK: - Labels

    private func named(_ stamp: String, series: String = "mcps") -> URL {
        dir.appendingPathComponent("\(series).\(stamp).json")
    }

    func testLabelsAreLocalTimeToTheSecondWhenUnique() throws {
        let a = named("2026-11-12T16-19-13-493Z"), b = named("2026-11-12T16-19-14-493Z")
        let labels = BackupManager.labels(for: [a, b])
        XCTAssertEqual(labels[a.lastPathComponent], IsoTimestamp.localDateTimeSeconds(from: try XCTUnwrap(BackupManager.takenAt(a))))
        XCTAssertNotEqual(labels[a.lastPathComponent], labels[b.lastPathComponent])
        XCTAssertEqual(labels[a.lastPathComponent]?.count, "2026-11-12 11:19:13".count)
    }

    func testBackupsOfOneSecondCarryMilliseconds() throws {
        let a = named("2026-11-12T16-19-13-493Z"), b = named("2026-11-12T16-19-13-871Z"), c = named("2026-11-12T16-19-14-000Z")
        let labels = BackupManager.labels(for: [a, b, c])
        let second = IsoTimestamp.localDateTimeSeconds(from: try XCTUnwrap(BackupManager.takenAt(a)))
        XCTAssertEqual(labels[a.lastPathComponent], "\(second).493")
        XCTAssertEqual(labels[b.lastPathComponent], "\(second).871")
        XCTAssertEqual(labels[c.lastPathComponent], IsoTimestamp.localDateTimeSeconds(from: try XCTUnwrap(BackupManager.takenAt(c))),
                       "a backup of another second keeps the short label")
    }

    func testBackupsOfOneMillisecondAddTheirCounter() throws {
        let a = named("2026-11-12T16-19-13-493Z"), b = named("2026-11-12T16-19-13-493Z-2"), c = named("2026-11-12T16-19-13-493Z-3")
        let labels = BackupManager.labels(for: [c, a, b])
        let base = "\(IsoTimestamp.localDateTimeSeconds(from: try XCTUnwrap(BackupManager.takenAt(a)))).493"
        XCTAssertEqual(labels[a.lastPathComponent], base)
        XCTAssertEqual(labels[b.lastPathComponent], "\(base) (2)")
        XCTAssertEqual(labels[c.lastPathComponent], "\(base) (3)")
    }

    func testANameWithNoTimeIsItsOwnLabel() {
        let original = dir.appendingPathComponent("mcps.original.json")
        XCTAssertEqual(BackupManager.labels(for: [original]), ["mcps.original.json": "mcps.original.json"])
    }
}

