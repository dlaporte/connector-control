import XCTest
@testable import ConnectorControlCore

final class MasterStoreTests: XCTestCase {
    var dir: URL!
    var url: URL { dir.appendingPathComponent("mcps.json") }

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("store-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    func testEnabledServersRendersEnabledSubset() {
        let store = MasterStore(version: 2, mcps: [
            "on": MCPEntry(enabled: true, config: .object(["command": .string("a")])),
            "off": MCPEntry(enabled: false, config: .object(["command": .string("b")]))])
        XCTAssertEqual(store.enabledServers, ["on": .object(["command": .string("a")])])
    }

    func testLoadMissingFileReturnsEmptyStore() {
        let result = MasterStoreIO.load(from: url)
        XCTAssertEqual(result.store, .empty)
        XCTAssertNil(result.corruptFileURL)
    }

    func testSaveThenLoadRoundTrips() throws {
        var store = MasterStore.empty
        store.mcps["scoutbook"] = MCPEntry(
            enabled: false,
            config: .object(["command": .string("npx"),
                             "args": .array([.string("-y"), .string("mcp-remote"),
                                             .string("https://example.com/mcp")])]),
            lastEditView: .json)
        try MasterStoreIO.save(store, to: url)
        let result = MasterStoreIO.load(from: url)
        XCTAssertEqual(result.store, store)
        XCTAssertNil(result.corruptFileURL)
    }

    func testLoadCorruptFilePreservesItAndReturnsEmpty() throws {
        try Data("{not json!!".utf8).write(to: url)
        let result = MasterStoreIO.load(from: url)
        XCTAssertEqual(result.store, .empty)
        let corrupt = try XCTUnwrap(result.corruptFileURL)
        XCTAssertTrue(corrupt.lastPathComponent.hasPrefix("mcps.corrupt."))
        XCTAssertEqual(try String(contentsOf: corrupt, encoding: .utf8), "{not json!!")
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
    }

    func testReadIsSideEffectFree() throws {
        // Missing file → nil, nothing created.
        XCTAssertNil(MasterStoreIO.read(from: url))
        // Corrupt file → nil, file left exactly in place (unlike load, which
        // moves it aside — the watcher must be able to peek at a sync tool's
        // mid-write partial without destroying it).
        try Data("{not json!!".utf8).write(to: url)
        XCTAssertNil(MasterStoreIO.read(from: url))
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), "{not json!!")
        // Valid file → decoded store.
        var store = MasterStore.empty
        store.mcps["s"] = MCPEntry(config: .object(["command": .string("npx")]))
        try MasterStoreIO.save(store, to: url)
        XCTAssertEqual(MasterStoreIO.read(from: url), store)
    }

    func testBackupTimestampsSortChronologicallyAcrossDSTFallBack() {
        // 2026-11-01 America/New_York repeats 01:00–02:00 wall-clock; UTC
        // stamps must stay strictly increasing regardless.
        let start = Date(timeIntervalSince1970: 1_793_500_000)  // hours before
        var previous = ""
        for step in 0..<10 {
            let stamp = BackupTimestamp.string(
                from: start.addingTimeInterval(Double(step) * 1800))
            XCTAssertGreaterThan(stamp, previous)
            previous = stamp
        }
    }

    func testLoadCorruptFileReportsOriginalPathWhenMoveFails() throws {
        let fixedNow = Date(timeIntervalSince1970: 1_752_600_000)
        let garbage = "{not json!!"
        try Data(garbage.utf8).write(to: url)

        // Pre-create the aside file so moveItem will fail due to name collision.
        let stamp = BackupTimestamp.string(from: fixedNow)
        let aside = url.deletingLastPathComponent()
            .appendingPathComponent("mcps.corrupt.\(stamp).json")
        try Data("existing".utf8).write(to: aside)

        let result = MasterStoreIO.load(from: url, now: fixedNow)
        XCTAssertEqual(result.store, .empty)
        XCTAssertEqual(result.corruptFileURL, url)
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), garbage)
    }
}
