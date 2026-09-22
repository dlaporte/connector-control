import Combine
import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/FlyoutModelTests.cs. Rows are
/// value types identified by name, so "the same row object" becomes "the row
/// with that name".
@MainActor
final class PopoverModelTests: XCTestCase {
    func testHeaderTextsFollowTheStore() {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(popover.subtitle, "No connectors configured")
        XCTAssertEqual(popover.collectionChipText, "Default ▾")
        XCTAssertTrue(popover.isEmpty)
        XCTAssertNil(state.upsert(name: "z", entry: MCPEntry(config: AppStateHarness.remote("https://z.example/mcp")), renamedFrom: nil))
        XCTAssertEqual(popover.subtitle, "1 of 1 enabled")
        XCTAssertFalse(popover.isEmpty)
    }

    func testRowsAreSortedOrdinallyWithEditTooltips() {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "Zebra", entry: MCPEntry(config: AppStateHarness.remote("https://zebra.example/mcp")), renamedFrom: nil))
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(popover.rows.map(\.name), ["Zebra", "aws-mcp", "scoutbook", "service-now"])   // uppercase first: ordinal
        XCTAssertEqual(popover.rows[1].editTooltip, "Edit “aws-mcp”")
        XCTAssertTrue(popover.rows.allSatisfy(\.enabled))
    }

    func testTogglingARowPersistsAndAppliesThroughAppState() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        popover.setEnabled("aws-mcp", false)
        XCTAssertEqual(try h.storeOnDisk().mcps["aws-mcp"]?.enabled, false)
        XCTAssertNil(try h.claudeServers()["aws-mcp"])
        XCTAssertEqual(popover.subtitle, "2 of 3 enabled")
        XCTAssertEqual(popover.rows.first { $0.name == "aws-mcp" }?.enabled, false)   // the row is still there, now off
    }

    func testRowsFollowExternalStateChanges() {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        // The rows read through to AppState; the republish in init is what makes
        // the popover repaint, and it is the one line a passthrough cannot prove.
        var repaints = 0
        let sink = popover.objectWillChange.sink { _ in repaints += 1 }
        defer { sink.cancel() }
        state.setEnabled("aws-mcp", false)
        XCTAssertEqual(popover.rows.first { $0.name == "aws-mcp" }?.enabled, false)
        XCTAssertGreaterThan(repaints, 0, "an AppState change is republished to the view")
        state.remove(name: "scoutbook")
        XCTAssertEqual(popover.rows.map(\.name), ["aws-mcp", "service-now"])
        XCTAssertNil(state.upsert(name: "alpha", entry: MCPEntry(config: AppStateHarness.remote("https://alpha.example/mcp")), renamedFrom: nil))
        XCTAssertEqual(popover.rows.map(\.name), ["alpha", "aws-mcp", "service-now"])
        // dispose() cuts the republish: the rows still read through, nothing repaints.
        popover.dispose()
        let before = repaints
        state.setEnabled("alpha", false)
        XCTAssertEqual(popover.rows.first { $0.name == "alpha" }?.enabled, false)
        XCTAssertEqual(repaints, before)
    }

    func testTheChipMenuHasNoHousekeepingItemsAndAddIsAllowedInALocalCollection() {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(popover.collectionItems, [CollectionMenuItem(name: "Default", isActive: true)])
        XCTAssertTrue(popover.canAddConnector)

        XCTAssertNil(state.createCollection(named: "Work"))
        XCTAssertEqual(popover.collectionItems.map(\.name), ["Default", "Work"], "switching only — New, Rename and Delete live in the window")
        XCTAssertEqual(popover.collectionItems.map(\.isActive), [false, true])
        XCTAssertEqual(popover.collectionItems.map(\.isSynced), [false, false])
        XCTAssertEqual(popover.collectionItems.map(\.hasPendingUpdate), [false, false])
        XCTAssertEqual(popover.collectionChipText, "Work ▾")
        popover.switchCollection("Default")
        XCTAssertEqual(popover.collectionChipText, "Default ▾")
    }

    func testASyncedCollectionIsMarkedInTheMenuAndClosedToAdditions() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        try CollectionsFile(collections: ["Team": CollectionsFile.Entry(kind: .synced, fileName: "team.json")])
            .save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        state.reload()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        state.pendingUpdates = ["Team": CollectionDiff(added: ["jira"], removed: [], changed: [])]
        XCTAssertEqual(popover.collectionItems.map(\.isSynced), [false, true])
        XCTAssertEqual(popover.collectionItems.map(\.hasPendingUpdate), [false, true])
        XCTAssertFalse(popover.canAddConnector)
        XCTAssertEqual(PopoverModel.addDisabledTooltip, "Additions go in a local collection.")
        popover.switchCollection("Default")
        XCTAssertTrue(popover.canAddConnector)
    }

    func testTheCollectionBannerCarriesItsTextAndButton() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        try CollectionsFile(collections: [
            "Team": CollectionsFile.Entry(kind: .synced, fileName: "team.json"),
            "Default": CollectionsFile.Entry(
                kind: .local, publish: CollectionsFile.PublishRecord(slug: "default", origin: "origin", intent: .none)),
        ]).save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        // A folder that exists and can be written: a binding pointing at one that cannot would
        // raise a real publish failure on the reload below, ahead of the banner under test.
        let folder = h.dir.file("pub")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try CollectionsLocalCache(synced: [:], published: ["Default": .init(folder: folder.path, lastWrittenHash: nil)])
            .save(to: state.service.paths.collectionsCacheURL, staging: nil)
        state.reload()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }

        XCTAssertEqual(popover.collectionBanner, .locate(collection: "Team", fileName: "team.json"))
        XCTAssertEqual(popover.collectionBannerText, "Team's file isn’t on this Mac yet.")
        XCTAssertEqual(popover.collectionBannerButton, "Locate team.json…")

        let diff = CollectionDiff(added: ["jira"], removed: ["confluence"], changed: [])
        state.pendingUpdates = ["Team": diff]
        XCTAssertEqual(popover.collectionBannerText, "Team changed at its source: adds jira; removes confluence.")
        XCTAssertEqual(popover.collectionBannerButton, "Review & Apply…")

        state.publishError = (collection: "Default", message: "the folder is read-only")
        XCTAssertEqual(popover.collectionBannerText,
                       "Couldn’t publish Default to \(folder.path): the folder is read-only")
        XCTAssertEqual(popover.collectionBannerButton, "Choose Folder…")

        state.publishError = nil
        state.pendingUpdates = [:]
        popover.switchCollection("Default")
        XCTAssertEqual(popover.collectionBanner, .locate(collection: "Team", fileName: "team.json"),
                       "another collection's unlocated file still gets the slot")
    }

    func testFooterPrefersRetryOverRestart() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.claude.isRunning = true
        h.claude.launchDate = h.now.addingTimeInterval(-3600)
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(popover.footer, FooterKind.hidden)
        XCTAssertFalse(popover.showFooter)

        state.setEnabled("aws-mcp", false)
        XCTAssertEqual(popover.footer, .restartRequired)
        XCTAssertEqual(popover.footerTitle, "Restart Required")
        XCTAssertEqual(popover.footerGlyph, "arrow.clockwise")
        XCTAssertTrue(popover.showFooter)

        try Data("{oops".utf8).write(to: h.claudeConfigURL)
        state.setEnabled("scoutbook", false)   // apply fails
        XCTAssertEqual(popover.footer, .retryApply)
        XCTAssertEqual(popover.footerTitle, "Apply Failed — Retry")
        XCTAssertEqual(popover.footerGlyph, "exclamationmark.arrow.circlepath")
        XCTAssertNotNil(popover.errorMessage)

        try Data(Fixtures.realisticClaudeConfig.utf8).write(to: h.claudeConfigURL)
        popover.footerAction()   // retry
        XCTAssertEqual(popover.footer, .restartRequired)
        XCTAssertEqual(try h.claudeServers().keys.sorted(), ["service-now"])
    }

    func testFooterActionRestartsWhenRestartIsRequired() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.settings.confirmBeforeRestart = false
        h.claude.isRunning = true
        h.claude.launchDate = h.now.addingTimeInterval(-3600)
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        state.setEnabled("aws-mcp", false)
        popover.footerAction()
        XCTAssertEqual(h.claude.restartCalls, 1)
    }

    func testOpenedRunsARoutineReload() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        try h.writeClaudeServers([("scoutbook", try XCTUnwrap(state.store.mcps["scoutbook"]).config)])
        popover.opened()
        XCTAssertEqual(try h.claudeServers().keys.sorted(), ["aws-mcp", "scoutbook", "service-now"])
        XCTAssertEqual(h.notifier.sent[0].body, AppState.claudeConfigRegeneratedBody)
    }

    func testEntryForReturnsTheLiveEntryOrNull() {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(popover.entryFor("scoutbook"), state.store.mcps["scoutbook"])
        XCTAssertNil(popover.entryFor("gone"))
    }

    func testRowsCarryTheToolWarningAndFollowLaterProbeResults() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.tools.statuses[.npx] = .notFound
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertTrue(popover.rows.allSatisfy { $0.toolWarning == nil })   // nothing probed yet: no glyph

        state.refreshTools([.npx])
        XCTAssertTrue(h.ui.pumpUntil({ state.toolStatuses[.npx] != nil }, timeout: 5))
        // All three seeded connectors run `npx -y mcp-remote`.
        XCTAssertTrue(popover.rows.allSatisfy { $0.toolWarning == "Needs npx, which wasn’t found. Edit to see how to install it." })

        // A connector whose command is a full path needs no PATH lookup, so it never warns.
        XCTAssertNil(state.upsert(name: "pathed", entry: MCPEntry(config: .object(["command": .string("/usr/local/bin/node")])), renamedFrom: nil))
        let pathed = popover.rows.first { $0.name == "pathed" }
        XCTAssertNil(pathed?.toolWarning)
        XCTAssertNotNil(popover.rows.first { $0.name == "aws-mcp" }?.toolWarning)   // the others are unchanged

        // Installing npx: the next probe publishes found and every glyph clears.
        h.tools.statuses[.npx] = .found(path: "/opt/homebrew/bin/npx", version: "10.9.2")
        state.refreshTools([.npx])
        XCTAssertTrue(h.ui.pumpUntil({ popover.rows.allSatisfy { $0.toolWarning == nil } }, timeout: 5))
        XCTAssertTrue(popover.rows.allSatisfy(\.enabled))   // the glyph never touched the switch
    }

    /// macOS only: a launcher only the login shell can see.
    func testRowsCarryTheShellOnlyWarning() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.tools.statuses[.npx] = .foundInShellOnly(path: "/Users/me/.nvm/versions/node/v22/bin/npx", version: "10.9.2")
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        popover.opened()
        XCTAssertTrue(h.ui.pumpUntil({ popover.rows.allSatisfy { $0.toolWarning != nil } }, timeout: 5))
        XCTAssertTrue(popover.rows.allSatisfy { $0.toolWarning == "Needs npx, which Claude Desktop may not see. Edit to see how to fix it." })
        XCTAssertTrue(popover.rows.allSatisfy(\.enabled))
    }

    func testOpenedProbesOnlyTheToolsTheRowsNeedAndOnlyOnce() {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(h.tools.batches, 0)   // building the model probes nothing

        popover.opened()
        XCTAssertTrue(h.ui.pumpUntil({ state.toolStatuses[.npx] != nil }, timeout: 5))
        // Three npx connectors: one tool, one batch — not one probe per row.
        XCTAssertEqual(h.tools.probed, [.npx])
        XCTAssertEqual(h.tools.batches, 1)

        popover.opened()   // everything the rows need is cached now
        XCTAssertEqual(h.tools.batches, 1)
        XCTAssertEqual(h.tools.probed, [.npx])
    }

    func testOpenedProbesNothingWhenNoRowNeedsATool() {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        popover.opened()   // an empty catalog
        XCTAssertEqual(h.tools.batches, 0)

        XCTAssertNil(state.upsert(name: "pathed", entry: MCPEntry(config: .object(["command": .string("/usr/local/bin/node")])), renamedFrom: nil))
        XCTAssertNil(state.upsert(name: "stranger", entry: MCPEntry(config: .object(["command": .string("python")])), renamedFrom: nil))
        popover.opened()   // a full path and an unknown launcher both need no PATH lookup
        XCTAssertEqual(h.tools.batches, 0)
        XCTAssertTrue(h.tools.probed.isEmpty)
        XCTAssertTrue(popover.rows.allSatisfy { $0.toolWarning == nil })
    }

    /// SF Symbol names: platform-specific glyph identifiers, deliberately out
    /// of the shared string catalog (Windows uses Segoe Fluent Icons code
    /// points instead), so pinned here instead.
    func testGlyphNamesAreSessionLocalAndStable() {
        XCTAssertEqual(PopoverModel.retryGlyph, "exclamationmark.arrow.circlepath")
        XCTAssertEqual(PopoverModel.restartGlyph, "arrow.clockwise")
        XCTAssertEqual(PopoverModel.toolWarningGlyph, "exclamationmark.triangle.fill")
    }

    // MARK: - Collection menu titles, chip marks and locks

    func testTheMenuTitlesNameTheActiveCollection() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(PopoverModel.importTitle, "Import…")
        XCTAssertEqual(PopoverModel.manageTitle, "Manage Collections…")
        XCTAssertEqual(popover.exportTitle, "Export “Default”…")
        XCTAssertEqual(popover.addTooltipText, PopoverModel.addTooltip)

        XCTAssertNil(state.createCollection(named: "Work"))
        XCTAssertEqual(popover.exportTitle, "Export “Work”…")
    }

    func testTheChipMarksAndLocksFollowTheActiveCollection() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let document = h.dir.file("acme/data-team.json")
        try FileManager.default.createDirectory(at: document.deletingLastPathComponent(),
                                                withIntermediateDirectories: true)
        try CollectionDocumentSamples.dataTeam.serialized().write(to: document)
        XCTAssertNil(state.subscribe(documentAt: document.path, as: nil))
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }

        // A local collection: no chain, no tooltip, no locks, and additions are allowed.
        XCTAssertFalse(popover.activeCollectionIsSynced)
        XCTAssertNil(popover.sourceTooltip)
        XCTAssertFalse(popover.activeHasPendingUpdate)
        XCTAssertTrue(popover.rows.allSatisfy { !$0.isLocked })

        state.switchCollection(to: "Data team")
        XCTAssertTrue(popover.activeCollectionIsSynced)
        XCTAssertEqual(popover.sourceTooltip, "Synced from \(document.path)")
        XCTAssertEqual(popover.addTooltipText, PopoverModel.addDisabledTooltip)
        XCTAssertEqual(popover.rows.map(\.name), ["dbt", "github", "ledger", "notion"])
        XCTAssertTrue(popover.rows.allSatisfy(\.isLocked), "every row of a synced collection is the author's")

        // The dot is the active collection's news only.
        state.pendingUpdates = ["Default": CollectionDiff(added: ["jira"], removed: [], changed: [])]
        XCTAssertFalse(popover.activeHasPendingUpdate)
        state.pendingUpdates = ["Data team": CollectionDiff(added: ["jira"], removed: [], changed: [])]
        XCTAssertTrue(popover.activeHasPendingUpdate)
    }

    func testTheSourceTooltipNamesTheSidecarFileWhileTheDocumentIsUnfound() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        try CollectionsFile(collections: ["Team": CollectionsFile.Entry(kind: .synced, fileName: "team.json")])
            .save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        state.reload()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(popover.sourceTooltip, "Synced from team.json")
    }

    // MARK: - Banner actions

    func testTheLocateBannerBindsTheCollectionItNames() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        try CollectionsFile(collections: ["Team": CollectionsFile.Entry(kind: .synced, fileName: "team.json")])
            .save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        state.reload()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(popover.collectionBanner, .locate(collection: "Team", fileName: "team.json"))

        // A file that is not there is not bound, and the message is the one AppState gives.
        XCTAssertNotNil(popover.locateSource(h.dir.file("nope.json").path))
        XCTAssertNil(state.sourceBinding(of: "Team"))

        let document = h.dir.file("team.json")
        try CollectionDocumentSamples.dataTeam.serialized().write(to: document)
        XCTAssertNil(popover.locateSource(document.path))
        XCTAssertEqual(state.sourceBinding(of: "Team")?.path, document.path)

        // The file is found, so the banner has moved on and a second locate has nothing to act on.
        XCTAssertNotEqual(popover.collectionBanner, .locate(collection: "Team", fileName: "team.json"))
        let elsewhere = h.dir.file("moved.json")
        try CollectionDocumentSamples.dataTeam.serialized().write(to: elsewhere)
        XCTAssertNil(popover.locateSource(elsewhere.path))
        XCTAssertEqual(state.sourceBinding(of: "Team")?.path, document.path, "no banner, no change")
    }

    func testTheFailedPublishBannerRepointsTheCollectionItNames() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        try CollectionsFile(collections: [
            "Default": CollectionsFile.Entry(
                kind: .local, publish: CollectionsFile.PublishRecord(slug: "default", origin: "origin", intent: .none)),
        ]).save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        let first = h.dir.file("first")
        try FileManager.default.createDirectory(at: first, withIntermediateDirectories: true)
        try CollectionsLocalCache(synced: [:], published: ["Default": .init(folder: first.path, lastWrittenHash: nil)])
            .save(to: state.service.paths.collectionsCacheURL, staging: nil)
        state.reload()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }

        state.publishError = (collection: "Default", message: "the folder is read-only")
        XCTAssertEqual(popover.collectionBannerButton, PopoverModel.chooseFolderButton)

        let second = h.dir.file("second")
        try FileManager.default.createDirectory(at: second, withIntermediateDirectories: true)
        XCTAssertNil(popover.choosePublishFolder(second.path))
        XCTAssertEqual(state.collectionsCache.published["Default"]?.folder, second.path)
        XCTAssertTrue(FileManager.default.fileExists(atPath: second.appendingPathComponent("default.json").path),
                      "the document lands in the folder just chosen")
        XCTAssertNil(state.publishError)

        // With the failure gone there is no banner to act on, so the folder stays put.
        let third = h.dir.file("third")
        try FileManager.default.createDirectory(at: third, withIntermediateDirectories: true)
        XCTAssertNil(popover.choosePublishFolder(third.path))
        XCTAssertEqual(state.collectionsCache.published["Default"]?.folder, second.path)
    }

    func testTheCollectionsWindowRequestsRoundTripThroughAppState() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        try CollectionsFile(collections: ["Team": CollectionsFile.Entry(kind: .synced, fileName: "team.json")])
            .save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        state.reload()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertNil(state.takeCollectionsWindowRequest())

        popover.requestImport()
        XCTAssertEqual(state.collectionsWindowRequest, .importFile)
        XCTAssertEqual(state.takeCollectionsWindowRequest(), .importFile)
        XCTAssertNil(state.collectionsWindowRequest, "the window takes the request once")
        XCTAssertNil(state.takeCollectionsWindowRequest())

        popover.requestExport()
        XCTAssertEqual(state.takeCollectionsWindowRequest(), .exportActive)

        // The review request comes from the banner, and the locate banner is not one.
        XCTAssertFalse(popover.collectionBannerAction())
        XCTAssertNil(state.collectionsWindowRequest)

        state.pendingUpdates = ["Team": CollectionDiff(added: ["jira"], removed: [], changed: [])]
        XCTAssertTrue(popover.collectionBannerAction())
        XCTAssertEqual(state.takeCollectionsWindowRequest(), .review(collection: "Team"))
    }
}
