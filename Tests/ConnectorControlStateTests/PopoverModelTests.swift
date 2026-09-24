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
        XCTAssertEqual(popover.activeCollection, "Default")
        XCTAssertTrue(popover.isEmpty)
        XCTAssertNil(state.upsert(name: "z", entry: MCPEntry(config: AppStateHarness.remote("https://z.example/mcp")), renamedFrom: nil))
        XCTAssertEqual(popover.subtitle, "1 of 1 enabled")
        XCTAssertFalse(popover.isEmpty)
    }

    func testRowsAreSortedOrdinally() {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "Zebra", entry: MCPEntry(config: AppStateHarness.remote("https://zebra.example/mcp")), renamedFrom: nil))
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(popover.rows.map(\.name), ["Zebra", "aws-mcp", "scoutbook", "service-now"])   // uppercase first: ordinal
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
        state.remove(names: ["scoutbook"])
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

    func testTheChipMenuHasNoHousekeepingItems() {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(popover.collectionItems, [CollectionMenuItem(name: "Default", isActive: true)])

        XCTAssertNil(state.createCollection(named: "Work"))
        XCTAssertEqual(popover.collectionItems.map(\.name), ["Default", "Work"], "switching only — New, Rename and Delete live in the window")
        XCTAssertEqual(popover.collectionItems.map(\.isActive), [false, true])
        XCTAssertEqual(popover.collectionItems.map(\.isSynced), [false, false])
        XCTAssertEqual(popover.collectionItems.map(\.hasPendingUpdate), [false, false])
        XCTAssertEqual(popover.activeCollection, "Work")
        popover.switchCollection("Default")
        XCTAssertEqual(popover.activeCollection, "Default")
    }

    func testASyncedCollectionIsMarkedInTheMenu() throws {
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
        XCTAssertEqual(popover.collectionBannerButton, "Locate team.json")

        let diff = CollectionDiff(added: ["jira"], removed: ["confluence"], changed: [])
        state.pendingUpdates = ["Team": diff]
        XCTAssertEqual(popover.collectionBannerText, "Team changed at its source: adds jira; removes confluence.")
        XCTAssertEqual(popover.collectionBannerButton, "Review & Apply")

        state.publishError = CollectionPublishError(collection: "Default", message: "the folder is read-only")
        XCTAssertEqual(popover.collectionBannerText,
                       "Couldn’t publish Default to \(folder.path): the folder is read-only")
        XCTAssertEqual(popover.collectionBannerButton, "Choose Folder")

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
        XCTAssertEqual(PopoverModel.cautionGlyph, PopoverModel.toolWarningGlyph, "one glyph, two names")
    }

    // MARK: - Collection menu titles, chip marks and locks

    func testTheManageItemHasItsTitle() {
        XCTAssertEqual(PopoverModel.manageTitle, "Manage Collections")
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

        // A local collection: no chain, no tooltip, no locks.
        XCTAssertFalse(popover.activeCollectionIsSynced)
        XCTAssertNil(popover.sourceTooltip)
        XCTAssertFalse(popover.activeHasPendingUpdate)
        XCTAssertTrue(popover.rows.allSatisfy { !$0.isLocked })

        state.switchCollection(to: "Data team")
        XCTAssertTrue(popover.activeCollectionIsSynced)
        XCTAssertEqual(popover.sourceTooltip, "Synced from \(document.path)")
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

        state.publishError = CollectionPublishError(collection: "Default", message: "the folder is read-only")
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

    func testOnlyTheFailedPublishBannerOffersStopPublishing() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        try CollectionsFile(collections: [
            "Team": CollectionsFile.Entry(kind: .synced, fileName: "team.json"),
            "Default": CollectionsFile.Entry(
                kind: .local, publish: CollectionsFile.PublishRecord(slug: "default", origin: "origin", intent: .none)),
        ]).save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        let folder = h.dir.file("pub")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try CollectionsLocalCache(synced: [:], published: ["Default": .init(folder: folder.path, lastWrittenHash: nil)])
            .save(to: state.service.paths.collectionsCacheURL, staging: nil)
        state.reload()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }

        // The locate banner has one button, so there is no second one to show or to press.
        XCTAssertEqual(popover.collectionBanner, .locate(collection: "Team", fileName: "team.json"))
        XCTAssertNil(popover.collectionBannerSecondaryButton)
        popover.collectionBannerSecondaryAction()
        XCTAssertNotNil(state.collectionsFile.collections["Default"]?.publish, "nothing was stopped")

        state.publishError = CollectionPublishError(collection: "Default", message: "the folder is read-only")
        XCTAssertEqual(popover.collectionBannerSecondaryButton, CollectionsModel.stopPublishingAction)
        popover.collectionBannerSecondaryAction()
        XCTAssertNil(state.collectionsFile.collections["Default"]?.publish, "the record is gone")
        XCTAssertNil(state.collectionsCache.published["Default"], "and so is this machine's binding")
        XCTAssertNil(state.publishError, "with the record gone there is nothing left to have failed")
        XCTAssertTrue(FileManager.default.fileExists(atPath: folder.appendingPathComponent("default.json").path),
                      "the document in the folder stays: a folder this machine cannot reach is not one to delete from")
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

        // The review request comes from the banner, and the locate banner is not one.
        XCTAssertFalse(popover.collectionBannerAction())
        XCTAssertNil(state.collectionsWindowRequest)

        state.pendingUpdates = ["Team": CollectionDiff(added: ["jira"], removed: [], changed: [])]
        XCTAssertTrue(popover.collectionBannerAction())
        XCTAssertEqual(state.collectionsWindowRequest, .review(collection: "Team"))
        XCTAssertEqual(state.takeCollectionsWindowRequest(), .review(collection: "Team"))
        XCTAssertNil(state.collectionsWindowRequest, "the window takes the request once")
        XCTAssertNil(state.takeCollectionsWindowRequest())
    }
    func testAMenuRowSpellsOutWhatItsSingleImageCannotShow() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        try CollectionsFile(collections: ["Team": CollectionsFile.Entry(kind: .synced, fileName: "team.json")])
            .save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        state.reload()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }

        // No news: the row is the name, and the chain is the one image it can draw.
        let quiet = try XCTUnwrap(popover.collectionItems.first { $0.name == "Team" })
        XCTAssertEqual(PopoverModel.menuTitle(for: quiet), "Team")

        state.pendingUpdates = ["Team": CollectionDiff(added: ["jira"], removed: [], changed: [])]
        let pending = try XCTUnwrap(popover.collectionItems.first { $0.name == "Team" })
        XCTAssertEqual(PopoverModel.menuTitle(for: pending), "Team · update available")
        // The mark is the dot's spoken form, so a row reads the same whether seen or heard.
        XCTAssertEqual(PopoverModel.pendingMenuMark, " · " + PopoverModel.pendingSpokenLabel)
        XCTAssertEqual(PopoverModel.pendingSpokenLabel, "update available")
        // One owner of the words: the window's status for the same condition.
        XCTAssertEqual(PopoverModel.pendingSpokenLabel, CollectionsModel.updateAvailableStatus)

        // A quiet row is just its name.
        let local = try XCTUnwrap(popover.collectionItems.first { $0.name == "Default" })
        XCTAssertEqual(PopoverModel.menuTitle(for: local), "Default")

        // The mark follows the news alone, not the chain: the title is pure over the flag, so a
        // row carrying news is marked whatever else it is.
        let unchained = CollectionMenuItem(name: "Default", isActive: false, isSynced: false, hasPendingUpdate: true)
        XCTAssertEqual(PopoverModel.menuTitle(for: unchained), "Default · update available")
    }

    func testARowsLockSaysWhatTheWindowsLockSays() {
        let row = ConnectorRow(name: "aws-mcp", enabled: true, toolWarning: nil, isLocked: true)
        XCTAssertEqual(row.lockTooltip, CollectionsModel.lockedGlyphTooltip)
        XCTAssertEqual(row.lockTooltip, "Read-only: synced from the collection's author")
    }
    func testEverySyncedMenuRowNamesItsOwnSource() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.createCollection(named: "Ops"))
        state.switchCollection(to: "Default")
        try CollectionsFile(collections: [
            "Team": CollectionsFile.Entry(kind: .synced, fileName: "team.json"),
            "Ops": CollectionsFile.Entry(kind: .synced, fileName: "ops.json"),
        ]).save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        try CollectionsLocalCache(synced: ["Team": .init(path: "/Acme/mcp/team.json", lastHash: nil, excluded: [:])],
                                  published: [:])
            .save(to: state.service.paths.collectionsCacheURL, staging: nil)
        state.reload()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }

        func item(_ name: String) throws -> CollectionMenuItem {
            try XCTUnwrap(popover.collectionItems.first { $0.name == name })
        }
        // Neither synced row is the active one, and each still names its own document: the bound
        // path where there is one, the sidecar's file name where the file is still to be found.
        XCTAssertEqual(try item("Team").source, "/Acme/mcp/team.json")
        XCTAssertEqual(PopoverModel.menuTooltip(for: try item("Team")), "Synced from /Acme/mcp/team.json")
        XCTAssertEqual(try item("Ops").source, "ops.json")
        XCTAssertEqual(PopoverModel.menuTooltip(for: try item("Ops")), "Synced from ops.json")
        XCTAssertNil(try item("Default").source)
        XCTAssertNil(PopoverModel.menuTooltip(for: try item("Default")))

        // One rule for every surface: the chip, the menu and the window's sidebar all agree.
        XCTAssertEqual(try item("Team").source, state.sourceLocation(of: "Team"))
        state.switchCollection(to: "Team")
        XCTAssertEqual(popover.sourceTooltip, PopoverModel.menuTooltip(for: try item("Team")))
        let window = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { window.dispose() }
        let sidebar = try XCTUnwrap(window.items.first { $0.name == "Team" })
        XCTAssertEqual(CollectionsModel.syncedGlyphTooltip(sidebar), PopoverModel.menuTooltip(for: try item("Team")))
    }
    func testAnEmptySidecarNameAsksForNothingAnywhere() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        // Only a hand-edited or malformed sidecar says this; nothing here writes an empty name.
        try CollectionsFile(collections: ["Team": CollectionsFile.Entry(kind: .synced, fileName: "")])
            .save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        state.reload()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }

        // The banner, the chip and the menu agree there is nothing to name: no "Locate " over a
        // blank while the tooltips stay silent.
        XCTAssertNil(popover.collectionBanner)
        XCTAssertNil(state.sourceLocation(of: "Team"))
        XCTAssertNil(popover.sourceTooltip)
        let team = try XCTUnwrap(popover.collectionItems.first { $0.name == "Team" })
        XCTAssertNil(PopoverModel.menuTooltip(for: team))
        XCTAssertTrue(state.isLocated("Team"), "nothing to find, which is what located already means")
    }
    // MARK: - A publish blocked for review

    func testABlockedPublishOpensThePublishSheetInsteadOfAFolder() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        try CollectionsFile(collections: [
            "Default": CollectionsFile.Entry(
                kind: .local, publish: CollectionsFile.PublishRecord(slug: "default", origin: "origin", intent: .none)),
        ]).save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        let folder = h.dir.file("pub")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try CollectionsLocalCache(synced: [:], published: ["Default": .init(folder: folder.path, lastWrittenHash: nil)])
            .save(to: state.service.paths.collectionsCacheURL, staging: nil)
        state.reload()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }

        // A failed write keeps today's banner exactly: another folder is an answer to it.
        state.publishError = CollectionPublishError(collection: "Default", message: "the folder is read-only")
        XCTAssertEqual(state.publishError?.kind, .writeFailed, "the default every existing caller meant")
        XCTAssertEqual(popover.collectionBanner, .publishFailed(collection: "Default", message: "the folder is read-only"))
        XCTAssertEqual(popover.collectionBannerButton, PopoverModel.chooseFolderButton)

        // Blocked for review: the message is the whole banner, the button opens the Publish sheet,
        // and nothing offers a folder or a second button.
        let moved = AppState.pathMarkMovedError("ledger")
        state.publishError = CollectionPublishError(collection: "Default", message: moved, kind: .blockedForReview)
        XCTAssertEqual(popover.collectionBanner, .publishBlocked(collection: "Default", message: moved))
        XCTAssertEqual(popover.collectionBannerText, moved)
        XCTAssertEqual(popover.collectionBannerButton, CollectionsModel.publishSettingsButton)
        XCTAssertNil(popover.collectionBannerSecondaryButton)

        // The button asks the Collections window for the Publish sheet, and true tells the view to
        // open that window and do nothing else.
        XCTAssertTrue(popover.collectionBannerAction())
        XCTAssertEqual(state.takeCollectionsWindowRequest(), .publish(collection: "Default"))

        // A folder chosen anyway, or Stop Publishing reached some other way, is refused here: the
        // banner is not asking for either, and re-binding would abandon the old folder's document.
        let elsewhere = h.dir.file("elsewhere")
        try FileManager.default.createDirectory(at: elsewhere, withIntermediateDirectories: true)
        XCTAssertEqual(popover.choosePublishFolder(elsewhere.path), moved, "refused, and it says why")
        XCTAssertEqual(state.collectionsCache.published["Default"]?.folder, folder.path)
        popover.collectionBannerSecondaryAction()
        XCTAssertTrue(state.isPublished("Default"))
    }

    func testAMovedMarkIsClassifiedAsBlockedAndEverythingElseAsAFailedWrite() {
        XCTAssertEqual(AppState.publishErrorKind(of: PublishIntentError.pathMarkMoved(connector: "ledger")),
                       .blockedForReview)
        XCTAssertEqual(AppState.publishErrorKind(of: CocoaError(.fileWriteNoPermission)), .writeFailed)
    }

    func testRenamingACollectionKeepsItsBlockedPublishBlocked() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        state.publishError = CollectionPublishError(collection: "Team", message: "moved", kind: .blockedForReview)
        XCTAssertNil(state.renameCollection("Team", to: "Crew"))
        XCTAssertEqual(state.publishError, CollectionPublishError(collection: "Crew", message: "moved", kind: .blockedForReview))
    }
    /// Adapted from the coll-21 review's probe P6. A real moved mark, not a hand-set error: the
    /// folder change that follows must be refused, because the intent it would carry is the one
    /// that blocked, so the new folder would be bound, receive nothing, and leave the old folder's
    /// document behind.
    func testAFolderChangeIsRefusedWhileAMovedMarkBlocksThePublish() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let collection = state.activeCollection
        let marked = "/Users/d/ledger/dist/index.js"
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(marked)]),
        ])), renamedFrom: nil))
        let a = h.dir.file("pubA")
        try FileManager.default.createDirectory(at: a, withIntermediateDirectories: true)
        let b = h.dir.file("pubB")
        try FileManager.default.createDirectory(at: b, withIntermediateDirectories: true)
        XCTAssertNil(state.startPublishing(collection, to: a.path, intent: PublishIntent(
            shareValues: [:],
            pathMarks: ["ledger": [JSONPointer(["args", "0"]): .init(name: "server_path", hint: nil, value: marked)]],
            hints: [:])))
        let document = a.appendingPathComponent(Slug.make(collection) + "." + CollectionDocument.fileExtension)
        let published = try Data(contentsOf: document)

        // The marked path moves: publishing stops before writing, for the author to review.
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string("/Users/d/v2.js")]),
        ])), renamedFrom: "ledger"))
        let blocked = try XCTUnwrap(state.publishError)
        XCTAssertEqual(blocked.kind, .blockedForReview)

        // Straight at AppState, as the probe did, and through the popover, as a view would.
        XCTAssertEqual(state.changePublishFolder(collection, to: b.path), blocked.message)
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(popover.choosePublishFolder(b.path), blocked.message)

        // Nothing moved: the binding is where it was, nothing landed in the new folder, and the old
        // folder still holds the document it had.
        XCTAssertEqual(state.collectionsCache.published[collection]?.folder, a.path)
        XCTAssertFalse(FileManager.default.fileExists(
            atPath: b.appendingPathComponent(Slug.make(collection) + "." + CollectionDocument.fileExtension).path))
        XCTAssertEqual(try Data(contentsOf: document), published)
        XCTAssertEqual(state.publishError, blocked, "refusing changes nothing, the error included")
    }
}
