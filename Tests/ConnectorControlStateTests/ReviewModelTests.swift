import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ReviewModelTests.cs.
/// The Review & Apply sheet over a real subscription: a document on disk, a change to it, and the
/// one button that lets the change reach Claude.
@MainActor
final class ReviewModelTests: XCTestCase {

    /// Subscribes to the sample, then publishes a version of it with github gone and dbt's
    /// arguments changed, and reads it into a pending update. Every read here goes through
    /// `recomputePending`, the source watcher's own read, rather than waiting on the watcher:
    /// AppStateCollectionsTests proves the watcher delivers the change.
    private func pending(_ h: AppStateHarness, _ state: AppState) throws -> URL {
        let url = try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["github"] = nil
        doc.connectors["dbt"]?.launcher = .local(.init(command: "npx", args: ["-y", "@dbt/mcp@2"], platform: .mac))
        try h.writeDocument(doc, at: url)
        state.recomputePending()
        XCTAssertNotNil(state.pendingUpdates["Data team"])
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
        let url = try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["jira"] = .init(launcher: .remote(.init(url: "https://mcp.jira.example/", auth: .automatic,
                                                               package: "mcp-remote", extraArgs: [])))
        try h.writeDocument(doc, at: url)
        state.recomputePending()
        XCTAssertNotNil(state.pendingUpdates["Data team"])

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
        try h.writeDocument(doc, at: url)
        state.recomputePending()
        XCTAssertEqual(state.pendingUpdates["Data team"]?.removed, ["github", "notion"])

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
        try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)

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
    func testTheSheetOwnsItsFooterButtons() {
        XCTAssertEqual(ReviewModel.cancelButton, "Cancel")
        XCTAssertEqual(ReviewModel.refreshButton, "Refresh")
        XCTAssertEqual(ReviewModel.applyButton, "Apply")
    }
}
