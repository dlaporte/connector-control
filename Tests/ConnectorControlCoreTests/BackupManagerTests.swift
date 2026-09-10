import XCTest
@testable import ConnectorControlCore

final class BackupManagerTests: XCTestCase {
    var dir: URL!
    var source: URL!
    var manager: BackupManager!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("backups-\(UUID().uuidString)")
        source = dir.appendingPathComponent("claude_desktop_config.json")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try Data(#"{"mcpServers": {}}"#.utf8).write(to: source)
        manager = BackupManager(backupsDir: dir.appendingPathComponent("backups"), keepCount: 3)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
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
        // URLs while the write path builds /var ones — same file.
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
        // A → B → back to A: the return to A still records — dedup compares
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

    func testBackupsArePrivate() throws {
        let made = try XCTUnwrap(manager.backUp(fileAt: source, series: "mcps"))
        let mode = try XCTUnwrap(FileManager.default
            .attributesOfItem(atPath: made.path)[.posixPermissions] as? Int)
        XCTAssertEqual(mode, 0o600)
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
}
