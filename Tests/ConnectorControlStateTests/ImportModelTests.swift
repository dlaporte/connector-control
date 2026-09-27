import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ImportModelTests.cs.
/// The Import sheet: what the rows say about one document against the collection it would land in,
/// what the count follows, and what a document this app cannot read leaves on screen.
@MainActor
final class ImportModelTests: XCTestCase {
    func testRowsShowCollisionsAndTheCountFollowsChoices() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.writeDocument(CollectionDocumentSamples.dataTeam, named: "shared/data-team.json")
        // One of the document's four connectors is already in the target, under the same name.
        XCTAssertNil(state.upsert(name: "github", entry: MCPEntry(enabled: true, config: AppStateHarness.remote("https://x/")),
                                  renamedFrom: nil))

        let model = ImportModel(state: state, path: url.path)
        XCTAssertNil(model.loadError)
        XCTAssertEqual(model.mode, .addToCollection)
        XCTAssertEqual(model.target, .collection("Default"), "the active collection is local, so it is the target")
        XCTAssertEqual(model.targets, [.collection("Default"), .newCollection])
        XCTAssertEqual(model.sourceLine, ImportModel.sourceLine("Data team", "Acme Data Platform", 4))
        XCTAssertEqual(model.rows.map(\.name), ["dbt", "github", "ledger", "notion"])
        XCTAssertEqual(model.rows.map(\.present), [false, true, false, false])
        XCTAssertEqual(model.rows.map(\.include), [true, false, true, true], "what is already there is not imported by default")
        XCTAssertEqual(model.rows.map(\.choice), [.add, .replace, .add, .add])
        XCTAssertEqual(model.rows.map(\.showsPicker), [false, false, false, false], "an unticked collision shows its badge")
        model.rows[1].include = true
        XCTAssertTrue(model.rows[1].showsPicker, "a collision coming across asks what to do")
        model.rows[1].include = false
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

    /// The target is the collection the window has selected when that one is local; otherwise the
    /// active collection, as it is for an Import started with nothing selected.
    func testTheTargetIsTheSelectedLocalCollectionElseTheActiveOne() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.writeDocument(CollectionDocumentSamples.dataTeam, named: "shared/data-team.json")
        try h.subscribe(state, to: CollectionDocumentSamples.dataTeam, at: "other.json", as: "Team")
        XCTAssertNil(state.addEmptyCollection(named: "Work"))
        XCTAssertEqual(ImportModel(state: state, path: url.path, selected: "Work").target, .collection("Work"))
        XCTAssertEqual(ImportModel(state: state, path: url.path, selected: "Team").target, .collection("Default"),
                       "a subscribed collection takes no copies")
        XCTAssertEqual(ImportModel(state: state, path: url.path).target, .collection("Default"))
        XCTAssertEqual(ImportModel(state: state, path: url.path, selected: "Gone").target, .collection("Default"))
    }

    /// New Collection, the last of the targets, asks for a name when it is chosen and lands the
    /// copies in a new, empty local collection that does not become active; nothing is made until
    /// Import is pressed, and a cancelled prompt leaves the target where it was.
    func testNewCollectionAsksForANameAndImportsIntoIt() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.writeDocument(CollectionDocumentSamples.dataTeam, named: "shared/data-team.json")
        XCTAssertNil(state.upsert(name: "github", entry: MCPEntry(enabled: true, config: AppStateHarness.remote("https://x/")),
                                  renamedFrom: nil))
        let model = ImportModel(state: state, path: url.path)
        XCTAssertEqual(model.targets, [.collection("Default"), .newCollection])
        XCTAssertEqual(model.targets.map(ImportModel.targetTitle), ["Default", CollectionsModel.newButton])
        XCTAssertEqual(model.target, .collection("Default"))

        h.dialogs.nextPromptAnswer = nil
        model.target = .newCollection
        XCTAssertEqual(model.target, .collection("Default"), "a cancelled prompt leaves the target where it was")
        XCTAssertEqual(h.dialogs.prompts.last, FakeDialogs.PromptCall(title: AppState.newCollectionTitle, initial: ""))

        h.dialogs.nextPromptAnswer = "  Fresh  "
        model.target = .newCollection
        XCTAssertEqual(model.target, .newCollection)
        XCTAssertEqual(model.targetName, "Fresh")
        XCTAssertEqual(ImportModel.addModeTitle(model.targetName), "Add to a collection: Fresh")
        XCTAssertEqual(model.rows.map(\.present), [false, false, false, false], "nothing is in a new collection's way")
        XCTAssertEqual(model.importCount, 4)
        XCTAssertNil(state.store.collections["Fresh"], "nothing is made before Import")

        let before = try h.claudeServers()
        XCTAssertNil(model.perform())
        let fresh = try XCTUnwrap(state.store.collections["Fresh"])
        XCTAssertEqual(fresh.mcps.keys.sorted(), ["dbt", "github", "ledger", "notion"])
        XCTAssertTrue(fresh.mcps.values.allSatisfy { !$0.enabled }, "copies arrive off")
        XCTAssertEqual(state.kind(of: "Fresh"), .local)
        XCTAssertEqual(state.activeCollection, "Default", "the new collection is not made active")
        XCTAssertEqual(try h.claudeServers(), before)

        // A name already taken is the store's refusal, and nothing is imported.
        let again = ImportModel(state: state, path: url.path)
        h.dialogs.nextPromptAnswer = "Fresh"
        again.target = .newCollection
        XCTAssertEqual(again.perform(), "A collection named \u{201C}Fresh\u{201D} already exists.")
        XCTAssertEqual(state.store.collections["Fresh"], fresh)

        // Choosing an existing collection again gives up the new one.
        again.target = .collection("Default")
        XCTAssertEqual(again.targetName, "Default")
        XCTAssertEqual(again.rows.map(\.present), [false, true, false, false])
    }

    /// A document that cannot be read by the time Import is pressed makes nothing: the new
    /// collection is not left behind empty, and a second Import says why again rather than that
    /// the collection exists.
    func testANewCollectionIsNotMadeWhenTheDocumentCannotBeReadAtImport() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.writeDocument(CollectionDocumentSamples.dataTeam, named: "shared/data-team.json")
        let model = ImportModel(state: state, path: url.path)
        h.dialogs.nextPromptAnswer = "Fresh"
        model.target = .newCollection
        XCTAssertEqual(model.target, .newCollection)

        try FileManager.default.removeItem(at: url)
        let unreadable = try XCTUnwrap(AppState.readDocument(at: url.standardizedFileURL).failure)
        XCTAssertEqual(model.perform(), unreadable)
        XCTAssertEqual(model.failure, unreadable)
        XCTAssertNil(state.store.collections["Fresh"], "nothing is made for copies that cannot land")
        XCTAssertEqual(model.perform(), unreadable, "a retry says why again, not that Fresh exists")
        XCTAssertNil(state.store.collections["Fresh"])
    }

    /// A New Collection name that trims to nothing is refused as Copy to ▸ New Collection refuses
    /// it: the sheet says so, the picker goes back to the target it had, and nothing is made.
    func testAnEmptyNewCollectionNameIsRefused() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.writeDocument(CollectionDocumentSamples.dataTeam, named: "shared/data-team.json")
        let model = ImportModel(state: state, path: url.path)
        let collections = state.collectionNames

        h.dialogs.nextPromptAnswer = "   "
        model.target = .newCollection
        XCTAssertEqual(h.dialogs.prompts.last, FakeDialogs.PromptCall(title: AppState.newCollectionTitle, initial: ""))
        XCTAssertEqual(model.failure, AppState.nameEmptyError)
        XCTAssertEqual(model.target, .collection("Default"), "the picker goes back to the target it had")
        XCTAssertEqual(model.targetName, "Default")
        XCTAssertEqual(state.collectionNames, collections)

        // A name the prompt accepts answers the refusal.
        h.dialogs.nextPromptAnswer = "Fresh"
        model.target = .newCollection
        XCTAssertNil(model.failure)
        XCTAssertEqual(model.targetName, "Fresh")
    }

    /// The failure line is the model's: an Import that did not land leaves its reason there, and
    /// only a change of mode takes it away, since the other mode is another question.
    func testAChangeOfModeClearsTheLastFailure() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.writeDocument(CollectionDocumentSamples.dataTeam, named: "shared/data-team.json")
        let model = ImportModel(state: state, path: url.path)
        model.mode = .keepInSync
        model.syncName = state.activeCollection
        let refusal = try XCTUnwrap(model.perform())
        XCTAssertEqual(model.failure, refusal)
        model.mode = .keepInSync
        XCTAssertEqual(model.failure, refusal, "choosing the mode already chosen leaves the line alone")
        model.mode = .addToCollection
        XCTAssertNil(model.failure)
    }

    /// The platform-forced half of a mirrored pair: the Mac never writes the `cmd /c` launcher,
    /// so a header name cmd.exe would re-parse excludes nothing here, where the Windows mirror
    /// asserts the row is excluded, uncountable and skipped.
    func testAnExcludedRowShowsItsReasonStaysOutOfTheCountAndIsSkipped() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try h.writeDocument(riskyHeaderDocument, named: "shared/risky.json")

        let model = ImportModel(state: state, path: url.path)
        XCTAssertEqual(model.rows.map(\.name), ["bad", "good"])
        XCTAssertTrue(model.rows.allSatisfy { $0.excludedReason == nil })
        XCTAssertTrue(model.rows.allSatisfy(\.canInclude), "nothing here is out of reach of its tick")
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
        let url = try h.writeDocument(CollectionDocumentSamples.dataTeam, named: "shared/data-team.json")

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
        let url = try h.writeDocument(CollectionDocumentSamples.dataTeam, named: "data-team.json")
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
        let url = try h.writeDocument(CollectionDocumentSamples.dataTeam, named: "data-team.json")
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
        let url = try h.writeDocument(twoNeedsDocument, named: "two.json")
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
        let url = try h.writeDocument(CollectionDocumentSamples.dataTeam, named: "shared/data-team.json")
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
