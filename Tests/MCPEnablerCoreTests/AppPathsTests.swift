import XCTest
@testable import MCPEnablerCore

final class AppPathsTests: XCTestCase {
    func testLiveDefaultsPointAtClaudeAndMCPEnabler() {
        let paths = AppPaths.live(environment: [:])
        XCTAssertTrue(paths.claudeConfigURL.path.hasSuffix(
            "Library/Application Support/Claude/claude_desktop_config.json"))
        XCTAssertTrue(paths.storeDirURL.path.hasSuffix(
            "Library/Application Support/Custom Connector Control"))
        XCTAssertEqual(paths.masterStoreURL.lastPathComponent, "mcps.json")
        XCTAssertEqual(paths.backupsDirURL.lastPathComponent, "backups")
    }

    func testEnvironmentOverrides() {
        let paths = AppPaths.live(environment: [
            "MCP_ENABLER_CLAUDE_CONFIG": "/tmp/x/claude.json",
            "MCP_ENABLER_STORE_DIR": "/tmp/x/store",
        ])
        XCTAssertEqual(paths.claudeConfigURL.path, "/tmp/x/claude.json")
        XCTAssertEqual(paths.storeDirURL.path, "/tmp/x/store")
        XCTAssertEqual(paths.masterStoreURL.path, "/tmp/x/store/mcps.json")
        XCTAssertEqual(paths.backupsDirURL.path, "/tmp/x/store/backups")
    }

    func testExplicitBackupsDirURLIsHonoredIndependentlyOfStoreDir() {
        let paths = AppPaths(
            claudeConfigURL: URL(fileURLWithPath: "/tmp/x/claude.json"),
            storeDirURL: URL(fileURLWithPath: "/tmp/x/store"),
            backupsDirURL: URL(fileURLWithPath: "/tmp/machine-local/backups"))
        XCTAssertEqual(paths.backupsDirURL.path, "/tmp/machine-local/backups")
        XCTAssertEqual(paths.masterStoreURL.path, "/tmp/x/store/mcps.json")
    }
}
