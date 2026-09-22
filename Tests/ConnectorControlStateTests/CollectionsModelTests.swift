import Combine
import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/CollectionsModelTests.cs. The two panes of the
/// Collections window: the collections as items, the selected one's connectors as rows, the
/// toolbar's enablement, and the actions that go through the dialog seam.
@MainActor
final class CollectionsModelTests: XCTestCase {
    private func synced(fileName: String) -> CollectionsFile.Entry {
        CollectionsFile.Entry(kind: .synced, fileName: fileName)
    }

    private func published(slug: String) -> CollectionsFile.Entry {
        CollectionsFile.Entry(kind: .local, publish: CollectionsFile.PublishRecord(slug: slug, origin: "origin", intent: .none))
    }

    private func bound(_ path: String?) -> CollectionsLocalCache.SyncedBinding {
        CollectionsLocalCache.SyncedBinding(path: path, lastHash: nil, excluded: [:])
    }

    /// Writes both collection files where the app reads them, then reloads so the state picks
    /// them up — the shape a subscribe or a publish would leave behind.
    private func seed(_ h: AppStateHarness, _ state: AppState,
                      file: CollectionsFile, cache: CollectionsLocalCache? = nil) throws {
        try file.save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        try (cache ?? CollectionsLocalCache(synced: [:], published: [:]))
            .save(to: state.service.paths.collectionsCacheURL, staging: nil)
        state.reload()
    }

    private func local(_ command: String, _ args: [String] = []) -> MCPEntry {
        MCPEntry(config: .object(["command": .string(command), "args": .array(args.map(JSONValue.string))]))
    }

    // MARK: - Items

    func testItemsMirrorTheStoreAndMarkSyncedPublishedAndPending() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        let file = CollectionsFile(collections: ["Shared": published(slug: "shared"), "Team": synced(fileName: "team.json")])
        let cache = CollectionsLocalCache(synced: ["Team": bound("/shared/team.json")],
                                          published: ["Shared": .init(folder: "/tmp/share", lastWrittenHash: nil)])
        try seed(h, state, file: file, cache: cache)

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        XCTAssertEqual(model.items.map(\.name), ["Default", "Shared", "Team"])   // the chip menu's order
        XCTAssertEqual(model.items.map(\.id), ["Default", "Shared", "Team"])
        XCTAssertEqual(model.items.map(\.kind), [.local, .local, .synced])
        XCTAssertEqual(model.items.map(\.isActive), [true, false, false])
        XCTAssertEqual(model.items.map(\.isPublished), [false, true, false])
        XCTAssertEqual(model.items.map(\.hasPendingUpdate), [false, false, false])
        XCTAssertEqual(model.items.map(\.isLocated), [true, true, true], "a local collection has no file to find")

        // The republish is what repaints the view, and it is the one line a passthrough cannot prove.
        var repaints = 0
        let sink = model.objectWillChange.sink { _ in repaints += 1 }
        defer { sink.cancel() }
        state.pendingUpdates = ["Team": CollectionDiff(added: ["jira"], removed: [], changed: [])]
        XCTAssertEqual(model.items.map(\.hasPendingUpdate), [false, false, true])
        XCTAssertGreaterThan(repaints, 0)

        // The binding gone, the sidecar still names the file: the item says it is not located.
        try seed(h, state, file: file, cache: CollectionsLocalCache(synced: [:], published: cache.published))
        XCTAssertEqual(model.items.map(\.isLocated), [true, true, false])
        XCTAssertFalse(state.isLocated("Team"))
        XCTAssertTrue(state.isLocated("Default"))

        // dispose() cuts the republish: the items still read through, nothing repaints.
        model.dispose()
        let before = repaints
        state.pendingUpdates = [:]
        XCTAssertEqual(model.items.map(\.hasPendingUpdate), [false, false, false])
        XCTAssertEqual(repaints, before)
    }

    // MARK: - Rows

    func testRowsForASyncedCollectionAreLockedAndUncheckable() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.upsert(name: "github", entry: MCPEntry(config: AppStateHarness.remote("https://github.example/mcp")),
                                  renamedFrom: nil, in: "Team"))
        XCTAssertNil(state.upsert(name: "Ledger", entry: local("/usr/local/bin/node", ["index.js"]), renamedFrom: nil, in: "Team"))
        XCTAssertNil(state.upsert(name: "jira", entry: MCPEntry(config: .object([
            "command": .string("npx"),
            "env": .object(["JIRA_TOKEN": .string(Placeholder.marker("JIRA_TOKEN"))]),
        ])), renamedFrom: nil, in: "Team"))
        XCTAssertNil(state.upsert(name: "notes", entry: local("uvx"), renamedFrom: nil, in: "Default"))
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]),
                 cache: CollectionsLocalCache(synced: ["Team": bound("/shared/team.json")], published: [:]))

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Team"
        // Uppercase first: ordinal, the order the popover lists the same connectors in.
        XCTAssertEqual(model.rows.map(\.name), ["Ledger", "github", "jira"])
        XCTAssertEqual(model.rows.map(\.id), ["Ledger", "github", "jira"])
        XCTAssertEqual(model.rows.map(\.typeText), ["local · node", "remote", "local · npx"])
        XCTAssertTrue(model.rows.allSatisfy(\.isLocked), "every row of a synced collection carries the lock")
        XCTAssertEqual(model.rows.map(\.caution), [nil, nil, AppState.needsValueCaution("JIRA_TOKEN")])
        XCTAssertEqual(model.rows.map(\.enabled), [true, true, true])

        // Nothing in a synced collection can be exported, so nothing in one can be ticked.
        model.setChecked("github", true)
        XCTAssertTrue(model.rows.allSatisfy { !$0.checked })
        XCTAssertEqual(model.checkedNames, [])
        XCTAssertEqual(model.exportIntentForChecked(), [])
        XCTAssertFalse(model.canExport)

        // The same rows in a local collection do tick, and the ticks belong to that collection.
        model.selected = "Default"
        XCTAssertEqual(model.rows.map(\.name), ["notes"])
        XCTAssertEqual(model.rows.map(\.typeText), ["local · uvx"])
        XCTAssertTrue(model.rows.allSatisfy { !$0.isLocked })
        model.setChecked("notes", true)
        XCTAssertEqual(model.rows.map(\.checked), [true])
        XCTAssertEqual(model.checkedNames, ["notes"])
        XCTAssertEqual(model.exportIntentForChecked(), ["notes"])
        XCTAssertTrue(model.canExport)
        model.setChecked("notes", false)
        XCTAssertFalse(model.canExport)

        // The pencil opens the row in the collection the window is showing, not the active one.
        model.selected = "Team"
        let target = model.editTarget(for: "jira")
        XCTAssertEqual(target.collection, "Team")
        XCTAssertEqual(target.name, "jira")
        XCTAssertFalse(target.isNew)
        XCTAssertEqual(target.entry.config, state.store.collections["Team"]?.mcps["jira"]?.config)

        // One connector list must not appear in two orders: the window lists the active
        // collection's rows exactly as the popover does.
        state.switchCollection(to: "Team")
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(model.rows.map(\.name), popover.rows.map(\.name))
    }

    // MARK: - Toolbar

    func testToolbarEnablementFollowsTheSelection() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        let file = CollectionsFile(collections: ["Shared": published(slug: "shared"), "Team": synced(fileName: "team.json")])
        let located = CollectionsLocalCache(synced: ["Team": bound("/shared/team.json")],
                                            published: ["Shared": .init(folder: "/tmp/share", lastWrittenHash: nil)])
        try seed(h, state, file: file, cache: located)

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        XCTAssertEqual(model.selected, "Default", "the selection starts on the active collection")
        XCTAssertFalse(model.canExport, "nothing is ticked yet")
        model.setChecked("aws-mcp", true)
        XCTAssertTrue(model.canExport)
        XCTAssertTrue(model.canPublish)
        XCTAssertFalse(model.canRefresh)
        XCTAssertFalse(model.canMakeLocalCopy)
        XCTAssertFalse(model.canStopSyncing)
        XCTAssertFalse(model.canStopPublishing)
        XCTAssertTrue(model.canDelete)

        model.selected = "Shared"
        XCTAssertFalse(model.canExport, "the ticks belonged to the collection that was showing")
        // Published from here, and still offered: reopening the sheet shows the record and
        // pressing Publish again updates what is shared, so both links stand side by side.
        XCTAssertTrue(model.canPublish, "the only way to change what a published collection shares")
        XCTAssertTrue(model.canStopPublishing)
        XCTAssertFalse(model.canRefresh)
        XCTAssertTrue(model.canDelete)

        model.selected = "Team"
        XCTAssertFalse(model.canExport)
        XCTAssertFalse(model.canPublish, "a synced collection has an author elsewhere")
        XCTAssertFalse(model.canStopPublishing)
        XCTAssertTrue(model.canRefresh)
        XCTAssertTrue(model.canMakeLocalCopy)
        XCTAssertTrue(model.canStopSyncing)
        XCTAssertTrue(model.canDelete, "a synced collection goes without taking the last local one with it")

        // Nothing to refresh until the file is found on this machine.
        try seed(h, state, file: file, cache: CollectionsLocalCache(synced: [:], published: located.published))
        XCTAssertFalse(model.canRefresh)
        XCTAssertTrue(model.canMakeLocalCopy)

        // With the second local collection gone, the last one cannot be deleted.
        XCTAssertNil(state.deleteCollection(named: "Shared"))
        model.selected = "Default"
        XCTAssertFalse(model.canDelete)
        model.selected = "Team"
        XCTAssertTrue(model.canDelete)

        // Nor can the last collection of any kind: the store always has an active one, so a lone
        // synced collection is no more deletable than a lone local one.
        XCTAssertNil(state.deleteCollection(named: "Team"))
        try seed(h, state, file: CollectionsFile(collections: ["Default": synced(fileName: "default.json")]),
                 cache: CollectionsLocalCache(synced: ["Default": bound("/shared/default.json")], published: [:]))
        XCTAssertEqual(state.collectionNames, ["Default"])
        XCTAssertTrue(state.isSynced("Default"))
        XCTAssertFalse(model.canDelete)
    }

    // MARK: - Create, rename, delete

    func testCreateRenameDeleteGoThroughTheDialogs() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Work"))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        XCTAssertEqual(model.selected, "Work")

        // A cancelled prompt does nothing at all.
        h.dialogs.nextPromptAnswer = nil
        model.create()
        XCTAssertEqual(h.dialogs.prompts.last, FakeDialogs.PromptCall(title: AppState.newCollectionTitle, initial: ""))
        XCTAssertEqual(state.collectionNames, ["Default", "Work"])
        XCTAssertNil(model.lastError)

        h.dialogs.nextPromptAnswer = "  Team  "
        model.create()
        XCTAssertEqual(state.collectionNames, ["Default", "Team", "Work"])
        XCTAssertEqual(model.selected, "Team", "the window shows what it just made")
        XCTAssertNil(model.lastError)

        // A name the store refuses comes back as the model's error.
        h.dialogs.nextPromptAnswer = "Work"
        model.create()
        XCTAssertNotNil(model.lastError)
        XCTAssertEqual(state.collectionNames, ["Default", "Team", "Work"])

        h.dialogs.nextPromptAnswer = "Team B"
        model.rename()
        XCTAssertEqual(h.dialogs.prompts.last, FakeDialogs.PromptCall(title: AppState.renameCollectionTitle, initial: "Team"))
        XCTAssertEqual(state.collectionNames, ["Default", "Team B", "Work"])
        XCTAssertEqual(model.selected, "Team B", "the selection follows the name it just gave")
        XCTAssertNil(model.lastError, "a successful action clears the last one's error")

        // Declined: the collection stays.
        h.dialogs.nextConfirm = false
        model.delete()
        let asked = try XCTUnwrap(h.dialogs.confirms.last)
        XCTAssertEqual(asked.message, AppState.deleteCollectionMessage("Team B"))
        XCTAssertEqual(asked.primary, AppState.deleteButton)
        XCTAssertTrue(asked.destructive)
        XCTAssertEqual(state.collectionNames, ["Default", "Team B", "Work"])

        h.dialogs.nextConfirm = true
        model.delete()
        XCTAssertEqual(state.collectionNames, ["Default", "Work"])
        XCTAssertEqual(h.dialogs.confirms.count, 2, "an unpublished collection is asked about once")
        XCTAssertEqual(model.selected, state.activeCollection)
    }

    func testDeletingAPublishedCollectionAsksAboutTheFile() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        let folder = h.dir.file("share")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.createCollection(named: "Consulting"))
        XCTAssertNil(state.startPublishing("Shared", to: folder.path, intent: .none))
        XCTAssertNil(state.startPublishing("Consulting", to: folder.path, intent: .none))
        let sharedFile = folder.appendingPathComponent("shared.json")
        let consultingFile = folder.appendingPathComponent("consulting.json")
        XCTAssertTrue(FileManager.default.fileExists(atPath: sharedFile.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: consultingFile.path))

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Shared"
        h.dialogs.confirmAnswers = [true, false]   // delete the collection, keep the document
        model.delete()
        XCTAssertEqual(h.dialogs.confirms.map(\.message),
                       [AppState.deleteCollectionMessage("Shared"),
                        CollectionsModel.deletePublishedFileQuestion("shared.json")])
        let fileQuestion = try XCTUnwrap(h.dialogs.confirms.last)
        XCTAssertEqual(fileQuestion.primary, CollectionsModel.removeFileButton)
        XCTAssertEqual(fileQuestion.cancel, CollectionsModel.keepFileButton)
        XCTAssertFalse(fileQuestion.destructive)
        XCTAssertFalse(state.collectionNames.contains("Shared"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: sharedFile.path),
                      "Keep leaves the copy the team reads where it is")

        // The same question on its own, answered the other way.
        model.selected = "Consulting"
        h.dialogs.confirmAnswers = [true]
        model.stopPublishing()
        XCTAssertEqual(h.dialogs.confirms.last?.message, CollectionsModel.deletePublishedFileQuestion("consulting.json"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: consultingFile.path))
        XCTAssertFalse(state.isPublished("Consulting"))
        XCTAssertTrue(state.collectionNames.contains("Consulting"), "Stop Publishing keeps the collection")
        XCTAssertFalse(model.canStopPublishing)
    }

    func testAFailedPublishIsStoppedWithoutAskingAboutTheFile() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        let folder = h.dir.file("share")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.startPublishing("Shared", to: folder.path, intent: .none))
        let file = folder.appendingPathComponent("shared.json")
        XCTAssertTrue(FileManager.default.fileExists(atPath: file.path))

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Shared"
        // The folder that refused the write would refuse the delete, so Remove is not offered.
        state.publishError = CollectionPublishError(collection: "Shared", message: "the folder is read-only")
        model.stopPublishing()
        XCTAssertTrue(h.dialogs.confirms.isEmpty, "nothing to ask when Remove could not be honoured")
        XCTAssertFalse(state.isPublished("Shared"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: file.path), "the document stays where it is")
        XCTAssertNil(state.publishError)

        // Another collection's failure is not this one's, so the question comes back.
        XCTAssertNil(state.createCollection(named: "Consulting"))
        XCTAssertNil(state.startPublishing("Consulting", to: folder.path, intent: .none))
        model.selected = "Consulting"
        state.publishError = CollectionPublishError(collection: "Shared", message: "the folder is read-only")
        h.dialogs.confirmAnswers = [false]
        model.stopPublishing()
        XCTAssertEqual(h.dialogs.confirms.map(\.message),
                       [CollectionsModel.deletePublishedFileQuestion("consulting.json")])
    }

    // MARK: - Toggles

    func testSetEnabledInAnInactiveCollectionLeavesClaudesConfigAlone() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Work"))
        state.switchCollection(to: "Default")
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }

        model.selected = "Work"
        model.setEnabled("aws-mcp", false)
        XCTAssertEqual(state.store.collections["Work"]?.mcps["aws-mcp"]?.enabled, false)
        XCTAssertEqual(try h.storeOnDisk().collections["Work"]?.mcps["aws-mcp"]?.enabled, false)
        XCTAssertEqual(state.store.collections["Default"]?.mcps["aws-mcp"]?.enabled, true)
        XCTAssertNotNil(try h.claudeServers()["aws-mcp"], "Claude runs the active collection, which did not change")
        XCTAssertEqual(model.rows.first { $0.name == "aws-mcp" }?.enabled, false)

        // The same toggle in the active collection does reach Claude.
        model.selected = "Default"
        model.setEnabled("aws-mcp", false)
        XCTAssertNil(try h.claudeServers()["aws-mcp"])
    }

    // MARK: - Detail line

    func testDetailLineFollowsTheCollectionState() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        let file = CollectionsFile(collections: ["Shared": published(slug: "shared"), "Team": synced(fileName: "team.json")])
        let located = CollectionsLocalCache(synced: ["Team": bound("/shared/team.json")],
                                            published: ["Shared": .init(folder: "/Acme/mcp", lastWrittenHash: nil)])
        try seed(h, state, file: file, cache: located)

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        XCTAssertEqual(model.detailLine, CollectionsModel.localDetail(3) + CollectionsModel.activeSuffix)

        model.selected = "Shared"
        XCTAssertEqual(model.detailLine,
                       CollectionsModel.localDetail(3) + " · " + CollectionsModel.publishedDetail("/Acme/mcp"))

        model.selected = "Team"
        XCTAssertEqual(model.detailLine,
                       CollectionsModel.syncedDetail("/shared/team.json", CollectionsModel.upToDateStatus))
        state.pendingUpdates = ["Team": CollectionDiff(added: ["jira"], removed: [], changed: [])]
        XCTAssertEqual(model.detailLine,
                       CollectionsModel.syncedDetail("/shared/team.json", CollectionsModel.updateAvailableStatus))
        state.sourceErrors = ["Team": "team.json couldn’t be read"]
        XCTAssertEqual(model.detailLine,
                       CollectionsModel.syncedDetail("/shared/team.json", "team.json couldn’t be read"),
                       "what went wrong outranks what is waiting")

        // Not located: there is nothing to say about the file except that it is missing.
        try seed(h, state, file: file, cache: CollectionsLocalCache(synced: [:], published: located.published))
        XCTAssertEqual(model.detailLine, CollectionsModel.unlocatedDetail)
        XCTAssertFalse(model.canRefresh)

        // A synced entry that records no file name either — a hand-edited or foreign collections
        // file. Nothing asks to be located, but there is still no document to name or to read.
        try seed(h, state, file: CollectionsFile(collections: [
            "Shared": published(slug: "shared"), "Team": CollectionsFile.Entry(kind: .synced),
        ]), cache: CollectionsLocalCache(synced: [:], published: located.published))
        XCTAssertTrue(state.isLocated("Team"), "nothing is waiting to be pointed at")
        XCTAssertEqual(model.detailLine, CollectionsModel.unlocatedDetail)
        XCTAssertFalse(model.canRefresh, "Refresh would read a document nobody can point at")
    }

    // MARK: - Selection

    func testSelectionFallsBackToTheActiveCollectionWhenItsCollectionDisappears() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Work"))
        XCTAssertNil(state.createCollection(named: "Spare"))
        state.switchCollection(to: "Default")
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }

        model.selected = "Work"
        XCTAssertEqual(model.selected, "Work")
        model.setChecked("aws-mcp", true)
        XCTAssertEqual(model.checkedNames, ["aws-mcp"])

        XCTAssertNil(state.deleteCollection(named: "Work"))
        XCTAssertEqual(model.selected, "Default")
        XCTAssertEqual(model.selected, state.activeCollection)
        XCTAssertEqual(model.checkedNames, [], "the ticks belonged to the collection that is gone")

        // A rename anywhere else is the same disappearance: the name selected is no longer a collection.
        model.selected = "Spare"
        XCTAssertNil(state.renameCollection("Spare", to: "Spare Parts"))
        XCTAssertEqual(model.selected, state.activeCollection)

        // Switching the active collection from the window goes through AppState.
        model.switchTo("Spare Parts")
        XCTAssertEqual(state.activeCollection, "Spare Parts")
        XCTAssertEqual(model.items.first { $0.isActive }?.name, "Spare Parts")
    }

    // MARK: - Banner strip

    /// Default, active and published into a real folder; Team, synced with its file still to be
    /// found. The two banners the window can show therefore belong to different collections.
    private func twoBanners(_ h: AppStateHarness, _ state: AppState, publishingInto folder: URL) throws {
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try seed(h, state,
                 file: CollectionsFile(collections: ["Team": synced(fileName: "team.json"),
                                                     "Default": published(slug: "default")]),
                 cache: CollectionsLocalCache(
                     synced: [:], published: ["Default": .init(folder: folder.path, lastWrittenHash: nil)]))
    }

    func testTheBannerStripSpeaksOnlyForTheSelectedCollection() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = h.dir.file("pub")
        try twoBanners(h, state, publishingInto: folder)
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }

        // Team's file is missing, but the window is showing Default: the strip says nothing.
        XCTAssertEqual(state.collectionBanner, .locate(collection: "Team", fileName: "team.json"))
        XCTAssertNil(model.bannerText)
        XCTAssertNil(model.bannerButton)
        XCTAssertFalse(model.bannerAction())

        model.selected = "Team"
        XCTAssertEqual(model.bannerText, AppState.collectionLocateBanner("Team"))
        XCTAssertEqual(model.bannerButton, PopoverModel.locateButton("team.json"))
        XCTAssertFalse(model.bannerAction(), "the view owes a file picker")

        // An update waiting is the one banner the strip can act on by itself.
        let diff = CollectionDiff(added: ["jira"], removed: [], changed: [])
        state.pendingUpdates = ["Team": diff]
        XCTAssertEqual(model.bannerText, AppState.collectionUpdateBanner("Team", diff.summary()))
        XCTAssertEqual(model.bannerButton, PopoverModel.reviewAndApplyButton)
        XCTAssertTrue(model.bannerAction())

        // A failed publish belongs to Default, so Team's strip goes quiet again.
        var repaints = 0
        let sink = model.objectWillChange.sink { _ in repaints += 1 }
        defer { sink.cancel() }
        state.publishError = CollectionPublishError(collection: "Default", message: "the folder is read-only")
        XCTAssertGreaterThan(repaints, 0, "a failed publish repaints the window")
        XCTAssertNil(model.bannerText)
        model.selected = "Default"
        XCTAssertEqual(model.bannerText,
                       AppState.collectionPublishFailedBanner("Default", folder.path, "the folder is read-only"))
        XCTAssertEqual(model.bannerButton, PopoverModel.chooseFolderButton)
        XCTAssertFalse(model.bannerAction(), "the view owes a folder picker")
    }

    func testTheBannerStripLocatesAndRepointsTheSelectedCollection() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let first = h.dir.file("first")
        try twoBanners(h, state, publishingInto: first)
        let second = h.dir.file("second")
        try FileManager.default.createDirectory(at: second, withIntermediateDirectories: true)
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }

        // The window is showing Default, which the locate banner is not about.
        let document = h.dir.file("team.json")
        try CollectionDocumentSamples.dataTeam.serialized().write(to: document)
        XCTAssertNil(model.locateSource(document.path))
        XCTAssertNil(state.sourceBinding(of: "Team"))

        model.selected = "Team"
        XCTAssertNil(model.locateSource(document.path))
        XCTAssertEqual(state.sourceBinding(of: "Team")?.path, document.path)

        // The document found, the banner has moved on, so the publish forwarding stays out of it.
        XCTAssertNil(model.choosePublishFolder(second.path))
        XCTAssertEqual(state.collectionsCache.published["Default"]?.folder, first.path)

        // A failed publish belongs to Default, and only Default's window may answer it.
        state.publishError = CollectionPublishError(collection: "Default", message: "the folder is read-only")
        XCTAssertNil(model.choosePublishFolder(second.path), "Team is showing, not Default")
        XCTAssertEqual(state.collectionsCache.published["Default"]?.folder, first.path)

        model.selected = "Default"
        XCTAssertNil(model.choosePublishFolder(second.path))
        XCTAssertEqual(state.collectionsCache.published["Default"]?.folder, second.path)
        XCTAssertTrue(FileManager.default.fileExists(atPath: second.appendingPathComponent("default.json").path),
                      "the document lands in the folder just chosen")
    }
    func testTheWindowsGlyphsAndActionsCarryTheirOwnWords() {
        XCTAssertEqual(CollectionsModel.editTooltip, "Edit")
        XCTAssertEqual(CollectionsModel.makeActiveAction, "Make Active")
        XCTAssertEqual(CollectionsModel.lockedGlyphTooltip, "Read-only: synced from the collection's author")
    }

    func testTheSidebarChainNamesTheDocumentThisMachineReads() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]),
                 cache: CollectionsLocalCache(synced: ["Team": bound("/Acme/mcp/team.json")], published: [:]))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }

        let team = try XCTUnwrap(model.items.first { $0.name == "Team" })
        XCTAssertEqual(team.source, "/Acme/mcp/team.json")
        // One sentence about one fact: the chain says the same here as on the popover's chip.
        XCTAssertEqual(CollectionsModel.syncedGlyphTooltip(team), "Synced from /Acme/mcp/team.json")
        XCTAssertEqual(CollectionsModel.syncedGlyphTooltip(team),
                       PopoverModel.sourceTooltipFormat("/Acme/mcp/team.json"))

        // A local collection has no source, so no chain and nothing to say about one.
        let local = try XCTUnwrap(model.items.first { $0.name == "Default" })
        XCTAssertNil(local.source)
        XCTAssertNil(CollectionsModel.syncedGlyphTooltip(local))

        // Synced but never found: the sidecar's file name is what the chain can still name, the
        // same fallback the popover's chip takes, so the two never disagree about one collection.
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]))
        let unlocated = try XCTUnwrap(model.items.first { $0.name == "Team" })
        XCTAssertFalse(unlocated.isLocated)
        XCTAssertEqual(unlocated.source, "team.json")
        XCTAssertEqual(CollectionsModel.syncedGlyphTooltip(unlocated), "Synced from team.json")
        state.switchCollection(to: "Team")
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(CollectionsModel.syncedGlyphTooltip(unlocated), popover.sourceTooltip)
    }
    func testChoosingTheCollectionAlreadyShowingChangesNothing() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Work"))
        state.switchCollection(to: "Default")
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.setChecked("aws-mcp", true)
        var repaints = 0
        let sink = model.objectWillChange.sink { _ in repaints += 1 }
        defer { sink.cancel() }

        // The active collection is what is showing, so naming it again is the same selection —
        // as is naming a collection that does not exist, which falls back to the same one.
        model.selected = "Default"
        model.selected = nil
        model.selected = "No such collection"
        XCTAssertEqual(repaints, 0, "a view writing its selection back must not feed itself")
        XCTAssertEqual(model.checkedNames, ["aws-mcp"], "and the ticks made in it survive")

        // Not remembered either: the window still follows the active collection.
        state.switchCollection(to: "Work")
        XCTAssertEqual(model.selected, "Work")

        // A real change still announces itself.
        model.selected = "Default"
        XCTAssertGreaterThan(repaints, 0)
        XCTAssertEqual(model.selected, "Default")
    }
    /// The Mac's own path: SwiftUI's `List` never writes its selection back, so nothing but the
    /// store change itself can let go of the vanished name. The test below adds a write-back and so
    /// exercises the other trigger; this one would still pass without that trigger and fails only
    /// without the store-change one.
    func testASelectionRenamedAwayIsForgottenWithoutAnyWriteBack() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Spare"))
        state.switchCollection(to: "Default")
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Spare"

        XCTAssertNil(state.renameCollection("Spare", to: "Spare Parts"))
        XCTAssertEqual(model.selected, "Default")
        XCTAssertNil(state.renameCollection("Spare Parts", to: "Spare"))
        XCTAssertEqual(model.selected, "Default", "a returning name must not pull the window to it")
    }

    func testASelectionRenamedAwayDoesNotPullTheWindowBackWhenItReturns() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Spare"))
        state.switchCollection(to: "Default")
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Spare"
        XCTAssertEqual(model.selected, "Spare")

        // Renamed away elsewhere: the window falls back to the active collection.
        XCTAssertNil(state.renameCollection("Spare", to: "Spare Parts"))
        XCTAssertEqual(model.selected, "Default")
        // A view writing its selection back is harmless, and changes nothing either.
        model.selected = "Default"

        // Renamed back: the name resolves again, and the window stays where the user left it.
        XCTAssertNil(state.renameCollection("Spare Parts", to: "Spare"))
        XCTAssertEqual(model.selected, "Default", "a returning name must not pull the window to it")
    }
    func testTheWindowsStripOpensPublishForABlockedPublishAndStillAsksAboutTheFile() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        let folder = h.dir.file("share")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.startPublishing("Shared", to: folder.path, intent: .none))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Shared"

        let moved = AppState.pathMarkMovedError("ledger")
        state.publishError = CollectionPublishError(collection: "Shared", message: moved, kind: .blockedForReview)
        XCTAssertEqual(model.bannerText, moved)
        XCTAssertEqual(model.bannerButton, CollectionsModel.publishButton)
        // False: true would put the Review sheet up. The view shows Publish for this kind.
        XCTAssertFalse(model.bannerAction())
        XCTAssertEqual(model.choosePublishFolder(h.dir.file("elsewhere").path), moved,
                       "a folder is no answer to this, and the refusal says why")
        XCTAssertEqual(state.collectionsCache.published["Shared"]?.folder, folder.path)

        // Unlike a failed write, a blocked publish never touched the folder, so Stop Publishing can
        // still offer to remove the document there.
        h.dialogs.confirmAnswers = [false]
        model.stopPublishing()
        XCTAssertEqual(h.dialogs.confirms.map(\.message), [CollectionsModel.deletePublishedFileQuestion("shared.json")])
    }
}
