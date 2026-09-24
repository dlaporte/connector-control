import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/PublishModelTests.cs. The Publish/Export
/// sheet: which rows the collection produces, what ticking them says in the intent, and what the
/// preview and the warnings show for it.
@MainActor
final class PublishModelTests: XCTestCase {
    /// One local connector with a stripped and a shared environment value and one absolute path
    /// argument — a row of each kind the sheet offers.
    private let connector = JSONValue.object([
        "command": .string("node"),
        "args": .array([.string("/Users/d/x.js"), .string("--quiet")]),
        "env": .object(["A": .string("sk-live-secret"), "B": .string("us")]),
    ])

    private func started() throws -> (AppStateHarness, AppState) {
        let (h, state) = AppStateHarness.started()
        XCTAssertNil(state.upsert(name: "c", entry: MCPEntry(config: connector), renamedFrom: nil))
        return (h, state)
    }

    private func publishFolder(_ h: AppStateHarness) throws -> URL {
        let url = h.dir.file("pub")
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    func testRowsComeFromTheCollectionAndBuildTheIntent() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let model = PublishModel(state: state, collection: state.activeCollection)

        XCTAssertEqual(model.envRows.map(\.name), ["A", "B"])
        XCTAssertEqual(model.envRows.map(\.connector), ["c", "c"])
        XCTAssertTrue(model.envRows.allSatisfy { !$0.share }, "stripped is the default")
        XCTAssertEqual(model.pathRows.count, 1, "only the argument that looks like a path is offered")
        let path = try XCTUnwrap(model.pathRows.first)
        XCTAssertEqual(path.pointer, JSONPointer(["args", "0"]))
        XCTAssertEqual(path.value, "/Users/d/x.js")
        XCTAssertFalse(path.marked)
        XCTAssertEqual(path.name, "path")
        XCTAssertEqual(model.fileName, Slug.make(state.activeCollection) + ".json")
        XCTAssertFalse(model.canPublish, "nothing is published until a folder is chosen")
        XCTAssertEqual(model.intent, .none)

        model.envRows[1].share = true
        model.pathRows[0].marked = true
        model.pathRows[0].name = "srv"
        model.pathRows[0].hint = "your ledger clone, then dist/index.js"
        XCTAssertEqual(model.intent.shareValues, ["c": ["B"]])
        XCTAssertEqual(model.intent.pathMarks["c"]?[JSONPointer(["args", "0"])],
                       PublishIntent.PathMark(name: "srv", hint: "your ledger clone, then dist/index.js", value: "/Users/d/x.js"),
                       "the mark records the path it was made on, so it can find it again once arguments move")
        XCTAssertTrue(model.preview.contains("${CC_NEEDS:srv}"), "a marked path leaves as its placeholder")
    }

    func testEachConnectorNumbersItsOwnPathsAndOnlyLocalOnesOfferAny() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "two", entry: MCPEntry(config: .object([
            "command": .string("node"),
            "args": .array([.string("~/one.js"), .string("./two.js"), .string("https://example.com/x")]),
        ])), renamedFrom: nil))
        let model = PublishModel(state: state, collection: state.activeCollection)
        XCTAssertEqual(model.pathRows.filter { $0.connector == "two" }.map(\.name), ["path", "path_2"])
        XCTAssertEqual(model.pathRows.filter { $0.connector == "c" }.map(\.name), ["path"],
                       "the numbering starts again in every connector")
        // The three connectors the harness seeds are remote: their arguments are the launcher's,
        // not the author's, and marking them would say nothing.
        XCTAssertEqual(Set(model.pathRows.map(\.connector)), ["c", "two"])
    }

    func testPreviewHidesUnsharedValuesAndListsWarnings() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let model = PublishModel(state: state, collection: state.activeCollection)
        XCTAssertFalse(jsonText(model.preview, contains: "sk-live-secret"), "the default is stripped")
        XCTAssertTrue(model.warnings.isEmpty, "a value that never leaves this machine is nothing to warn about")

        model.envRows[0].hint = "the ledger dashboard ▸ API tokens"
        XCTAssertTrue(model.preview.contains("the ledger dashboard ▸ API tokens"))

        model.envRows[0].share = true
        XCTAssertTrue(model.preview.contains("sk-live-secret"), "the preview shows every byte that leaves")
        XCTAssertEqual(model.warnings, [PublishModel.warningLine("c", "env.A looks like a credential")])
    }

    func testPublishingThroughTheSheetRecordsWhatWasTickedAndReopensWithIt() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        let model = PublishModel(state: state, collection: state.activeCollection)
        model.folder = folder.path
        XCTAssertTrue(model.canPublish)
        model.envRows[1].share = true
        model.pathRows[0].marked = true
        model.pathRows[0].name = "srv"
        XCTAssertNil(model.publish())

        let written = try Data(contentsOf: folder.appendingPathComponent(model.fileName))
        let document = try CollectionDocument.decode(written)
        XCTAssertEqual(document.connectors["c"]?.env["B"], .value("us"))
        XCTAssertEqual(document.connectors["c"]?.needs["srv"], .some(nil))

        let reopened = PublishModel(state: state, collection: state.activeCollection)
        XCTAssertEqual(reopened.folder, folder.path)
        XCTAssertEqual(reopened.envRows.first { $0.name == "B" }?.share, true)
        XCTAssertEqual(reopened.pathRows.first?.marked, true)
        XCTAssertEqual(reopened.pathRows.first?.name, "srv")

        // The file name is fixed when publishing starts, so a rename never orphans the document
        // the team already subscribed to.
        XCTAssertNil(state.renameCollection(state.activeCollection, to: "Team"))
        XCTAssertEqual(PublishModel(state: state, collection: "Team").fileName, "default.json")
    }

    func testAReopenedSheetTicksAPathWhereItNowStands() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let model = PublishModel(state: state, collection: state.activeCollection)
        model.folder = try publishFolder(h).path
        model.pathRows[0].marked = true
        model.pathRows[0].name = "srv"
        XCTAssertNil(model.publish())

        // Moved outside the editor, so the record still says where it was.
        XCTAssertNil(state.upsert(name: "c", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string("--quiet"), .string("/Users/d/x.js")]),
        ])), renamedFrom: "c"))
        let reopened = PublishModel(state: state, collection: state.activeCollection)
        let row = try XCTUnwrap(reopened.pathRows.first { $0.connector == "c" })
        XCTAssertEqual(row.pointer, JSONPointer(["args", "1"]))
        XCTAssertTrue(row.marked, "the tick sits where the exporter places the mark")
        XCTAssertEqual(row.name, "srv")

        // A marked argument that does not look like a path keeps its row, so publishing from the
        // sheet cannot quietly unmark it. Recorded as the author's reviewed answer, which is what
        // lets the path it replaces leave this machine's list of marked paths.
        let flag = PublishIntent(shareValues: [:], pathMarks: ["c": [
            JSONPointer(["args", "0"]): .init(name: "flag", hint: nil, value: "--quiet"),
        ]], hints: [:])
        XCTAssertNil(state.updatePublishIntent(state.activeCollection, intent: flag, reviewedValues: ["--quiet"]))
        let marked = PublishModel(state: state, collection: state.activeCollection)
        XCTAssertEqual(marked.pathRows.filter { $0.connector == "c" && $0.marked }.map(\.value), ["--quiet"])
        XCTAssertEqual(marked.intent, flag)
    }

    // MARK: - A mark that lost its argument

    private func node(_ args: [String]) -> JSONValue {
        .object(["command": .string("node"), "args": .array(args.map(JSONValue.string))])
    }

    private func documentArgs(_ connector: String, in data: Data) throws -> [String] {
        let document = try CollectionDocument.decode(data)
        guard case .local(let local)? = document.connectors[connector]?.launcher else {
            throw AppStateHarness.HarnessError()
        }
        return local.args
    }

    /// "c" published through the sheet with its path marked "srv", then that path edited outside
    /// the editor, so the record's mark has lost its argument. Returns the document's path.
    private func publishThenLoseTheMark(_ h: AppStateHarness, _ state: AppState) throws -> URL {
        let first = PublishModel(state: state, collection: state.activeCollection)
        first.folder = try publishFolder(h).path
        first.pathRows[0].marked = true
        first.pathRows[0].name = "srv"
        XCTAssertNil(first.publish())
        XCTAssertNil(state.upsert(name: "c", entry: MCPEntry(config: node(["/Users/d/y.js", "--quiet"])), renamedFrom: "c"))
        XCTAssertEqual(state.publishError?.message, AppState.pathMarkMovedError("c"))
        return try publishFolder(h).appendingPathComponent(first.fileName)
    }

    func testAReopenedSheetKeepsALostMarkAndWritesNothingUntilItIsAnswered() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let file = try publishThenLoseTheMark(h, state)
        let before = try Data(contentsOf: file)

        let sheet = PublishModel(state: state, collection: state.activeCollection)
        XCTAssertEqual(sheet.unresolvedMarks.map(\.connector), ["c"])
        XCTAssertEqual(sheet.pathRows.first { $0.connector == "c" }?.marked, false, "the path it lost is not where it was")
        XCTAssertNotNil(sheet.folder)
        XCTAssertFalse(sheet.canPublish, "a folder is on record, and still nothing can be published")
        XCTAssertFalse(sheet.canExport)

        let out = h.dir.file("away/copy.json")
        XCTAssertEqual(sheet.export(to: out.path), PublishModel.unresolvedMarkNote("c", "srv"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: out.path))
        XCTAssertEqual(sheet.publish(), PublishModel.unresolvedMarkNote("c", "srv"))
        XCTAssertEqual(try Data(contentsOf: file), before)
        XCTAssertEqual(state.collectionsFile.collections[state.activeCollection]?.publish?.intent.pathMarks["c"]?.values.first?.value,
                       "/Users/d/x.js", "the record is left as it was")
    }

    func testTickingThePathWhereItNowSitsAnswersTheLostMark() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let file = try publishThenLoseTheMark(h, state)
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        let row = try XCTUnwrap(sheet.pathRows.firstIndex { $0.connector == "c" && $0.value == "/Users/d/y.js" })

        sheet.pathRows[row].marked = true
        XCTAssertEqual(sheet.unresolvedMarks, [])
        XCTAssertTrue(sheet.canPublish)
        XCTAssertTrue(sheet.canExport)
        sheet.pathRows[row].name = "  "
        XCTAssertEqual(sheet.unresolvedMarks.map(\.connector), ["c"], "a tick with no name to carry would publish the path, so it answers nothing")
        sheet.pathRows[row].marked = false
        sheet.pathRows[row].name = "srv"
        XCTAssertEqual(sheet.unresolvedMarks.map(\.connector), ["c"], "unticking puts it back")

        sheet.pathRows[row].marked = true
        XCTAssertNil(sheet.publish())
        XCTAssertNil(state.publishError)
        XCTAssertEqual(try documentArgs("c", in: Data(contentsOf: file)), ["${CC_NEEDS:srv}", "--quiet"])
        XCTAssertEqual(state.collectionsFile.collections[state.activeCollection]?.publish?.intent.pathMarks["c"],
                       [JSONPointer(["args", "0"]): .init(name: "srv", hint: nil, value: "/Users/d/y.js")],
                       "the tick replaced the lost mark, pointer and value both")
    }

    func testARowTickedOnOpenAnswersNoOtherLostMark() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "two", entry: MCPEntry(config: node(["/Users/d/one.js", "/Users/d/two.js"])),
                                  renamedFrom: nil))
        let first = PublishModel(state: state, collection: state.activeCollection)
        first.folder = try publishFolder(h).path
        for index in first.pathRows.indices where first.pathRows[index].connector == "two" { first.pathRows[index].marked = true }
        XCTAssertNil(first.publish())
        XCTAssertNil(state.upsert(name: "two", entry: MCPEntry(config: node(["/Users/d/one.js", "/Users/d/2.js"])),
                                  renamedFrom: "two"))

        let sheet = PublishModel(state: state, collection: state.activeCollection)
        XCTAssertEqual(sheet.pathRows.filter { $0.connector == "two" && $0.marked }.map(\.value), ["/Users/d/one.js"])
        XCTAssertEqual(sheet.unresolvedMarks.map(\.connector), ["two"], "the tick on one.js was already on record, so it stands in for nothing")
        let moved = try XCTUnwrap(sheet.pathRows.firstIndex { $0.value == "/Users/d/2.js" })
        sheet.pathRows[moved].marked = true
        XCTAssertEqual(sheet.unresolvedMarks, [])
    }

    func testForgettingALostMarkSendsThePathAsThePreviewShows() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let file = try publishThenLoseTheMark(h, state)
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        XCTAssertEqual(try documentArgs("c", in: Data(sheet.preview.utf8)), ["/Users/d/y.js", "--quiet"],
                       "the preview shows what forgetting the mark would send")

        sheet.forgetUnresolvedMark(try XCTUnwrap(sheet.unresolvedMarks.first { $0.connector == "c" }).id)
        XCTAssertEqual(sheet.unresolvedMarks, [])
        XCTAssertTrue(sheet.canPublish)
        XCTAssertTrue(sheet.canExport)
        XCTAssertNil(sheet.publish())
        XCTAssertNil(state.publishError)
        XCTAssertEqual(try documentArgs("c", in: Data(contentsOf: file)), ["/Users/d/y.js", "--quiet"],
                       "the author's explicit choice: the path travels as written")
        XCTAssertNil(state.collectionsFile.collections[state.activeCollection]?.publish?.intent.pathMarks["c"])
        XCTAssertEqual(state.collectionsCache.published[state.activeCollection]?.markedValues, [],
                       "forgetting then publishing is how a path leaves this machine's list")
    }

    func testARowHoldingAPathThisMachineKeepsBackStartsTicked() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let first = PublishModel(state: state, collection: state.activeCollection)
        first.folder = try publishFolder(h).path
        first.pathRows[0].marked = true
        first.pathRows[0].name = "srv"
        XCTAssertNil(first.publish())
        let file = try publishFolder(h).appendingPathComponent(first.fileName)

        // The other machine dropped the mark; its sidecar is here, its master list is not.
        var sidecar = state.collectionsFile
        var record = try XCTUnwrap(sidecar.collections[state.activeCollection]?.publish)
        record.intent = record.intent.replacingPathMarks(of: "c", with: [:])
        sidecar.collections[state.activeCollection]?.publish = record
        try sidecar.save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        state.reload()
        XCTAssertEqual(state.publishError?.message, AppState.keptPathCarriedError("c", FieldName.argument(1)))

        let sheet = PublishModel(state: state, collection: state.activeCollection)
        let row = try XCTUnwrap(sheet.pathRows.first { $0.connector == "c" })
        XCTAssertTrue(row.marked, "the record no longer marks it, but this machine has sent it as a placeholder")
        XCTAssertEqual(sheet.unresolvedMarks, [])
        XCTAssertNil(sheet.publish())
        XCTAssertNil(state.publishError)
        XCTAssertEqual(try documentArgs("c", in: Data(contentsOf: file)), ["${CC_NEEDS:path}", "--quiet"])
    }

    func testAMarkWhoseConnectorIsGoneWaitsToBeForgotten() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let first = PublishModel(state: state, collection: state.activeCollection)
        first.folder = try publishFolder(h).path
        first.pathRows[0].marked = true
        XCTAssertNil(first.publish())
        let file = try publishFolder(h).appendingPathComponent(first.fileName)

        // Renamed by an older app on another machine: the master list arrives, the record does not follow.
        var store = try h.storeOnDisk()
        let entry = try XCTUnwrap(store.collections[store.activeCollection]?.mcps.removeValue(forKey: "c"))
        store.collections[store.activeCollection]?.mcps["d"] = entry
        try MasterStoreIO.save(store, to: h.masterStoreURL)
        state.reload(trigger: .externalStoreAdoption)

        let sheet = PublishModel(state: state, collection: state.activeCollection)
        XCTAssertEqual(sheet.unresolvedMarks.map(\.connector), ["c"])
        let row = try XCTUnwrap(sheet.pathRows.firstIndex { $0.connector == "d" })
        sheet.pathRows[row].marked = true
        XCTAssertEqual(sheet.unresolvedMarks.map(\.connector), ["c"], "a tick in another connector answers nothing about this one")
        XCTAssertFalse(sheet.canPublish)
        sheet.forgetUnresolvedMark(try XCTUnwrap(sheet.unresolvedMarks.first { $0.connector == "c" }).id)
        XCTAssertTrue(sheet.canPublish)
        XCTAssertNil(sheet.publish())
        XCTAssertNil(state.publishError)
        XCTAssertEqual(try documentArgs("d", in: Data(contentsOf: file)), ["${CC_NEEDS:path}", "--quiet"])
    }

    // MARK: - Each lost mark on its own

    /// "ledger" published with two marked paths, then both paths edited outside the editor, so
    /// each mark has lost its argument. Returns the document's path.
    private func publishTwoMarksThenLoseBoth(_ h: AppStateHarness, _ state: AppState) throws -> URL {
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: node(["/Users/d/a/one.js", "/Users/d/b/two.js"])),
                                  renamedFrom: nil))
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: PublishIntent(shareValues: [:], pathMarks: ["ledger": [
            JSONPointer(["args", "0"]): .init(name: "first", hint: "h1", value: "/Users/d/a/one.js"),
            JSONPointer(["args", "1"]): .init(name: "second", hint: "h2", value: "/Users/d/b/two.js"),
        ]], hints: [:]), reviewedValues: ["/Users/d/a/one.js", "/Users/d/b/two.js"]))
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: node(["/Users/d/a2/one.js", "/Users/d/b2/two.js"])),
                                  renamedFrom: "ledger"))
        return folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json")
    }

    func testEachLostMarkIsAnsweredByATickOfItsOwn() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let file = try publishTwoMarksThenLoseBoth(h, state)
        let before = try Data(contentsOf: file)
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        XCTAssertEqual(sheet.unresolvedMarks.map { "\($0.connector) \($0.name)" }, ["ledger first", "ledger second"])
        let one = try XCTUnwrap(sheet.pathRows.firstIndex { $0.value == "/Users/d/a2/one.js" })
        let two = try XCTUnwrap(sheet.pathRows.firstIndex { $0.value == "/Users/d/b2/two.js" })

        sheet.pathRows[one].marked = true
        XCTAssertEqual(sheet.unresolvedMarks.map(\.name), ["second"], "one tick answers one mark")
        XCTAssertEqual(sheet.pathRows[one].name, "first")
        XCTAssertEqual(sheet.pathRows[one].hint, "h1")
        XCTAssertFalse(sheet.canPublish)
        XCTAssertEqual(sheet.publish(), PublishModel.unresolvedMarkNote("ledger", "second"))
        XCTAssertEqual(try Data(contentsOf: file), before, "the second moved path does not travel")

        sheet.pathRows[two].marked = true
        XCTAssertEqual(sheet.unresolvedMarks, [])
        XCTAssertEqual(sheet.pathRows[two].name, "second", "each tick carries the name of the mark it answers")
        XCTAssertEqual(sheet.pathRows[two].hint, "h2")
        XCTAssertNil(sheet.publish())
        XCTAssertEqual(try documentArgs("ledger", in: Data(contentsOf: file)), ["${CC_NEEDS:first}", "${CC_NEEDS:second}"])
        XCTAssertEqual(state.collectionsFile.collections[state.activeCollection]?.publish?.intent.pathMarks["ledger"], [
            JSONPointer(["args", "0"]): .init(name: "first", hint: "h1", value: "/Users/d/a2/one.js"),
            JSONPointer(["args", "1"]): .init(name: "second", hint: "h2", value: "/Users/d/b2/two.js"),
        ])
    }

    func testUntickingPutsBackTheMarkItAnsweredAndEachMarkIsForgottenAlone() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        _ = try publishTwoMarksThenLoseBoth(h, state)
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        let one = try XCTUnwrap(sheet.pathRows.firstIndex { $0.value == "/Users/d/a2/one.js" })
        let two = try XCTUnwrap(sheet.pathRows.firstIndex { $0.value == "/Users/d/b2/two.js" })
        sheet.pathRows[one].marked = true
        sheet.pathRows[two].marked = true
        sheet.pathRows[one].marked = false
        XCTAssertEqual(sheet.unresolvedMarks.map(\.name), ["first"], "unticking gives back the mark that row answered")

        let first = try XCTUnwrap(sheet.unresolvedMarks.first)
        sheet.pathRows[two].marked = false
        XCTAssertEqual(sheet.unresolvedMarks.map(\.name), ["first", "second"])
        sheet.forgetUnresolvedMark(first.id)
        XCTAssertEqual(sheet.unresolvedMarks.map(\.name), ["second"], "Forget Mark forgets that one mark")
        XCTAssertFalse(sheet.canPublish)
    }

    // MARK: - A kept value outside the argument rows

    private let keptPath = "/Users/d/ledger/dist/index.js"

    /// "ledger" published with its path marked; then the other machine's save drops the mark and
    /// leaves the path only in an `additional` field no row offers.
    private func publishThenMoveThePathOutOfTheRows(_ h: AppStateHarness, _ state: AppState) throws -> URL {
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: node([keptPath])), renamedFrom: nil))
        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(state.activeCollection, to: folder.path, intent: PublishIntent(shareValues: [:], pathMarks: ["ledger": [
            JSONPointer(["args", "0"]): .init(name: "server_path", hint: nil, value: keptPath)]], hints: [:]), reviewedValues: [keptPath]))
        var sidecar = state.collectionsFile
        var record = try XCTUnwrap(sidecar.collections[state.activeCollection]?.publish)
        record.intent = record.intent.replacingPathMarks(of: "ledger", with: [:])
        sidecar.collections[state.activeCollection]?.publish = record
        try sidecar.save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        var store = try h.storeOnDisk()
        store.collections[store.activeCollection]?.mcps["ledger"]?.config = .object([
            "command": .string("node"), "args": .array([.string("--serve")]), "cwd": .string(keptPath)])
        try MasterStoreIO.save(store, to: h.masterStoreURL)
        state.reload(trigger: .externalStoreAdoption)
        XCTAssertEqual(state.publishError?.message, AppState.keptPathCarriedError("ledger", FieldName.document("additional.cwd")))
        return folder.appendingPathComponent(Slug.make(state.activeCollection) + ".json")
    }

    func testAKeptValueOutsideTheRowsIsListedAndHoldsPublishUntilReleased() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let file = try publishThenMoveThePathOutOfTheRows(h, state)
        let before = try Data(contentsOf: file)
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        XCTAssertEqual(sheet.unresolvedMarks, [])
        XCTAssertEqual(sheet.keptPaths, [PublishModel.KeptPath(value: keptPath, connector: "ledger", field: "additional.cwd", kind: .path)])
        XCTAssertFalse(sheet.canPublish)
        XCTAssertFalse(sheet.canExport)
        XCTAssertEqual(sheet.publish(), PublishModel.keptPathNote("ledger", FieldName.document("additional.cwd")))
        let out = h.dir.file("away/copy.json")
        XCTAssertEqual(sheet.export(to: out.path), PublishModel.keptPathNote("ledger", FieldName.document("additional.cwd")))
        XCTAssertFalse(FileManager.default.fileExists(atPath: out.path))
        XCTAssertEqual(try Data(contentsOf: file), before)
        XCTAssertEqual(state.collectionsCache.published[state.activeCollection]?.markedValues, [keptPath], "nothing released it")

        sheet.releaseKeptPath(keptPath)
        XCTAssertEqual(sheet.keptPaths, [])
        XCTAssertTrue(sheet.canPublish)
        XCTAssertNil(sheet.publish())
        XCTAssertNil(state.publishError)
        XCTAssertTrue(jsonText(try Data(contentsOf: file), contains: keptPath),
                      "the author's explicit choice: the path travels as written")
        let binding = state.collectionsCache.published[state.activeCollection]
        XCTAssertEqual(binding?.markedValues, [])
        XCTAssertEqual(binding?.releasedValues, [keptPath])
        state.reload()
        XCTAssertNil(state.publishError, "a released path stays released")
    }

    func testAHintHoldingAMarkedPathIsListedWhereItSits() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: node([keptPath])), renamedFrom: nil))
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        sheet.folder = try publishFolder(h).path
        sheet.pathRows[0].marked = true
        sheet.pathRows[0].name = "server_path"
        sheet.pathRows[0].hint = "like mine, \(keptPath)"
        XCTAssertEqual(sheet.keptPaths.map { "\($0.connector) \($0.field)" }, ["ledger needs.server_path.hint"])
        XCTAssertFalse(sheet.canPublish)
        XCTAssertEqual(sheet.publish(), PublishModel.keptPathNote("ledger", FieldName.hint("server_path")))
        XCTAssertFalse(FileManager.default.fileExists(atPath: try publishFolder(h).appendingPathComponent(sheet.fileName).path))
        sheet.pathRows[0].hint = "your ledger clone"
        XCTAssertTrue(sheet.canPublish)
        XCTAssertNil(sheet.publish())
    }

    /// A filesystem server started on "." and a remote connector beside it: a relative value
    /// counts only where a string is exactly it, so the dots in a URL are not the marked path.
    func testAShortRelativeMarkHoldsBackOnlyItself() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "files", entry: MCPEntry(config: .object([
            "command": .string("npx"), "args": .array([.string("-y"), .string("@modelcontextprotocol/server-filesystem"), .string(".")])])),
            renamedFrom: nil))
        XCTAssertNil(state.upsert(name: "notion", entry: MCPEntry(config: AppStateHarness.remote("https://mcp.notion.com/mcp")),
                                  renamedFrom: nil))
        let sheet = PublishModel(state: state, collection: state.activeCollection)
        sheet.folder = try publishFolder(h).path
        let dot = try XCTUnwrap(sheet.pathRows.firstIndex { $0.value == "." })
        sheet.pathRows[dot].marked = true
        XCTAssertEqual(sheet.keptPaths, [])
        XCTAssertTrue(sheet.canPublish)
        XCTAssertNil(sheet.publish())
        XCTAssertNil(state.publishError)
        let data = try Data(contentsOf: try publishFolder(h).appendingPathComponent(sheet.fileName))
        XCTAssertEqual(try documentArgs("files", in: data), ["-y", "@modelcontextprotocol/server-filesystem", "${CC_NEEDS:path}"])
        state.reload()
        XCTAssertNil(state.publishError)
    }

    func testExportWritesTheSameDocumentOnce() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let model = PublishModel(state: state, collection: state.activeCollection)
        model.envRows[1].share = true
        let out = h.dir.file("away/copy.json")
        XCTAssertNil(model.export(to: out.path))
        XCTAssertEqual(try Data(contentsOf: out), try state.exportDocument(for: state.activeCollection,
                                                                          intent: model.intent).serialized())
        XCTAssertFalse(state.isPublished(state.activeCollection), "exporting binds nothing")
    }

    func testPublishAgainRetriesAFailedWrite() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let folder = try publishFolder(h)
        let model = PublishModel(state: state, collection: state.activeCollection)
        model.folder = folder.path
        XCTAssertNil(model.publish())
        let file = folder.appendingPathComponent(model.fileName)
        XCTAssertTrue(FileManager.default.fileExists(atPath: file.path))

        // A file where the folder belongs fails the write on both platforms; the next store
        // change is what raises the banner.
        try FileManager.default.removeItem(at: folder)
        try TempDir.touch(folder, "not a folder")
        XCTAssertNil(state.upsert(name: "d", entry: MCPEntry(config: connector), renamedFrom: nil))
        XCTAssertEqual(state.publishError?.collection, state.activeCollection)

        try FileManager.default.removeItem(at: folder)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        // Nothing about the document or the ticks has changed since the failure, so only an
        // unconditional write puts it back — which is what pressing Publish again has to do,
        // rather than waiting for whatever the user changes next.
        let retry = PublishModel(state: state, collection: state.activeCollection)
        XCTAssertEqual(retry.folder, folder.path)
        XCTAssertNil(retry.publish())
        XCTAssertNil(state.publishError, "pressing Publish again is a retry")
        XCTAssertNotNil(try CollectionDocument.decode(try Data(contentsOf: file)).connectors["d"],
                        "the change the failed write held back lands with it")
    }

    func testAnIllegalPathNameIsSanitizedNotDropped() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let model = PublishModel(state: state, collection: state.activeCollection)
        model.pathRows[0].marked = true

        model.pathRows[0].name = "my path!"
        XCTAssertEqual(model.intent.pathMarks["c"]?[JSONPointer(["args", "0"])]?.name, "my_path_")
        XCTAssertTrue(model.preview.contains("${CC_NEEDS:my_path_}"))
        XCTAssertFalse(jsonText(model.preview, contains: "/Users/d/x.js"),
                       "a name nobody can fill must not publish the path the mark was hiding")

        model.pathRows[0].name = "2nd path"
        XCTAssertEqual(model.intent.pathMarks["c"]?[JSONPointer(["args", "0"])]?.name, "2nd_path",
                       "a leading digit is legal in a marker name; only the space is replaced")

        // Nothing to make a name out of is the one case left: the row stays unmarked rather
        // than writing a marker with no name in it.
        model.pathRows[0].name = "   "
        XCTAssertNil(model.intent.pathMarks["c"])
        XCTAssertTrue(model.preview.contains("/Users/d/x.js"))
    }

    func testEachSectionKnowsWhetherItHasAnythingToShow() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let model = PublishModel(state: state, collection: state.activeCollection)
        XCTAssertTrue(model.hasEnvRows)
        XCTAssertTrue(model.hasPathRows)

        // A remote connector carries no passthrough environment and no arguments of its own,
        // so the sheet over one has neither section.
        XCTAssertNil(state.createCollection(named: "Remote"))
        state.remove(names: ["c"], in: "Remote")
        XCTAssertNil(state.upsert(name: "r", entry: MCPEntry(config: AppStateHarness.remote("https://r.example/mcp")),
                                  renamedFrom: nil, in: "Remote"))
        let bare = PublishModel(state: state, collection: "Remote")
        XCTAssertEqual(bare.envRows, [])
        XCTAssertEqual(bare.pathRows, [])
        XCTAssertFalse(bare.hasEnvRows)
        XCTAssertFalse(bare.hasPathRows)
    }

    func testAnEnvRowCarriesTheValueTheTickWouldPublish() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let model = PublishModel(state: state, collection: state.activeCollection)

        // In full and unelided: the tick beside it is a decision about exactly these bytes.
        XCTAssertEqual(model.envRows.map(\.name), ["A", "B"])
        XCTAssertEqual(model.envRows.map(\.value), ["sk-live-secret", "us"])
        XCTAssertFalse(jsonText(model.preview, contains: "sk-live-secret"), "stripped until it is ticked")
        model.envRows[0].share = true
        XCTAssertTrue(model.preview.contains("sk-live-secret"))
        XCTAssertEqual(model.envRows[0].value, "sk-live-secret", "the tick does not change what is there")
    }

    func testTheSheetOwnsItsButtonsAndItsExportTitle() {
        XCTAssertEqual(PublishModel.cancelButton, "Cancel")
        XCTAssertEqual(PublishModel.chooseFolderButton, "Choose Folder")
        XCTAssertEqual(PublishModel.markPathLabel, "Mark as a path this machine supplies")
        XCTAssertEqual(PublishModel.exportTitle("Data team"), "Export “Data team”")
    }

    func testTheFooterNamesTheFileAndTheOriginOnceThereIsOne() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        let collection = state.activeCollection
        let before = PublishModel(state: state, collection: collection)

        // Nothing published yet, so there is no origin to show and the footer is the name alone.
        XCTAssertEqual(before.originShort, "")
        XCTAssertEqual(before.footerLine, before.fileName)

        let folder = try publishFolder(h)
        XCTAssertNil(state.startPublishing(collection, to: folder.path, intent: .none))
        let origin = try XCTUnwrap(state.collectionsFile.collections[collection]?.publish?.origin)
        XCTAssertEqual(origin.count, 36, "a UUID, which is what the eight characters are cut from")

        let after = PublishModel(state: state, collection: collection)
        XCTAssertEqual(after.originShort, String(origin.prefix(8)))
        XCTAssertEqual(after.footerLine, PublishModel.footerLine(after.fileName, after.originShort))
        XCTAssertEqual(after.footerLine, "\(after.fileName) · \(after.originShort)")
        // The document an export writes carries that same origin, so both sheets show one thing.
        XCTAssertEqual(try state.exportDocument(for: collection, intent: .none).origin, origin)
    }
    func testAnExportCarriesOnlyTheTickedConnectors() throws {
        let (h, state) = try started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "other", entry: MCPEntry(config: .object([
            "command": .string("uvx"),
            "env": .object(["OTHER_KEY": .string("sk-other-secret")]),
        ])), renamedFrom: nil))
        let collection = state.activeCollection
        let held = try XCTUnwrap(state.store.collections[collection]?.mcps.keys).sorted()
        XCTAssertTrue(held.contains("c") && held.contains("other"))

        // The whole collection, as publishing always takes it.
        let whole = PublishModel(state: state, collection: collection)
        XCTAssertNil(whole.connectors)
        XCTAssertTrue(Set(whole.envRows.map(\.connector)).isSuperset(of: ["c", "other"]))
        XCTAssertTrue(whole.pathRows.contains { $0.connector == "c" })

        // One ticked name: the rows, the preview and the document all stop at it.
        let subset = PublishModel(state: state, collection: collection, connectors: ["other"])
        XCTAssertEqual(subset.envRows.map(\.connector), ["other"])
        XCTAssertEqual(subset.envRows.map(\.name), ["OTHER_KEY"])
        XCTAssertEqual(subset.pathRows, [], "c's path argument is not this export's business")
        XCTAssertFalse(subset.hasPathRows)
        XCTAssertFalse(subset.preview.contains("\"c\""))
        XCTAssertTrue(subset.preview.contains("other"))
        XCTAssertEqual(subset.warnings, [], "the ticked connector's value is not credential-shaped")

        let out = h.dir.file("one.json")
        XCTAssertNil(subset.export(to: out.path))
        let document = try CollectionDocument.decode(try Data(contentsOf: out))
        XCTAssertEqual(document.connectors.keys.sorted(), ["other"])
        XCTAssertEqual(state.store.collections[collection]?.mcps.keys.sorted(), held,
                       "exporting a subset takes nothing out of the collection")
    }
}
