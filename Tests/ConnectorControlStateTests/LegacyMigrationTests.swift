import XCTest
@testable import ConnectorControlState

final class LegacyMigrationTests: XCTestCase {
    private let fm = FileManager.default

    private func makeDir(_ url: URL, marker: String) throws {
        try fm.createDirectory(at: url, withIntermediateDirectories: true)
        try Data(marker.utf8).write(to: url.appendingPathComponent("mcps.json"))
    }

    /// Catalog §1.14 step 1: newest name first, and only the first existing
    /// old directory moves — once "Connector Control" exists, the check fails
    /// for the older name.
    func testMovesTheNewestLegacyDirectoryOnly() throws {
        let dir = TempDir(prefix: "migrate")
        defer { dir.dispose() }
        let appSupport = dir.file("Library/Application Support")
        try makeDir(appSupport.appendingPathComponent("Custom Connector Control"), marker: "custom")
        try makeDir(appSupport.appendingPathComponent("MCP Enabler"), marker: "enabler")

        LegacyMigration.moveLegacyDirectory(appSupport: appSupport)

        let new = appSupport.appendingPathComponent("Connector Control/mcps.json")
        XCTAssertEqual(try String(contentsOf: new, encoding: .utf8), "custom")
        XCTAssertFalse(fm.fileExists(atPath: appSupport.appendingPathComponent("Custom Connector Control").path))
        XCTAssertTrue(fm.fileExists(atPath: appSupport.appendingPathComponent("MCP Enabler/mcps.json").path),
                      "the older name stays where it was")
    }

    func testLeavesAnExistingDirectoryAlone() throws {
        let dir = TempDir(prefix: "migrate")
        defer { dir.dispose() }
        let appSupport = dir.file("Library/Application Support")
        try makeDir(appSupport.appendingPathComponent("Connector Control"), marker: "current")
        try makeDir(appSupport.appendingPathComponent("MCP Enabler"), marker: "enabler")

        LegacyMigration.moveLegacyDirectory(appSupport: appSupport)

        XCTAssertEqual(try String(contentsOf: appSupport.appendingPathComponent("Connector Control/mcps.json"), encoding: .utf8), "current")
        XCTAssertTrue(fm.fileExists(atPath: appSupport.appendingPathComponent("MCP Enabler/mcps.json").path))
        // Idempotent: a second run with nothing to move changes nothing.
        LegacyMigration.moveLegacyDirectory(appSupport: appSupport)
        XCTAssertEqual(try String(contentsOf: appSupport.appendingPathComponent("Connector Control/mcps.json"), encoding: .utf8), "current")
    }
}
