import XCTest
@testable import MCPEnablerCore

final class ConfigServiceTests: XCTestCase {
    var dir: URL!
    var paths: AppPaths!
    var service: ConfigService!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("svc-\(UUID().uuidString)")
        let claudeDir = dir.appendingPathComponent("Claude")
        try FileManager.default.createDirectory(at: claudeDir, withIntermediateDirectories: true)
        paths = AppPaths(
            claudeConfigURL: claudeDir.appendingPathComponent("claude_desktop_config.json"),
            storeDirURL: dir.appendingPathComponent("MCP Enabler"))
        try Data(Fixtures.realisticClaudeConfig.utf8).write(to: paths.claudeConfigURL)
        service = ConfigService(paths: paths)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    func testFirstLoadImportsAllServersEnabled() throws {
        let result = try service.loadAndReconcile()
        XCTAssertEqual(Set(result.store.mcps.keys),
                       ["scoutbook", "aws-mcp", "service-now"])
        XCTAssertTrue(result.store.mcps.values.allSatisfy(\.enabled))
        XCTAssertEqual(result.missingEnabled, [])
        XCTAssertEqual(result.claudeServers.count, 3)
        // reconciled store was persisted
        XCTAssertEqual(MasterStoreIO.load(from: paths.masterStoreURL).store, result.store)
    }

    func testApplyWritesEnabledSubsetWithBackups() throws {
        var store = try service.loadAndReconcile().store
        store.mcps["aws-mcp"]?.enabled = false
        try service.apply(store)
        XCTAssertEqual(Set(try ClaudeConfigIO.readMCPServers(at: paths.claudeConfigURL).keys),
                       ["scoutbook", "service-now"])
        // non-MCP keys survived
        let root = try XCTUnwrap(JSONSerialization.jsonObject(
            with: Data(contentsOf: paths.claudeConfigURL)) as? [String: Any])
        XCTAssertNotNil(root["preferences"])
        XCTAssertNotNil(root["someFutureKey"])
        // backups exist: original + timestamped
        let backups = service.backups
        XCTAssertEqual(try backups.backups(series: "claude_desktop_config").count, 1)
        XCTAssertTrue(FileManager.default.fileExists(atPath: backups.backupsDir
            .appendingPathComponent("claude_desktop_config.original.json").path))
    }

    func testSaveStoreBacksUpPreviousVersion() throws {
        let store = try service.loadAndReconcile().store
        try service.saveStore(store)  // first explicit save; store file already exists
        XCTAssertEqual(try service.backups.backups(series: "mcps").count, 1)
    }

    func testWipeRecoveryFlow() throws {
        let store = try service.loadAndReconcile().store
        // Claude wipes the file to a preferences-only stub (issue #32345 shape)
        try Data(#"{"preferences": {}}"#.utf8).write(to: paths.claudeConfigURL)
        let result = try service.loadAndReconcile()
        XCTAssertEqual(result.missingEnabled, ["aws-mcp", "scoutbook", "service-now"])
        XCTAssertEqual(result.store.mcps.count, 3, "nothing deleted")
        // restore: apply the store puts them back, preserving the stub's keys
        try service.apply(store)
        XCTAssertEqual(try ClaudeConfigIO.readMCPServers(at: paths.claudeConfigURL).count, 3)
    }

    func testCorruptMasterStoreIsRebuiltWithNote() throws {
        _ = try service.loadAndReconcile()
        try Data("garbage".utf8).write(to: paths.masterStoreURL)
        let result = try service.loadAndReconcile()
        XCTAssertEqual(result.store.mcps.count, 3, "rebuilt from Claude's config")
        XCTAssertEqual(result.notes.count, 1)
        XCTAssertTrue(result.notes[0].contains("mcps.corrupt."))
    }

    func testRestoreClaudeConfigFromBackup() throws {
        var store = try service.loadAndReconcile().store
        store.mcps["aws-mcp"]?.enabled = false
        try service.apply(store)  // creates a backup of the 3-server file
        let backup = try XCTUnwrap(
            try service.backups.backups(series: "claude_desktop_config").first)
        try service.restoreClaudeConfig(from: backup, mergedWith: store)
        XCTAssertEqual(try ClaudeConfigIO.readMCPServers(at: paths.claudeConfigURL).count, 3)
    }

    func testRestoreRefusesMalformedBackup() throws {
        let store = try service.loadAndReconcile().store
        let badBackup = dir.appendingPathComponent("bad-backup.json")
        try Data("{not json".utf8).write(to: badBackup)
        let before = try Data(contentsOf: paths.claudeConfigURL)
        XCTAssertThrowsError(try service.restoreClaudeConfig(
            from: badBackup, mergedWith: store)) {
            guard case ClaudeConfigError.malformed = $0 else {
                return XCTFail("wrong error: \($0)")
            }
        }
        XCTAssertEqual(try Data(contentsOf: paths.claudeConfigURL), before,
                       "live config must be untouched after refused restore")
    }
}
