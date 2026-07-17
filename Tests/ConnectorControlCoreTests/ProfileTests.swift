import XCTest
@testable import ConnectorControlCore

final class ProfileTests: XCTestCase {
    var dir: URL!
    var url: URL { dir.appendingPathComponent("mcps.json") }

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("profile-store-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private func entry(_ url: String) -> MCPEntry {
        MCPEntry(config: .object(["command": .string("npx"),
                                   "args": .array([.string("-y"), .string("mcp-remote"),
                                                    .string(url)])]))
    }

    // MARK: - Decoding

    func testV2RoundTripPreservesTwoProfiles() throws {
        var store = MasterStore(
            version: 2, activeProfile: "Work",
            profiles: [
                "Work": Profile(mcps: ["a": entry("https://a.example/mcp")]),
                "Personal": Profile(mcps: ["b": entry("https://b.example/mcp")]),
            ])
        let data = try JSONEncoder().encode(store)
        let decoded = try JSONDecoder().decode(MasterStore.self, from: data)
        XCTAssertEqual(decoded, store)
        XCTAssertEqual(decoded.activeProfile, "Work")
        XCTAssertEqual(decoded.profiles["Work"]?.mcps.keys.sorted(), ["a"])
        XCTAssertEqual(decoded.profiles["Personal"]?.mcps.keys.sorted(), ["b"])
        _ = store // silence unused-mutation warning if any
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

    // MARK: - mcps accessor scoping

    func testMcpsAccessorReadsAndWritesOnlyActiveProfile() {
        var store = MasterStore(
            version: 2, activeProfile: "Work",
            profiles: [
                "Work": Profile(mcps: ["a": entry("https://a.example/mcp")]),
                "Personal": Profile(mcps: ["b": entry("https://b.example/mcp")]),
            ])
        store.mcps["c"] = entry("https://c.example/mcp")
        XCTAssertEqual(store.profiles["Work"]?.mcps.keys.sorted(), ["a", "c"])
        XCTAssertEqual(store.profiles["Personal"]?.mcps.keys.sorted(), ["b"], "untouched")
    }

    // MARK: - Profile management

    func testAddProfileCopyingCurrent() {
        var store = MasterStore(version: 1, mcps: ["a": entry("https://a.example/mcp")])
        let error = store.addProfile(named: "Copy", copyingCurrent: true)
        XCTAssertNil(error)
        XCTAssertEqual(store.activeProfile, "Copy")
        XCTAssertEqual(store.profiles["Copy"]?.mcps.keys.sorted(), ["a"])
        XCTAssertEqual(store.profiles["Default"]?.mcps.keys.sorted(), ["a"], "original untouched")
    }

    func testAddProfileEmptyStartsBlank() {
        var store = MasterStore(version: 1, mcps: ["a": entry("https://a.example/mcp")])
        let error = store.addProfile(named: "Fresh", copyingCurrent: false)
        XCTAssertNil(error)
        XCTAssertEqual(store.profiles["Fresh"]?.mcps, [:])
    }

    func testAddProfileRejectsEmptyName() {
        var store = MasterStore.empty
        XCTAssertNotNil(store.addProfile(named: "   ", copyingCurrent: false))
    }

    func testAddProfileRejectsDuplicateName() {
        var store = MasterStore.empty
        XCTAssertNotNil(store.addProfile(named: "Default", copyingCurrent: false))
    }

    func testRenameActiveProfile() {
        var store = MasterStore.empty
        let error = store.renameActiveProfile(to: "Main")
        XCTAssertNil(error)
        XCTAssertEqual(store.activeProfile, "Main")
        XCTAssertEqual(Array(store.profiles.keys), ["Main"])
    }

    func testRenameActiveProfileRejectsCollision() {
        var store = MasterStore(
            version: 2, activeProfile: "Work",
            profiles: ["Work": Profile(), "Personal": Profile()])
        XCTAssertNotNil(store.renameActiveProfile(to: "Personal"))
        XCTAssertEqual(store.activeProfile, "Work", "unchanged on error")
    }

    func testRenameActiveProfileRejectsEmptyName() {
        var store = MasterStore.empty
        XCTAssertNotNil(store.renameActiveProfile(to: "  "))
    }

    func testDeleteActiveProfileSwitchesToFirstRemaining() {
        var store = MasterStore(
            version: 2, activeProfile: "Work",
            profiles: ["Work": Profile(), "Alpha": Profile(), "Zeta": Profile()])
        let error = store.deleteActiveProfile()
        XCTAssertNil(error)
        XCTAssertEqual(store.activeProfile, "Alpha")
        XCTAssertNil(store.profiles["Work"])
    }

    func testDeleteActiveProfileRejectsLastProfile() {
        var store = MasterStore.empty
        XCTAssertNotNil(store.deleteActiveProfile())
        XCTAssertEqual(store.profiles.count, 1)
    }

    func testSwitchProfile() {
        var store = MasterStore(
            version: 2, activeProfile: "Work",
            profiles: ["Work": Profile(), "Personal": Profile()])
        XCTAssertNil(store.switchProfile(to: "Personal"))
        XCTAssertEqual(store.activeProfile, "Personal")
    }

    func testSwitchProfileRejectsUnknownName() {
        var store = MasterStore.empty
        XCTAssertNotNil(store.switchProfile(to: "Nope"))
        XCTAssertEqual(store.activeProfile, "Default")
    }
}
