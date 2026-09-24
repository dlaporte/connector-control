import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/AppStateUpgradeTests.cs
///
/// An install upgraded from 1.3, which knew collections as profiles: `Tests/Fixtures/v1.3/` holds
/// the master list 1.3 wrote (schema 2, two profiles, "Work" active, some connectors off) and the
/// Claude config it left applied. There is no collections.json and no collections-local.json:
/// 1.3 never wrote either.
@MainActor
final class AppStateUpgradeTests: XCTestCase {
    /// Each profile's connectors and whether they are on, as 1.3 wrote them.
    private let flags13: [String: [String: Bool]] = [
        "Default": ["aws-mcp": true, "scoutbook": false, "service-now": true],
        "Work": ["jira": true, "ledger": false, "scoutbook": true],
    ]

    /// Places the 1.3 install's two files where this app looks for them.
    private func installVersion13(_ h: AppStateHarness) throws -> (store: Data, claude: Data) {
        let store = try Data(contentsOf: Fixtures.url("v1.3/mcps.json"))
        let claude = try Data(contentsOf: Fixtures.url("v1.3/claude_desktop_config.json"))
        try FileManager.default.createDirectory(at: h.storeDir, withIntermediateDirectories: true)
        try store.write(to: h.masterStoreURL)
        try claude.write(to: h.claudeConfigURL)
        return (store, claude)
    }

    /// The keys 1.3's decoder requires at every level above a connector's own config: the file's
    /// root, each profile, and each connector entry. 1.3 fails to decode a file that lacks one.
    private func shape(of data: Data) throws -> [String] {
        func keys(_ value: JSONValue?, _ path: String) throws -> (String, [String: JSONValue]) {
            guard case .object(let object)? = value else { throw AppStateHarness.HarnessError() }
            return ("\(path): \(object.keys.sorted().joined(separator: ","))", object)
        }
        let (root, rootObject) = try keys(try JSONValue.parse(data), "")
        var shape = [root]
        guard case .object(let profiles)? = rootObject["profiles"] else { throw AppStateHarness.HarnessError() }
        for name in profiles.keys.sorted() {
            let (line, profile) = try keys(profiles[name], "/profiles/\(name)")
            shape.append(line)
            guard case .object(let mcps)? = profile["mcps"] else { throw AppStateHarness.HarnessError() }
            for connector in mcps.keys.sorted() {
                shape.append(try keys(mcps[connector], "/profiles/\(name)/mcps/\(connector)").0)
            }
        }
        return shape
    }

    func testAVersion13InstallOpensItsProfilesAsLocalCollectionsAndStaysReadableBy13() throws {
        let h = AppStateHarness(seedClaudeConfig: false)
        defer { h.dispose() }
        let written = try installVersion13(h)
        let fm = FileManager.default
        XCTAssertFalse(fm.fileExists(atPath: h.storeDir.appendingPathComponent(CollectionsFile.fileName).path))
        XCTAssertFalse(fm.fileExists(atPath: h.storeDir.appendingPathComponent(CollectionsLocalCache.fileName).path))

        let state = h.create()

        // Every profile is a local collection under its own name, and the active one stays active.
        XCTAssertEqual(state.collectionNames, ["Default", "Work"])
        for name in state.collectionNames {
            XCTAssertEqual(state.kind(of: name), .local, name)
            XCTAssertFalse(state.isPublished(name), name)
        }
        XCTAssertEqual(state.activeCollection, "Work")
        XCTAssertNil(state.collectionBanner)
        XCTAssertNil(state.lastError)

        // Connectors, their configs and their on/off flags are exactly what 1.3 wrote.
        XCTAssertEqual(state.store, MasterStoreIO.read(from: Fixtures.url("v1.3/mcps.json")))
        XCTAssertEqual(state.store.collections.mapValues { $0.mcps.mapValues(\.enabled) }, flags13)

        // The upgrade alone gives Claude nothing new to run, so its config is left as it was.
        XCTAssertEqual(try Data(contentsOf: h.claudeConfigURL), written.claude)
        XCTAssertNil(h.settings.lastApplyDate, "nothing was applied")
        XCTAssertFalse(state.needsClaudeRestart)

        // The master list keeps 1.3's keys, whether or not the launch rewrote it...
        let shape13 = try shape(of: written.store)
        XCTAssertEqual(try shape(of: Data(contentsOf: h.masterStoreURL)), shape13)
        XCTAssertEqual(try h.storeOnDisk(), state.store)

        // ...and after a save from this version, which a 1.3 install on another machine or after
        // a downgrade still has to read: the same keys, schema version 2, the same content.
        state.setEnabled("ledger", true, in: "Work")
        state.setEnabled("ledger", false, in: "Work")
        let saved = try Data(contentsOf: h.masterStoreURL)
        XCTAssertEqual(try shape(of: saved), shape13)
        XCTAssertEqual(try JSONValue.parse(saved).value(at: JSONPointer(["version"])), .int(2))
        XCTAssertEqual(try JSONValue.parse(saved), try JSONValue.parse(written.store))
    }
}
