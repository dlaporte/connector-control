import XCTest
@testable import ConnectorControlCore

final class AppPathsTests: XCTestCase {
    /// The real default `appSupport`, for tests that want AppPaths.live's
    /// actual home-directory behavior rather than a throwaway one.
    private var realAppSupport: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support")
    }

    func testLiveDefaultsPointAtClaudeAndConnectorControl() {
        let paths = AppPaths.live(environment: [:], appSupport: realAppSupport)
        XCTAssertTrue(paths.claudeConfigURL.path.hasSuffix(
            "Library/Application Support/Claude/claude_desktop_config.json"))
        XCTAssertTrue(paths.storeDirURL.path.hasSuffix(
            "Library/Application Support/Connector Control"))
        XCTAssertEqual(paths.masterStoreURL.lastPathComponent, "mcps.json")
        XCTAssertEqual(paths.backupsDirURL.lastPathComponent, "backups")
    }

    func testEnvironmentOverrides() {
        let paths = AppPaths.live(environment: [
            "CONNECTOR_CONTROL_CLAUDE_CONFIG": "/tmp/x/claude.json",
            "CONNECTOR_CONTROL_STORE_DIR": "/tmp/x/store",
        ], appSupport: realAppSupport)
        XCTAssertEqual(paths.claudeConfigURL.path, "/tmp/x/claude.json")
        XCTAssertEqual(paths.storeDirURL.path, "/tmp/x/store")
        XCTAssertEqual(paths.masterStoreURL.path, "/tmp/x/store/mcps.json")
        XCTAssertEqual(paths.backupsDirURL.path, "/tmp/x/store/backups")
        XCTAssertEqual(paths.stagingDirURL.path, "/tmp/x/store/.staging")
    }

    /// A CI/sandbox environment that inherits the variable but leaves it unset
    /// must not shadow the real default.
    func testEmptyOverridesCountAsAbsent() {
        let paths = AppPaths.live(environment: [
            "CONNECTOR_CONTROL_CLAUDE_CONFIG": "",
            "CONNECTOR_CONTROL_STORE_DIR": "",
        ], appSupport: realAppSupport)
        XCTAssertTrue(paths.claudeConfigURL.path.hasSuffix(
            "Library/Application Support/Claude/claude_desktop_config.json"))
        XCTAssertTrue(paths.storeDirURL.path.hasSuffix(
            "Library/Application Support/Connector Control"))
    }

    func testExplicitBackupsDirURLIsHonoredIndependentlyOfStoreDir() {
        let paths = AppPaths(
            claudeConfigURL: URL(fileURLWithPath: "/tmp/x/claude.json"),
            storeDirURL: URL(fileURLWithPath: "/tmp/x/store"),
            backupsDirURL: URL(fileURLWithPath: "/tmp/machine-local/backups"))
        XCTAssertEqual(paths.backupsDirURL.path, "/tmp/machine-local/backups")
        XCTAssertEqual(paths.masterStoreURL.path, "/tmp/x/store/mcps.json")
        XCTAssertEqual(paths.stagingDirURL.path, "/tmp/x/store/.staging", "defaults under the store dir")
    }

    /// The staging folder is the app's own, like backups: a chosen (synced) store dir never
    /// gets it, so `makeService` passes the machine-local one explicitly.
    func testExplicitStagingDirURLIsHonoredIndependentlyOfStoreDir() {
        let paths = AppPaths(
            claudeConfigURL: URL(fileURLWithPath: "/tmp/x/claude.json"),
            storeDirURL: URL(fileURLWithPath: "/tmp/x/store"),
            stagingDirURL: URL(fileURLWithPath: "/tmp/machine-local/.staging"))
        XCTAssertEqual(paths.stagingDirURL.path, "/tmp/machine-local/.staging")
        XCTAssertEqual(paths.backupsDirURL.path, "/tmp/x/store/backups")
    }

    func testLiveHonoursTheAppSupportDirectory() {
        let home = URL(fileURLWithPath: "/tmp/fake-home/Library/Application Support")
        let paths = AppPaths.live(environment: [:], appSupport: home)
        XCTAssertEqual(paths.claudeConfigURL.path,
                       "/tmp/fake-home/Library/Application Support/Claude/claude_desktop_config.json")
        XCTAssertEqual(paths.storeDirURL.path, "/tmp/fake-home/Library/Application Support/Connector Control")
        XCTAssertEqual(paths.backupsDirURL.path,
                       "/tmp/fake-home/Library/Application Support/Connector Control/backups")
        let overridden = AppPaths.live(environment: ["CONNECTOR_CONTROL_STORE_DIR": "/tmp/x/store"], appSupport: home)
        XCTAssertEqual(overridden.storeDirURL.path, "/tmp/x/store", "the env override still beats the home directory")
        XCTAssertEqual(overridden.claudeConfigURL.path,
                       "/tmp/fake-home/Library/Application Support/Claude/claude_desktop_config.json")
    }
}
