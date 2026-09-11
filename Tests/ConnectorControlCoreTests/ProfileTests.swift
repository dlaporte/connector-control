import XCTest
@testable import ConnectorControlCore

final class ProfileTests: XCTestCase {
    private func entry(_ url: String) -> MCPEntry {
        MCPEntry(config: RemotePattern.make(url: url))
    }

    // MARK: - Decoding

    func testV2RoundTripPreservesTwoProfiles() throws {
        let store = MasterStore(
            activeProfile: "Work",
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
    }

    // MARK: - mcps accessor scoping

    func testMcpsAccessorReadsAndWritesOnlyActiveProfile() {
        var store = MasterStore(
            activeProfile: "Work",
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
        var store = MasterStore.single(["a": entry("https://a.example/mcp")])
        let error = store.addProfile(named: "Copy", copyingCurrent: true)
        XCTAssertNil(error)
        XCTAssertEqual(store.activeProfile, "Copy")
        XCTAssertEqual(store.profiles["Copy"]?.mcps.keys.sorted(), ["a"])
        XCTAssertEqual(store.profiles["Default"]?.mcps.keys.sorted(), ["a"], "original untouched")
    }

    func testAddProfileEmptyStartsBlank() {
        var store = MasterStore.single(["a": entry("https://a.example/mcp")])
        let error = store.addProfile(named: "Fresh", copyingCurrent: false)
        XCTAssertNil(error)
        XCTAssertEqual(store.profiles["Fresh"]?.mcps, [:])
    }

    func testAddProfileRejectsEmptyName() {
        var store = MasterStore.empty
        XCTAssertEqual(store.addProfile(named: "   ", copyingCurrent: false), "Name must not be empty.")
    }

    func testAddProfileRejectsDuplicateName() {
        var store = MasterStore.empty
        XCTAssertEqual(store.addProfile(named: "Default", copyingCurrent: false),
                       "A profile named “Default” already exists.")
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
            activeProfile: "Work",
            profiles: ["Work": Profile(), "Personal": Profile()])
        XCTAssertEqual(store.renameActiveProfile(to: "Personal"), "A profile named “Personal” already exists.")
        XCTAssertEqual(store.activeProfile, "Work", "unchanged on error")
    }

    func testRenameActiveProfileRejectsEmptyName() {
        var store = MasterStore.empty
        XCTAssertNotNil(store.renameActiveProfile(to: "  "))
    }

    func testDeleteActiveProfileSwitchesToFirstRemaining() {
        var store = MasterStore(
            activeProfile: "Work",
            profiles: ["Work": Profile(), "Alpha": Profile(), "Zeta": Profile()])
        let error = store.deleteActiveProfile()
        XCTAssertNil(error)
        XCTAssertEqual(store.activeProfile, "Alpha")
        XCTAssertNil(store.profiles["Work"])
    }

    func testDeleteActiveProfileRejectsLastProfile() {
        var store = MasterStore.empty
        XCTAssertEqual(store.deleteActiveProfile(), "Can’t delete the last profile.")
        XCTAssertEqual(store.profiles.count, 1)
    }

    func testSwitchProfile() {
        var store = MasterStore(
            activeProfile: "Work",
            profiles: ["Work": Profile(), "Personal": Profile()])
        XCTAssertNil(store.switchProfile(to: "Personal"))
        XCTAssertEqual(store.activeProfile, "Personal")
    }

    func testSwitchProfileRejectsUnknownName() {
        var store = MasterStore.empty
        XCTAssertEqual(store.switchProfile(to: "Nope"), "No profile named “Nope”.")
        XCTAssertEqual(store.activeProfile, "Default")
    }

    /// windows/tests/ConnectorControl.Core.Tests/ProfileTests.cs
    /// ErrorMessagesUseTypographicPunctuationLikeTheMacApp.
    func testErrorMessagesUseTypographicPunctuationLikeTheMacApp() {
        var store = MasterStore.empty
        let duplicate = store.addProfile(named: "Default", copyingCurrent: false)!
        XCTAssertEqual(duplicate[duplicate.index(before: duplicate.range(of: "Default")!.lowerBound)], "“")
        XCTAssertEqual(duplicate[duplicate.range(of: "Default")!.upperBound], "”")
        XCTAssertTrue(store.deleteActiveProfile()!.contains("’"))
    }
}
