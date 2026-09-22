import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/ReviewModelTests.cs. The Review & Apply sheet
/// over a real subscription: a document on disk, a change to it, and the one button that lets
/// the change reach Claude.
@MainActor
final class ReviewModelTests: XCTestCase {
    private func writeDocument(_ doc: CollectionDocument, at url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try doc.serialized().write(to: url)
    }

    /// Subscribes to the sample, then publishes a version of it with github gone and dbt's
    /// arguments changed, and waits for that to become a pending update.
    private func pending(_ h: AppStateHarness, _ state: AppState) throws -> URL {
        let url = h.dir.file("data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["github"] = nil
        doc.connectors["dbt"]?.launcher = .local(.init(command: "npx", args: ["-y", "@dbt/mcp@2"], platform: .mac))
        try writeDocument(doc, at: url)
        try TempDir.bumpModificationDate(of: url)
        XCTAssertTrue(h.ui.pumpUntil({ state.pendingUpdates["Data team"] != nil }, timeout: 8))
        return url
    }

    func testRowsDescribeTheDiffAndApplyLandsIt() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        _ = try pending(h, state)

        let model = ReviewModel(state: state, collection: "Data team")
        XCTAssertEqual(model.title, "Update to Data team")
        XCTAssertEqual(model.summary, "removes github; changes dbt")
        XCTAssertEqual(model.rows.map(\.name), ["github", "dbt"], "added, then removed, then changed")
        XCTAssertEqual(model.rows.map(\.kind), [.removed, .changed])
        XCTAssertEqual(model.rows.map(\.id), ["github", "dbt"])

        let github = try XCTUnwrap(model.rows.first)
        XCTAssertEqual(github.before, state.store.collections["Data team"]?.mcps["github"]?.config.editorText())
        XCTAssertNil(github.after)
        let dbt = try XCTUnwrap(model.rows.last)
        XCTAssertEqual(dbt.before, state.store.collections["Data team"]?.mcps["dbt"]?.config.editorText())
        XCTAssertEqual(dbt.after, state.pendingDocument(for: "Data team")?.connectors["dbt"]?.config.editorText())
        XCTAssertEqual(dbt.after?.contains("@dbt/mcp@2"), true)

        XCTAssertNil(model.apply())
        XCTAssertTrue(state.pendingUpdates.isEmpty)
        XCTAssertTrue(model.rows.isEmpty)
        XCTAssertEqual(model.summary, "")
        XCTAssertNil(state.store.collections["Data team"]?.mcps["github"])
        XCTAssertNil(model.apply(), "a second Apply has nothing left to do and nothing to report")
    }

    func testAnAddedConnectorHasNoBeforeSide() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["jira"] = .init(launcher: .remote(.init(url: "https://mcp.jira.example/", auth: .automatic,
                                                               package: "mcp-remote", extraArgs: [])))
        try writeDocument(doc, at: url)
        try TempDir.bumpModificationDate(of: url)
        XCTAssertTrue(h.ui.pumpUntil({ state.pendingUpdates["Data team"] != nil }, timeout: 8))

        let model = ReviewModel(state: state, collection: "Data team")
        XCTAssertEqual(model.rows.map(\.name), ["jira"])
        XCTAssertEqual(model.rows.first?.kind, .added)
        XCTAssertNil(model.rows.first?.before)
        XCTAssertEqual(model.rows.first?.after, state.pendingDocument(for: "Data team")?.connectors["jira"]?.config.editorText())
        XCTAssertNil(model.apply())
        XCTAssertEqual(state.store.collections["Data team"]?.mcps["jira"]?.enabled, false, "an added connector arrives off")
    }

    func testApplyRefusesWhenTheSourceMovedUnderTheSheet() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try pending(h, state)
        let model = ReviewModel(state: state, collection: "Data team")
        XCTAssertFalse(model.sourceMoved)
        XCTAssertEqual(model.rows.map(\.name), ["github", "dbt"])

        // The author commits again while the sheet is open.
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["github"] = nil
        doc.connectors["notion"] = nil
        doc.connectors["dbt"]?.launcher = .local(.init(command: "npx", args: ["-y", "@dbt/mcp@3"], platform: .mac))
        try writeDocument(doc, at: url)
        try TempDir.bumpModificationDate(of: url)
        XCTAssertTrue(h.ui.pumpUntil({ state.pendingUpdates["Data team"]?.removed == ["github", "notion"] }, timeout: 8))

        XCTAssertEqual(model.apply(), ReviewModel.sourceMovedMessage, "the rows on screen are not what would land")
        XCTAssertTrue(model.sourceMoved)
        XCTAssertNotNil(state.pendingUpdates["Data team"], "nothing was applied")
        XCTAssertNotNil(state.store.collections["Data team"]?.mcps["notion"])

        model.refresh()
        XCTAssertFalse(model.sourceMoved)
        XCTAssertEqual(model.rows.map(\.name), ["github", "notion", "dbt"])
        XCTAssertNil(model.apply())
        XCTAssertTrue(state.pendingUpdates.isEmpty)
        XCTAssertEqual(state.store.collections["Data team"]?.mcps["dbt"]?.config.value(at: JSONPointer(["args", "1"])),
                       .string("@dbt/mcp@3"))
    }

    func testACollectionWithNothingPendingHasNoRows() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = h.dir.file("data-team.json")
        try writeDocument(CollectionDocumentSamples.dataTeam, at: url)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))

        let model = ReviewModel(state: state, collection: "Data team")
        XCTAssertTrue(model.rows.isEmpty)
        XCTAssertEqual(model.summary, "")
        XCTAssertNil(model.apply())
    }
    func testTheKindsGroupTheListInTheOrderTheSummaryReads() {
        XCTAssertEqual(ReviewModel.kinds, [.added, .removed, .changed])
        XCTAssertEqual(ReviewModel.kinds.map(ReviewModel.kindLabel), ["Added", "Removed", "Changed"])
        XCTAssertEqual(ReviewModel.kindLabel(.added), ReviewModel.addedLabel)
        XCTAssertEqual(ReviewModel.kindLabel(.removed), ReviewModel.removedLabel)
        XCTAssertEqual(ReviewModel.kindLabel(.changed), ReviewModel.changedLabel)
    }
}
