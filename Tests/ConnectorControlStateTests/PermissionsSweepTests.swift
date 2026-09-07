import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

final class PermissionsSweepTests: XCTestCase {
    private func mode(_ url: URL) throws -> Int {
        try XCTUnwrap(FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? Int)
    }

    func testSweepsEveryFileAndDirectoryOnceAndSetsTheFlag() throws {
        let dir = TempDir(prefix: "sweep")
        defer { dir.dispose() }
        let paths = AppPaths(claudeConfigURL: dir.file("claude.json"), storeDirURL: dir.file("store"))
        let fm = FileManager.default
        let nested = paths.backupsDirURL.appendingPathComponent("nested")
        try fm.createDirectory(at: nested, withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: paths.masterStoreURL)
        let nestedFile = nested.appendingPathComponent("b.json")
        try Data("{}".utf8).write(to: nestedFile)
        // Start from the world-readable modes a pre-fix build left behind.
        for url in [paths.storeDirURL, paths.backupsDirURL, nested] {
            try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        }
        for url in [paths.masterStoreURL, nestedFile] {
            try fm.setAttributes([.posixPermissions: 0o644], ofItemAtPath: url.path)
        }
        let settings = FakeSettings()

        XCTAssertTrue(PermissionsSweep.runOnce(settings: settings, paths: paths))
        XCTAssertTrue(settings.permissionsSweepDone)
        XCTAssertEqual(try mode(paths.storeDirURL), 0o700)
        XCTAssertEqual(try mode(paths.masterStoreURL), 0o600)
        XCTAssertEqual(try mode(paths.backupsDirURL), 0o700)
        XCTAssertEqual(try mode(nested), 0o700)
        XCTAssertEqual(try mode(nestedFile), 0o600)
        XCTAssertFalse(PermissionsSweep.runOnce(settings: settings, paths: paths), "gated by the flag from now on")
    }

    func testMissingDirectoriesAreToleratedAndStillMarkTheSweepDone() {
        let dir = TempDir(prefix: "sweep")
        defer { dir.dispose() }
        let paths = AppPaths(claudeConfigURL: dir.file("claude.json"), storeDirURL: dir.file("never-created"))
        let settings = FakeSettings()
        XCTAssertTrue(PermissionsSweep.runOnce(settings: settings, paths: paths))
        XCTAssertTrue(settings.permissionsSweepDone)
    }
}
