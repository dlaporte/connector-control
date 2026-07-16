import XCTest
@testable import MCPEnablerCore

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

    func testBackUpCreatesTimestampedCopy() throws {
        let made = try XCTUnwrap(manager.backUp(
            fileAt: source, series: "claude_desktop_config",
            now: Date(timeIntervalSince1970: 1_752_600_000)))
        XCTAssertTrue(made.lastPathComponent.hasPrefix("claude_desktop_config."))
        XCTAssertTrue(made.lastPathComponent.hasSuffix(".json"))
        XCTAssertEqual(try Data(contentsOf: made), try Data(contentsOf: source))
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

    func testSeriesAreIndependent() throws {
        try manager.backUp(fileAt: source, series: "claude_desktop_config")
        try manager.backUp(fileAt: source, series: "mcps")
        XCTAssertEqual(try manager.backups(series: "claude_desktop_config").count, 1)
        XCTAssertEqual(try manager.backups(series: "mcps").count, 1)
    }
}
