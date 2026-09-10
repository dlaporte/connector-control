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

    /// The store directory can be a folder the user chose (a repo, iCloud Drive): only the
    /// app's own files there are repaired, nothing else in it or below it, and its mode is
    /// left alone. The backups directory is always the app's and is swept in full.
    func testAChosenStoreDirectoryKeepsItsOwnFilesAndMode() throws {
        let dir = TempDir(prefix: "sweep")
        defer { dir.dispose() }
        let fm = FileManager.default
        let chosen = dir.file("Dropbox/connectors")
        let backups = dir.file("Library/Connector Control/backups")
        let paths = AppPaths(claudeConfigURL: dir.file("claude.json"), storeDirURL: chosen, backupsDirURL: backups)
        let strangerDir = chosen.appendingPathComponent("photos")
        try fm.createDirectory(at: strangerDir, withIntermediateDirectories: true)
        try fm.createDirectory(at: backups, withIntermediateDirectories: true)
        let strangerFile = chosen.appendingPathComponent("deploy.sh")
        let nestedStranger = strangerDir.appendingPathComponent("a.jpg")
        let corrupt = chosen.appendingPathComponent("mcps.corrupt.2026-09-10T00-00-00-000Z.json")
        let backup = backups.appendingPathComponent("mcps.2026-09-10T00-00-00-000Z.json")
        for url in [paths.masterStoreURL, strangerFile, nestedStranger, corrupt, backup] {
            try Data("{}".utf8).write(to: url)
        }
        try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: chosen.path)
        try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: strangerFile.path)   // an executable script
        try fm.setAttributes([.posixPermissions: 0o644], ofItemAtPath: nestedStranger.path)
        for url in [paths.masterStoreURL, corrupt, backup] {
            try fm.setAttributes([.posixPermissions: 0o644], ofItemAtPath: url.path)
        }
        let settings = FakeSettings()
        settings.masterStoreDir = chosen.path

        XCTAssertTrue(PermissionsSweep.runOnce(settings: settings, paths: paths))
        XCTAssertEqual(try mode(chosen), 0o755, "a chosen folder's own mode is not the app's to change")
        XCTAssertEqual(try mode(strangerFile), 0o755, "other files in the chosen folder are untouched")
        XCTAssertEqual(try mode(nestedStranger), 0o644, "nothing below the chosen folder is touched")
        XCTAssertEqual(try mode(paths.masterStoreURL), 0o600)
        XCTAssertEqual(try mode(corrupt), 0o600)
        XCTAssertEqual(try mode(backups), 0o700)
        XCTAssertEqual(try mode(backup), 0o600)
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
