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

    /// The platform-forced half of a mirrored pair: the Mac never writes the `cmd /c` launcher,
    /// so a header name cmd.exe would re-parse excludes nothing here, where the Windows mirror
    /// asserts the row is excluded, uncountable and skipped.
    func testAnExcludedRowShowsItsReasonStaysOutOfTheCountAndIsSkipped() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("shared/risky.json")
        try write(riskyHeaderDocument, at: url)

        let model = ImportModel(state: state, path: url.path)
        XCTAssertEqual(model.rows.map(\.name), ["bad", "good"])
        XCTAssertTrue(model.rows.allSatisfy { $0.excludedReason == nil })
        XCTAssertTrue(model.rows.allSatisfy(\.include))
        XCTAssertEqual(model.importCount, 2)
        model.mode = .keepInSync
        XCTAssertEqual(model.importCount, 2)

        model.mode = .addToCollection
        XCTAssertNil(model.perform())
        let mcps = try XCTUnwrap(state.store.collections["Default"]).mcps
        XCTAssertNotNil(mcps["good"])
        XCTAssertNotNil(mcps["bad"], "a header name is only unsafe where cmd.exe re-parses it")
    }

    /// Two remote connectors, one with a header name carrying the `&` the Windows `cmd /c`
    /// launcher cannot hand to cmd.exe.
    private var riskyHeaderDocument: CollectionDocument {
        CollectionDocument(
            name: "Risky", author: "Acme", origin: "o-risky", exported: "2026-09-21T14:02:11Z",
            connectors: [
                "bad": .init(launcher: .remote(.init(url: "https://h/mcp", auth: .header(name: "X&Y"),
                                                     package: "mcp-remote", extraArgs: []))),
                "good": .init(launcher: .remote(.init(url: "https://h/mcp", auth: .automatic,
                                                      package: "mcp-remote", extraArgs: []))),
            ])
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

    func testEveryChoiceHasATitleAndACollisionOffersThree() {
        XCTAssertEqual(ImportModel.choiceTitle(.add), "Add")
        XCTAssertEqual(ImportModel.choiceTitle(.replace), "Replace")
        XCTAssertEqual(ImportModel.choiceTitle(.keepBoth), "Keep both")
        XCTAssertEqual(ImportModel.choiceTitle(.skip), "Skip")
        // The picker is a collision's, so the case where nothing is in the way is not in it.
        XCTAssertEqual(ImportModel.collisionChoices, [.replace, .keepBoth, .skip])
        XCTAssertEqual(ImportModel.collisionChoices.map(ImportModel.choiceTitle),
                       ["Replace", "Keep both", "Skip"])
    }

    func testARowsCautionIsTheSentenceAConnectorAlreadyCarries() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("data-team.json")
        try write(CollectionDocumentSamples.dataTeam, at: url)
        let model = ImportModel(state: state, path: url.path)

        // The document's own needs, sorted, in the wording the window and the editor use.
        let dbt = try XCTUnwrap(model.rows.first { $0.name == "dbt" })
        XCTAssertEqual(dbt.needs, ["DBT_TOKEN"])
        XCTAssertEqual(dbt.needsCaution, AppState.needsValueCaution("DBT_TOKEN"))
        let notion = try XCTUnwrap(model.rows.first { $0.name == "notion" })
        XCTAssertEqual(notion.needs, ["token"])
        XCTAssertEqual(notion.needsCaution, AppState.needsValueCaution("token"))

        // No glyph for a connector the author left nothing to fill in.
        let github = try XCTUnwrap(model.rows.first { $0.name == "github" })
        XCTAssertEqual(github.needs, [])
        XCTAssertNil(github.needsCaution)

        // The same connector, once imported, says exactly the same thing in the window.
        XCTAssertNil(model.perform())
        XCTAssertEqual(state.connectorCaution("dbt", in: "Default"), dbt.needsCaution)
    }
    func testEachRowCarriesItsOwnBadge() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("data-team.json")
        try write(CollectionDocumentSamples.dataTeam, at: url)
        // One of the document's four connectors is already in the target, under the same name.
        XCTAssertNil(state.upsert(name: "github", entry: MCPEntry(config: AppStateHarness.remote("https://x/")),
                                  renamedFrom: nil))

        let model = ImportModel(state: state, path: url.path)
        XCTAssertEqual(model.rows.map(\.name), ["dbt", "github", "ledger", "notion"])
        XCTAssertEqual(model.rows.map(\.badge),
                       [ImportModel.newBadge, ImportModel.presentBadge, ImportModel.newBadge, ImportModel.newBadge])
        // The badge is what the row is, not what the user has since ticked.
        model.rows[1].include = true
        model.rows[1].choice = .replace
        XCTAssertEqual(model.rows[1].badge, ImportModel.presentBadge)
        // The third form is the excluded one, which only the other platform's launcher rules produce;
        // its mirror asserts it against a row this Mac cannot make.
    }
    /// Two needs in one connector, one in an argument and one in an environment value, so first
    /// appearance and alphabetical order disagree: object keys are walked sorted, and `args`
    /// comes before `env`, while the names sort the other way.
    private var twoNeedsDocument: CollectionDocument {
        CollectionDocument(
            name: "Two", author: "Acme", origin: "o-two", exported: "2026-09-21T14:02:11Z",
            connectors: [
                "pair": .init(launcher: .local(.init(command: "node", args: ["${CC_NEEDS:zulu}"], platform: .mac)),
                              env: ["ALPHA": .hint("the alpha hint")],
                              needs: ["zulu": "the zulu hint"]),
            ])
    }

    func testARowsNeedsFollowTheConfigRatherThanTheAlphabet() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("two.json")
        try write(twoNeedsDocument, at: url)
        let model = ImportModel(state: state, path: url.path)

        let pair = try XCTUnwrap(model.rows.first { $0.name == "pair" })
        XCTAssertEqual(pair.needs, ["zulu", "ALPHA"], "first appearance in the config, not sorted")
        XCTAssertEqual(pair.needsCaution, AppState.needsValueCaution("zulu, ALPHA"))

        // Once imported, the connector's own caution is the very same sentence.
        XCTAssertNil(model.perform())
        XCTAssertEqual(state.connectorCaution("pair", in: "Default"), pair.needsCaution)
    }
    func testATickedRowSetToSkipIsNotCounted() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("shared/data-team.json")
        try write(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.upsert(name: "github", entry: MCPEntry(config: AppStateHarness.remote("https://x/")),
                                  renamedFrom: nil))

        let model = ImportModel(state: state, path: url.path)
        XCTAssertEqual(model.importCount, 3, "the collision starts unticked")

        // Ticked and set to Skip: the button must not promise what `perform` will not land.
        model.rows[1].include = true
        model.rows[1].choice = .skip
        XCTAssertEqual(model.importCount, 3)
        model.rows[1].choice = .replace
        XCTAssertEqual(model.importCount, 4)

        // Every row skipped is nothing to import at all.
        for index in model.rows.indices { model.rows[index].choice = .skip }
        XCTAssertEqual(model.importCount, 0)
        XCTAssertFalse(model.canImport)
        // Sync mode takes the whole document, so a per-row choice says nothing about it.
        model.mode = .keepInSync
        XCTAssertEqual(model.importCount, 4)
    }

    func testTheAccessibilityLabelsNameTheirControls() {
        XCTAssertEqual(ImportModel.includeLabel("github"), "Include github")
        XCTAssertEqual(ImportModel.syncNameLabel, "Collection name")
        XCTAssertEqual(ImportModel.collisionPickerLabel("github"), "What to do with github")
    }
}
