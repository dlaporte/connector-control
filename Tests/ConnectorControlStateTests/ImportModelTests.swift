import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/ImportModelTests.cs. The Import sheet: what
/// the rows say about one document against the collection it would land in, what the count
/// follows, and what a document this app cannot read leaves on screen.
@MainActor
final class ImportModelTests: XCTestCase {
    private func write(_ doc: CollectionDocument, at url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try doc.serialized().write(to: url)
    }

    func testRowsShowCollisionsAndTheCountFollowsChoices() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("shared/data-team.json")
        try write(CollectionDocumentSamples.dataTeam, at: url)
        // One of the document's four connectors is already in the target, under the same name.
        XCTAssertNil(state.upsert(name: "github", entry: MCPEntry(enabled: true, config: AppStateHarness.remote("https://x/")),
                                  renamedFrom: nil))

        let model = ImportModel(state: state, path: url.path)
        XCTAssertNil(model.loadError)
        XCTAssertEqual(model.mode, .addToCollection)
        XCTAssertEqual(model.targetCollection, "Default", "the active collection is local, so it is the target")
        XCTAssertEqual(model.localCollections, ["Default"])
        XCTAssertEqual(model.sourceLine, ImportModel.sourceLine("Data team", "Acme Data Platform", 4))
        XCTAssertEqual(model.rows.map(\.name), ["dbt", "github", "ledger", "notion"])
        XCTAssertEqual(model.rows.map(\.present), [false, true, false, false])
        XCTAssertEqual(model.rows.map(\.include), [true, false, true, true], "what is already there is not imported by default")
        XCTAssertEqual(model.rows.map(\.choice), [.add, .replace, .add, .add])
        XCTAssertEqual(model.rows.first { $0.name == "ledger" }?.needs, ["server_path"])
        XCTAssertTrue(model.rows.allSatisfy { $0.excludedReason == nil }, "a Mac excludes nothing")
        XCTAssertEqual(model.importCount, 3)
        XCTAssertTrue(model.canImport)

        // Ticking the collision in imports it too; the count follows the ticks, not the document.
        model.rows[1].include = true
        XCTAssertEqual(model.importCount, 4)
        model.rows[0].include = false
        XCTAssertEqual(model.importCount, 3)

        model.rows[1].choice = .keepBoth
        XCTAssertNil(model.perform())
        let mcps = try XCTUnwrap(state.store.collections["Default"]).mcps
        XCTAssertNil(mcps["dbt"], "an unticked row is skipped")
        XCTAssertNotNil(mcps["github 2"], "Keep both lands beside the connector that was there")
        XCTAssertEqual(mcps["github"]?.config, AppStateHarness.remote("https://x/"), "the one that was there is untouched")
        XCTAssertEqual(state.collectionsFile.collections["Default"]?.provenance["ledger"]?.from, "Data team")
        XCTAssertEqual(state.kind(of: "Default"), .local)
    }

    func testSyncModeDefaultsTheNameAndSuffixesATakenOne() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("shared/data-team.json")
        try write(CollectionDocumentSamples.dataTeam, at: url)

        let first = ImportModel(state: state, path: url.path)
        XCTAssertEqual(first.syncName, "Data team", "the document's own name is the default")
        first.mode = .keepInSync
        XCTAssertEqual(first.importCount, 4, "sync mode takes every connector this platform can carry")
        XCTAssertTrue(first.canImport)
        XCTAssertNil(first.perform())
        XCTAssertEqual(state.kind(of: "Data team"), .synced)
        XCTAssertEqual(state.sourceBinding(of: "Data team")?.path, url.standardizedFileURL.path)

        let second = ImportModel(state: state, path: url.path)
        XCTAssertEqual(second.syncName, "Data team 2", "a name already taken is suffixed")
        second.mode = .keepInSync
        second.syncName = "  "
        XCTAssertFalse(second.canImport, "a collection has to be called something")
    }

    func testANewerDocumentIsALoadError() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        var newer = CollectionDocumentSamples.dataTeam.encode()
        newer = try XCTUnwrap(newer.replacing(at: JSONPointer(["connectorControlCollection"]), with: .int(2)))
        let url = h.dir.file("future.json")
        try newer.serialized().write(to: url)

        let model = ImportModel(state: state, path: url.path)
        XCTAssertEqual(model.loadError, AppState.newerDocumentError)
        XCTAssertTrue(model.rows.isEmpty)
        XCTAssertEqual(model.importCount, 0)
        XCTAssertFalse(model.canImport)
        XCTAssertEqual(model.perform(), AppState.newerDocumentError, "the sheet's button says what the sheet says")

        let half = h.dir.file("half.json")
        try TempDir.touch(half, "{half")
        let malformed = ImportModel(state: state, path: half.path)
        XCTAssertEqual(malformed.loadError?.hasPrefix("half.json couldn’t be read: "), true)
        XCTAssertFalse(malformed.canImport)
        XCTAssertEqual(state.collectionNames, ["Default"], "a document that cannot be read creates nothing")
    }
}
