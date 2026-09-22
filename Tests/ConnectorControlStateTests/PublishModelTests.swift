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
                       PublishIntent.PathMark(name: "srv", hint: "your ledger clone, then dist/index.js"))
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
        XCTAssertFalse(model.preview.contains("sk-live-secret"), "the default is stripped")
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
        XCTAssertFalse(model.preview.contains("/Users/d/x.js"),
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
        XCTAssertEqual(state.exportDocument(for: collection, intent: .none).origin, origin)
    }
}
