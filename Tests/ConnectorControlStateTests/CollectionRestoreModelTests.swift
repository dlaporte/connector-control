import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// The Restore sheet's Collections tab.
///
/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/CollectionRestoreModelTests.cs; the
/// confirmation is a sheet (pending state + the button's method).
@MainActor
final class CollectionRestoreModelTests: XCTestCase {
    /// `store` saved where the backups are, as a master-list backup holds it. Named older than any
    /// backup a save takes.
    @discardableResult
    private func backup(_ h: AppStateHarness, of store: MasterStore,
                        named name: String = "mcps.2026-01-01T00-00-00-000Z.json") throws -> URL {
        try FileManager.default.createDirectory(at: h.backupsDir, withIntermediateDirectories: true)
        let url = h.backupsDir.appendingPathComponent(name)
        try MasterStoreIO.save(store, to: url)
        return url
    }

    /// Chooses the backup at `url` as the sheet's list does: by the URL the listing gave, which may spell
    /// the temp folder another way.
    private func choose(_ url: URL, in model: CollectionRestoreModel) {
        model.selection = model.backups.first { $0.url.lastPathComponent == url.lastPathComponent }?.url
        XCTAssertNotNil(model.selection)
    }

    private func entry(_ command: String, on: Bool = true) -> MCPEntry {
        MCPEntry(enabled: on, config: .object(["command": .string(command)]))
    }

    /// A backup taken with Default as the harness seeds it, Gone (one connector, since deleted),
    /// Personal (one connector, one added since) and Data team (subscribed then and now).
    private func stagedBackup(_ h: AppStateHarness, _ state: AppState) throws -> URL {
        XCTAssertNil(state.addEmptyCollection(named: "Gone"))
        XCTAssertNil(state.upsert(name: "g", entry: entry("g"), renamedFrom: nil, in: "Gone"))
        XCTAssertNil(state.addEmptyCollection(named: "Personal"))
        XCTAssertNil(state.upsert(name: "notes", entry: entry("notes", on: false), renamedFrom: nil, in: "Personal"))
        try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        let url = try backup(h, of: state.store)
        XCTAssertNil(state.deleteCollection(named: "Gone"))
        XCTAssertNil(state.upsert(name: "added", entry: entry("added"), renamedFrom: nil, in: "Personal"))
        return url
    }

    func testListsBackupsNewestFirstWithTheirLabels() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let older = try backup(h, of: state.store)
        let newer = try backup(h, of: state.store, named: "mcps.2026-01-02T00-00-00-000Z.json")
        let model = CollectionRestoreModel(state: state)
        model.load()
        XCTAssertEqual(model.backups.map(\.url.lastPathComponent), [newer, older].map(\.lastPathComponent))
        let labels = BackupManager.labels(for: [newer, older])
        XCTAssertEqual(model.backups.map(\.label), [labels[newer.lastPathComponent], labels[older.lastPathComponent]])
        XCTAssertNil(model.selection, "no backup is chosen when the tab opens")
        XCTAssertTrue(model.rows.isEmpty)
        XCTAssertFalse(model.canRestore)
        XCTAssertNil(model.restoreError)
    }

    /// Each collection the chosen backup holds, by name, with its connector count and how it stands now;
    /// only one deleted since or differing from now can be restored. Choosing another backup clears the
    /// chosen collection.
    func testEachCollectionSaysHowItStandsNow() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try stagedBackup(h, state)
        let held = try XCTUnwrap(MasterStoreIO.read(from: url))
        func count(_ name: String) -> String {
            let mcps = held.collections[name]?.mcps ?? [:]
            return ControlTools.connectorCountText(mcps.count, mcps.values.filter(\.enabled).count)
        }
        let other = try backup(h, of: state.store, named: "mcps.2026-01-02T00-00-00-000Z.json")
        let model = CollectionRestoreModel(state: state)
        model.load()
        choose(url, in: model)

        XCTAssertEqual(model.rows.map(\.name), ["Data team", "Default", "Gone", "Personal"])
        XCTAssertEqual(model.rows.map(\.state), [.subscribed, .same, .gone, .differs])
        XCTAssertEqual(model.rows.map(\.detail), [
            count("Data team") + " · subscribed now",
            count("Default") + " · same as now",
            "1 connector, 1 on · deleted since",
            "1 connector, 0 on · differs from now",
        ])
        XCTAssertEqual(model.rows[2].spokenName, "Gone · 1 connector, 1 on · deleted since")
        XCTAssertNil(model.restoreError)

        XCTAssertFalse(model.canRestore, "no collection is chosen yet")
        for (name, restorable) in [("Data team", false), ("Default", false), ("Gone", true), ("Personal", true)] {
            model.collection = name
            XCTAssertEqual(model.canRestore, restorable, name)
        }

        choose(other, in: model)
        XCTAssertNil(model.collection, "another backup clears the chosen collection")
        XCTAssertFalse(model.canRestore)
        XCTAssertEqual(model.rows.map(\.name), ["Data team", "Default", "Personal"])
    }

    /// A row read when the backup was chosen is read again when its collection is chosen, so one that
    /// changed in between shows how it stands now, and the choice stays.
    func testChoosingACollectionRereadsItsRowsAgainstTheStoreNow() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try stagedBackup(h, state)
        let model = CollectionRestoreModel(state: state)
        model.load()
        choose(url, in: model)
        XCTAssertEqual(model.rows.first { $0.name == "Gone" }?.state, .gone)

        XCTAssertNil(state.addEmptyCollection(named: "Gone"))
        XCTAssertNil(state.upsert(name: "g", entry: entry("g"), renamedFrom: nil, in: "Gone"))
        XCTAssertEqual(model.rows.first { $0.name == "Gone" }?.state, .gone, "not read again until chosen")

        model.collection = "Gone"
        XCTAssertEqual(model.rows.first { $0.name == "Gone" }?.state, .same)
        XCTAssertEqual(model.collection, "Gone")
        XCTAssertFalse(model.canRestore)
    }

    /// The confirmation names the collection and the backup's time, and lists every line; then, after a
    /// blank line, that a gone collection is created again, or that Claude's config is updated too for
    /// the active one, or nothing more. A gone collection that held no connectors says only that.
    func testTheConfirmationListsEachLineAndWhatElseHappens() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.addEmptyCollection(named: "Empty"))
        let url = try stagedBackup(h, state)
        XCTAssertNil(state.deleteCollection(named: "Empty"))
        let active = try XCTUnwrap(state.store.collections["Default"])
        state.setEnabled(try XCTUnwrap(active.mcps.keys.sorted().first), false)
        let model = CollectionRestoreModel(state: state)
        model.load()
        choose(url, in: model)
        let label = try XCTUnwrap(model.backups.first { $0.url.lastPathComponent == url.lastPathComponent }?.label)
        let defaultLines = try state.collectionRestorePlan("Default", id: url.lastPathComponent,
                                                           data: try Data(contentsOf: url)).lines()
        XCTAssertEqual(defaultLines.first.map { $0.hasPrefix("~ ") }, true)

        for (collection, informative) in [
            ("Gone", "+ g (on)\n\n“Gone” no longer exists, so it is created again."),
            ("Default", defaultLines.joined(separator: "\n") + "\n\nClaude is running this collection, so Claude’s config is updated too."),
            ("Personal", "− added\n= 1 unchanged"),
            ("Empty", "“Empty” no longer exists, so it is created again."),
        ] {
            model.collection = collection
            XCTAssertTrue(model.canRestore, collection)
            model.requestRestore()
            XCTAssertTrue(model.confirming, collection)
            XCTAssertEqual(model.confirmMessage, "Restore “\(collection)” as it was at \(label)?")
            XCTAssertEqual(model.confirmInformative, informative)
            model.cancelRestore()
            XCTAssertFalse(model.confirming)
        }
        XCTAssertNil(state.store.collections["Gone"], "cancelled: nothing restored")
        XCTAssertNotNil(state.store.collections["Personal"]?.mcps["added"])
        XCTAssertTrue(h.dialogs.confirms.isEmpty)   // a sheet, not an NSAlert
    }

    /// What is restored is the backup as the tab read it when it loaded, not the file as it is by the
    /// time the user confirms.
    func testARestoreLandsTheBackupAsTheTabReadIt() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.addEmptyCollection(named: "Personal"))
        XCTAssertNil(state.upsert(name: "notes", entry: entry("notes"), renamedFrom: nil, in: "Personal"))
        let personal = try XCTUnwrap(state.store.collections["Personal"])
        let url = try backup(h, of: state.store)
        XCTAssertNil(state.deleteCollection(named: "Personal"))
        let model = CollectionRestoreModel(state: state)
        model.load()

        var rewritten = try XCTUnwrap(MasterStoreIO.read(from: url))
        rewritten.collections["Personal"]?.mcps["swapped"] = entry("evil")
        try MasterStoreIO.save(rewritten, to: url)
        choose(url, in: model)
        model.collection = "Personal"
        model.requestRestore()
        XCTAssertTrue(model.confirming)
        XCTAssertEqual(model.confirmInformative, "+ notes (on)\n\n“Personal” no longer exists, so it is created again.")
        XCTAssertTrue(model.confirmRestore())
        XCTAssertEqual(state.store.collections["Personal"], personal)
        XCTAssertEqual(state.activeCollection, "Default", "the active collection does not change")
        XCTAssertNil(model.restoreError)
    }

    /// A refusal is the sheet's alone on either side of the confirmation: nothing was restored, so the
    /// banner has nothing to say, and the rows then say how the collection stands.
    func testARefusalIsTheSheetsAlone() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let url = try stagedBackup(h, state)
        let data = try Data(contentsOf: url)
        let model = CollectionRestoreModel(state: state)
        model.load()
        choose(url, in: model)
        model.collection = "Personal"
        let banner = state.lastError

        // The collection goes back as it was while the confirmation is up.
        model.requestRestore()
        XCTAssertTrue(model.confirming)
        try state.restoreCollection("Personal", from: url, data: data)
        XCTAssertFalse(model.confirmRestore())
        XCTAssertEqual(model.restoreError, AppState.restoreCollectionUnchangedError("Personal"))
        XCTAssertEqual(state.lastError, banner, "a refusal is the sheet's alone")
        XCTAssertEqual(model.rows.first { $0.name == "Personal" }?.state, .same)
        XCTAssertFalse(model.canRestore)

        // And before the confirmation: the store changed after the rows were read.
        model.collection = "Gone"
        XCTAssertTrue(model.canRestore)
        try state.restoreCollection("Gone", from: url, data: data)
        model.requestRestore()
        XCTAssertFalse(model.confirming)
        XCTAssertEqual(model.restoreError, AppState.restoreCollectionUnchangedError("Gone"))
        XCTAssertEqual(state.lastError, banner)
    }

    /// A backup that cannot be read is listed, and chosen says so on the failure line, with no
    /// collections; choosing another clears it.
    func testAnUnreadableBackupSaysSoAndListsNoCollections() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let readable = try backup(h, of: state.store)
        let unreadable = h.backupsDir.appendingPathComponent("mcps.2026-01-02T00-00-00-000Z.json")
        try Data("{not json".utf8).write(to: unreadable)
        let model = CollectionRestoreModel(state: state)
        model.load()
        XCTAssertEqual(model.backups.map(\.url.lastPathComponent), [unreadable, readable].map(\.lastPathComponent))
        XCTAssertNil(model.restoreError, "nothing is said until it is chosen")

        choose(unreadable, in: model)
        XCTAssertEqual(model.restoreError, AppState.collectionBackupUnreadableError(unreadable.lastPathComponent))
        XCTAssertTrue(model.rows.isEmpty)
        XCTAssertFalse(model.canRestore)
        model.requestRestore()
        XCTAssertFalse(model.confirming)
        XCTAssertEqual(model.restoreError, AppState.collectionBackupUnreadableError(unreadable.lastPathComponent),
                       "still said: nothing was asked")

        choose(readable, in: model)
        XCTAssertNil(model.restoreError)
        XCTAssertEqual(model.rows.map(\.name), ["Default"])
    }

    /// A listing failure surfaces on the failure line rather than leaving an empty list unexplained.
    func testLoadSurfacesABackupsListingFailure() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        try backup(h, of: state.store)
        let model = CollectionRestoreModel(state: state)
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: h.backupsDir.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: h.backupsDir.path) }

        model.load()
        XCTAssertTrue(model.backups.isEmpty)
        XCTAssertNotNil(model.restoreError)
    }
}
