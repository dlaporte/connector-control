import XCTest
import ConnectorControlTestSupport
@testable import ConnectorControlCore

final class MasterStoreTests: XCTestCase {
    var tempDir: TempDir!
    var dir: URL!
    var url: URL { dir.appendingPathComponent("mcps.json") }

    override func setUpWithError() throws {
        tempDir = TempDir(prefix: "store")
        dir = tempDir.url
    }

    override func tearDownWithError() throws {
        tempDir.dispose()
    }

    func testEnabledServersRendersEnabledSubset() {
        let store = MasterStore.single([
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
            config: RemotePattern.make(url: "https://example.com/mcp"),
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

    func testBackupTimestampFormat() throws {
        var components = DateComponents()
        components.year = 2025
        components.month = 7
        components.day = 15
        components.hour = 17
        components.minute = 20
        components.second = 0
        components.nanosecond = 123_000_000
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        let date = try XCTUnwrap(calendar.date(from: components))
        XCTAssertEqual(BackupTimestamp.string(from: date), "2025-07-15T17-20-00-123Z")
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

    func testUnknownActiveProfileFallsBackToExistingProfile() throws {
        let json = """
        {"version":2,"activeProfile":"Ghost",\
        "profiles":{"Alpha":{"mcps":{}},"Beta":{"mcps":{}}}}
        """
        try Data(json.utf8).write(to: url)
        let result = MasterStoreIO.load(from: url)
        XCTAssertNil(result.corruptFileURL)
        XCTAssertEqual(result.store.activeProfile, "Alpha", "sorted-first existing profile")
    }

    func testV1FormatFileIsTreatedAsCorruptAndRebuilt() throws {
        // No v1 compatibility: an old-format file can't decode against the
        // v2-only schema, so it flows through the existing corrupt-file path
        // (moved aside, empty store returned) rather than being migrated.
        let json = """
        {"version":1,"mcps":{"scoutbook":{"enabled":true,\
        "config":{"command":"npx","args":["-y","mcp-remote","https://example.com/mcp"]},\
        "lastEditView":"form"}}}
        """
        try Data(json.utf8).write(to: url)
        let result = MasterStoreIO.load(from: url)
        XCTAssertEqual(result.store, .empty)
        let corrupt = try XCTUnwrap(result.corruptFileURL)
        XCTAssertTrue(corrupt.lastPathComponent.hasPrefix("mcps.corrupt."))
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
    }

    /// MasterStore has no custom `init(from:)` — decoding is exactly as
    /// strict as the synthesized Codable conformance, so every one of these
    /// six malformed shapes fails to decode and flows through the same
    /// corrupt-file path as garbage bytes: moved aside, empty store returned.
    func testRequiredKeysAreStrictLikeCodable() throws {
        let malformed = [
            // missing "version"
            #"{"activeProfile":"Default","profiles":{"Default":{"mcps":{}}}}"#,
            // missing "activeProfile"
            #"{"version":2,"profiles":{"Default":{"mcps":{}}}}"#,
            // missing "profiles"
            #"{"version":2,"activeProfile":"Default"}"#,
            // "version" is a string, not an Int
            #"{"version":"2","activeProfile":"Default","profiles":{"Default":{"mcps":{}}}}"#,
            // "profiles" is an array, not an object
            #"{"version":2,"activeProfile":"Default","profiles":[]}"#,
            // an entry missing its required "config"
            #"{"version":2,"activeProfile":"Default","profiles":{"Default":{"mcps":{"a":{"enabled":true,"lastEditView":"form"}}}}}"#,
        ]
        for (index, json) in malformed.enumerated() {
            try Data(json.utf8).write(to: url)
            // A distinct `now` per case: two loads in the same millisecond would
            // otherwise collide on the same corrupt-file name and prove nothing.
            let result = MasterStoreIO.load(from: url, now: Date(timeIntervalSince1970: 1_800_000_000 + Double(index)))
            XCTAssertEqual(result.store, .empty, "case \(index): \(json)")
            let corrupt = try XCTUnwrap(result.corruptFileURL, "case \(index): \(json)")
            XCTAssertTrue(corrupt.lastPathComponent.hasPrefix("mcps.corrupt."), "case \(index)")
        }
    }

    func testUnknownKeysAreIgnored() throws {
        let json = """
        {"version":2,"activeProfile":"Default",\
        "profiles":{"Default":{"mcps":{}}},"unknownField":"surprise"}
        """
        try Data(json.utf8).write(to: url)
        let result = MasterStoreIO.load(from: url)
        XCTAssertNil(result.corruptFileURL)
        XCTAssertEqual(result.store, .empty)
    }

    /// MasterStore.version is a Swift Int (64-bit), unlike a 32-bit field:
    /// a value past Int32.max still decodes.
    func testVersionBeyondInt32IsAccepted() throws {
        let json = """
        {"version":5000000000,"activeProfile":"Default",\
        "profiles":{"Default":{"mcps":{}}}}
        """
        try Data(json.utf8).write(to: url)
        let result = MasterStoreIO.load(from: url)
        XCTAssertNil(result.corruptFileURL)
        XCTAssertEqual(result.store.version, 5_000_000_000)
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
