import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Restoring one collection from a master-list backup (`collectionRestorePlan`, `restoreCollection`).
///
/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/AppStateCollectionRestoreTests.cs
@MainActor
final class AppStateCollectionRestoreTests: XCTestCase {
    /// `store` saved where the backups are, as a master-list backup holds it: its file and bytes. Named
    /// older than any backup a save takes, so the newest in the series is still the save's own.
    private func backup(_ h: AppStateHarness, of store: MasterStore,
                        named name: String = "mcps.2026-01-01T00-00-00-000Z.json") throws -> (url: URL, data: Data) {
        try FileManager.default.createDirectory(at: h.backupsDir, withIntermediateDirectories: true)
        let url = h.backupsDir.appendingPathComponent(name)
        try MasterStoreIO.save(store, to: url)
        return (url, try Data(contentsOf: url))
    }

    private func config(_ command: String) -> JSONValue { .object(["command": .string(command)]) }

    private func mcpsBackups(_ state: AppState) throws -> [URL] { try state.service.backups.backups(series: "mcps") }

    /// The reported sequence. Personal, with 11 connectors and 2 on, is deleted. A Claude's config restore of
    /// a backup recorded against it, holding the one connector Claude was running, creates Personal again
    /// with that one, active. Restoring Personal from the master-list backup taken before the delete then
    /// brings back all 11 with the same 2 on, and leaves Harvard alone.
    func testTheReportedSequenceBringsPersonalBackWhole() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.addEmptyCollection(named: "Personal"))
        for index in 1...11 {
            let name = String(format: "p%02d", index)
            XCTAssertNil(state.upsert(name: name, entry: MCPEntry(enabled: index <= 2, config: config(name)),
                                      renamedFrom: nil, in: "Personal"))
        }
        XCTAssertNil(state.addEmptyCollection(named: "Harvard"))
        XCTAssertNil(state.upsert(name: "canvas", entry: MCPEntry(config: config("canvas")), renamedFrom: nil, in: "Harvard"))
        state.switchCollection(to: "Harvard")
        let personal = try XCTUnwrap(state.store.collections["Personal"])
        let harvard = try XCTUnwrap(state.store.collections["Harvard"])
        // What Claude ran from Personal once: one connector, recorded against Personal.
        try FileManager.default.createDirectory(at: h.backupsDir, withIntermediateDirectories: true)
        let claudeBackup = h.backupsDir.appendingPathComponent("claude_desktop_config.2026-11-01T00-00-00-000Z.json")
        try Data(#"{"mcpServers": {"p01": {"command": "p01"}}}"#.utf8).write(to: claudeBackup)
        try BackupCollections.record("Personal", for: claudeBackup, in: h.backupsDir, staging: nil)

        XCTAssertNil(state.deleteCollection(named: "Personal"))
        try state.restoreClaudeConfig(from: claudeBackup)
        XCTAssertEqual(state.activeCollection, "Personal")
        XCTAssertEqual(state.store.collections["Personal"]?.mcps.keys.sorted(), ["p01"])

        // Newest first, the first backup holding all 11 is the one the delete took, and pinned.
        let before = try XCTUnwrap(try mcpsBackups(state).first { MasterStoreIO.read(from: $0)?.collections["Personal"]?.mcps.count == 11 })
        XCTAssertTrue(BackupPins.pinned(in: h.backupsDir).contains(before.lastPathComponent))
        let data = try BoundedRead.data(at: before, limit: ConfigService.maxBackupBytes)
        let plan = try state.collectionRestorePlan("Personal", id: before.lastPathComponent, data: data)
        XCTAssertFalse(plan.created)
        XCTAssertTrue(plan.active)
        XCTAssertEqual(plan.lines(), ["+ p02 (on)"] + (3...11).map { String(format: "+ p%02d (off)", $0) } + ["= 1 unchanged"])

        try state.restoreCollection("Personal", from: before, data: data)
        XCTAssertEqual(state.store.collections["Personal"], personal)
        XCTAssertEqual(state.store.collections["Personal"]?.mcps.values.filter(\.enabled).count, 2)
        XCTAssertEqual(state.store.collections["Harvard"], harvard)
        XCTAssertEqual(state.activeCollection, "Personal")
        XCTAssertEqual(Set(try h.claudeServers().keys), ["p01", "p02"])
    }

    /// Exactly as it was: a connector added since goes, a changed config and a changed switch go back, and
    /// one deleted since comes back. The lines run by name, whatever each one does.
    func testACollectionGoesBackExactlyAsItWas() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let before = try XCTUnwrap(state.store.collections["Default"])
        let taken = try backup(h, of: state.store)
        XCTAssertNil(state.upsert(name: "added-since", entry: MCPEntry(config: config("a")), renamedFrom: nil))
        XCTAssertNil(state.upsert(name: "aws-mcp", entry: MCPEntry(config: config("changed")), renamedFrom: "aws-mcp"))
        state.setEnabled("scoutbook", false)
        state.delete(names: ["service-now"])

        let plan = try state.collectionRestorePlan("Default", id: taken.url.lastPathComponent, data: taken.data)
        XCTAssertEqual(plan.collection, "Default")
        XCTAssertFalse(plan.created)
        XCTAssertTrue(plan.active)
        XCTAssertEqual(plan.lines(), ["− added-since", "~ aws-mcp (on)", "~ scoutbook (on)", "+ service-now (on)"])
        XCTAssertEqual(plan.lines(naming: { "<\($0)>" }).first, "− <added-since>", "the caller names each connector")

        try state.restoreCollection("Default", from: taken.url, data: taken.data)
        XCTAssertEqual(state.store.collections["Default"], before)
        XCTAssertEqual(try h.storeOnDisk().collections["Default"], before)
        XCTAssertEqual(state.activeCollection, "Default")
    }

    /// Each refusal, in the order they are checked; none of them writes anything.
    func testEachRefusalInOrder() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        let taken = try backup(h, of: state.store)
        let id = taken.url.lastPathComponent
        let storeBefore = state.store
        let backupsBefore = try mcpsBackups(state)
        /// The plan's refusal, which the restore itself gives too when it is handed the same bytes.
        func refusal(_ collection: String, _ data: Data) -> CollectionRestoreError? {
            var planned: CollectionRestoreError?
            XCTAssertThrowsError(try state.collectionRestorePlan(collection, id: id, data: data)) { planned = $0 as? CollectionRestoreError }
            XCTAssertThrowsError(try state.restoreCollection(collection, from: taken.url, data: data)) {
                XCTAssertEqual($0 as? CollectionRestoreError, planned)
            }
            return planned
        }

        XCTAssertThrowsError(try state.collectionRestorePlan("Data team", id: id, data: nil), "bytes that could not be read") {
            XCTAssertEqual($0 as? CollectionRestoreError, .backupUnreadable(id))
        }
        XCTAssertEqual(refusal("Data team", Data("{not json".utf8)), .backupUnreadable(id), "unreadable comes first")
        XCTAssertEqual(refusal("Nope", taken.data), .notInBackup(id: id, collection: "Nope"))
        XCTAssertEqual(refusal("Data team", taken.data), .subscribed("Data team"), "subscribed before unchanged")
        XCTAssertEqual(refusal("Default", taken.data), .unchanged("Default"))

        XCTAssertEqual(CollectionRestoreError.backupUnreadable(id).localizedDescription, AppState.collectionBackupUnreadableError(id))
        XCTAssertEqual(CollectionRestoreError.notInBackup(id: id, collection: "Nope").localizedDescription,
                       AppState.collectionNotInBackupError(id, "Nope"))
        XCTAssertEqual(CollectionRestoreError.subscribed("Data team").localizedDescription,
                       AppState.restoreCollectionSubscribedError("Data team"))
        XCTAssertEqual(CollectionRestoreError.unchanged("Default").localizedDescription, AppState.restoreCollectionUnchangedError("Default"))
        XCTAssertEqual(state.store, storeBefore)
        XCTAssertEqual(try mcpsBackups(state), backupsBefore, "nothing was saved")
    }

    /// Without the bytes, the restore reads the backup itself; one it cannot read is refused as unreadable.
    func testARestoreWithoutBytesReadsTheBackup() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let taken = try backup(h, of: state.store)
        state.setEnabled("aws-mcp", false)
        try state.restoreCollection("Default", from: taken.url)
        XCTAssertEqual(state.store.collections["Default"]?.mcps["aws-mcp"]?.enabled, true)
        let missing = h.backupsDir.appendingPathComponent("mcps.2026-01-02T00-00-00-000Z.json")
        XCTAssertThrowsError(try state.restoreCollection("Default", from: missing)) {
            XCTAssertEqual($0 as? CollectionRestoreError, .backupUnreadable(missing.lastPathComponent))
        }
    }

    /// A collection that was subscribed when the backup was taken and is gone now comes back local: the
    /// backup holds its connectors, never its subscription.
    func testAGoneCollectionThatWasSubscribedComesBackLocal() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        let authored = try XCTUnwrap(state.store.collections["Data team"])
        let taken = try backup(h, of: state.store)
        XCTAssertNil(state.deleteCollection(named: "Data team"))

        let plan = try state.collectionRestorePlan("Data team", id: taken.url.lastPathComponent, data: taken.data)
        XCTAssertTrue(plan.created)
        XCTAssertFalse(plan.active)
        try state.restoreCollection("Data team", from: taken.url, data: taken.data)
        XCTAssertEqual(state.store.collections["Data team"], authored)
        XCTAssertEqual(state.kind(of: "Data team"), .local)
        XCTAssertNil(state.collectionsFile.collections["Data team"], "no sidecar entry")
        XCTAssertEqual(state.activeCollection, "Default")
    }

    /// A collection the store holds under another spelling of its name (é composed, the backup's
    /// decomposed) is restored into the spelling held, never beside it.
    func testAnotherSpellingOfTheNameRestoresIntoTheOneHeld() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let composed = "Caf\u{E9}", decomposed = "Cafe\u{301}"
        XCTAssertNil(state.addEmptyCollection(named: composed))
        XCTAssertNil(state.upsert(name: "now", entry: MCPEntry(config: config("n")), renamedFrom: nil, in: composed))
        let then = Collection(mcps: ["then": MCPEntry(config: config("t"))])
        var other = state.store
        other.collections.removeValue(forKey: composed)
        other.collections[decomposed] = then
        let taken = try backup(h, of: other)

        let plan = try state.collectionRestorePlan(decomposed, id: taken.url.lastPathComponent, data: taken.data)
        XCTAssertTrue(plan.collection.hasSameScalars(as: composed))
        XCTAssertFalse(plan.created)
        try state.restoreCollection(decomposed, from: taken.url, data: taken.data)
        let names = state.store.collections.keys.filter { $0.hasPrefix("Caf") }
        XCTAssertEqual(names.count, 1)
        XCTAssertTrue(names[0].hasSameScalars(as: composed))
        XCTAssertEqual(state.store.collections[composed], then)
    }

    /// Restoring the active collection updates Claude's config; restoring another touches nothing Claude runs.
    func testOnlyTheActiveCollectionsRestoreReachesClaude() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.addEmptyCollection(named: "Work"))
        XCTAssertNil(state.upsert(name: "w1", entry: MCPEntry(config: config("w1")), renamedFrom: nil, in: "Work"))
        XCTAssertNil(state.upsert(name: "w2", entry: MCPEntry(config: config("w2")), renamedFrom: nil, in: "Work"))
        let taken = try backup(h, of: state.store)
        state.setEnabled("w1", false, in: "Work")
        state.setEnabled("aws-mcp", false)
        XCTAssertNil(try h.claudeServers()["aws-mcp"])
        let claudeBefore = try Data(contentsOf: h.claudeConfigURL)
        let appliedBefore = h.settings.lastApplyDate

        let work = try state.collectionRestorePlan("Work", id: taken.url.lastPathComponent, data: taken.data)
        XCTAssertFalse(work.active)
        XCTAssertEqual(work.lines(), ["~ w1 (on)", "= 1 unchanged"])
        try state.restoreCollection("Work", from: taken.url, data: taken.data)
        XCTAssertEqual(state.store.collections["Work"]?.mcps["w1"]?.enabled, true)
        XCTAssertEqual(try Data(contentsOf: h.claudeConfigURL), claudeBefore, "Claude does not run Work")
        XCTAssertEqual(h.settings.lastApplyDate, appliedBefore)

        h.now = h.now.addingTimeInterval(60)
        XCTAssertTrue(try state.collectionRestorePlan("Default", id: taken.url.lastPathComponent, data: taken.data).active)
        try state.restoreCollection("Default", from: taken.url, data: taken.data)
        XCTAssertNotNil(try h.claudeServers()["aws-mcp"], "Claude runs Default")
        XCTAssertEqual(h.settings.lastApplyDate, h.now)
        XCTAssertEqual(state.activeCollection, "Default")
    }

    /// The restore keeps the backup it takes for 30 days, so it can be undone the same way.
    func testTheRestoresOwnBackupIsPinned() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let taken = try backup(h, of: state.store)
        state.setEnabled("aws-mcp", false)
        XCTAssertEqual(BackupPins.pinned(in: h.backupsDir), [])

        try state.restoreCollection("Default", from: taken.url, data: taken.data)
        let newest = try XCTUnwrap(try mcpsBackups(state).first)
        XCTAssertNotEqual(newest, taken.url)
        XCTAssertEqual(BackupPins.pinned(in: h.backupsDir), [newest.lastPathComponent])
        XCTAssertEqual(MasterStoreIO.read(from: newest)?.collections["Default"]?.mcps["aws-mcp"]?.enabled, false,
                       "the pinned copy holds what the restore replaced")
    }

    /// A connector the restore removes from a published collection loses its marks, as a delete takes them.
    func testAConnectorTheRestoreRemovesLosesItsPublishIntent() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let taken = try backup(h, of: state.store)
        let path = "/Users/d/ledger/dist/index.js"
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(path)]),
        ])), renamedFrom: nil))
        let intent = PublishIntent(shareValues: [:], pathMarks: ["ledger": [JSONPointer(["args", "0"]):
            .init(name: "server_path", hint: "your ledger clone", value: path)]], hints: [:])
        let document = try h.publish(state, "Default", intent: intent)
        XCTAssertNotNil(state.collectionsFile.collections["Default"]?.publish?.intent.pathMarks["ledger"])

        try state.restoreCollection("Default", from: taken.url, data: taken.data)
        XCTAssertNil(state.store.collections["Default"]?.mcps["ledger"])
        XCTAssertNil(state.collectionsFile.collections["Default"]?.publish?.intent.pathMarks["ledger"])
        XCTAssertNil(state.publishError)
        XCTAssertNil(try CollectionDocument.decode(try Data(contentsOf: document)).connectors["ledger"])
    }

    /// A published collection keeps the marks of every connector the restore leaves in place: only one it
    /// removes loses its own, and the document still carries the placeholder, never the path.
    func testAPublishedCollectionKeepsTheMarksOfWhatStays() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let path = "/Users/d/ledger/dist/index.js"
        XCTAssertNil(state.upsert(name: "ledger", entry: MCPEntry(config: .object([
            "command": .string("node"), "args": .array([.string(path)]),
        ])), renamedFrom: nil))
        let taken = try backup(h, of: state.store)
        XCTAssertNil(state.upsert(name: "added-since", entry: MCPEntry(config: config("a")), renamedFrom: nil))
        let marks: [JSONPointer: PublishIntent.PathMark] = [
            JSONPointer(["args", "0"]): .init(name: "server_path", hint: "your ledger clone", value: path),
        ]
        let document = try h.publish(state, "Default", intent: PublishIntent(shareValues: [:], pathMarks: ["ledger": marks], hints: [:]))

        try state.restoreCollection("Default", from: taken.url, data: taken.data)
        XCTAssertNil(state.store.collections["Default"]?.mcps["added-since"])
        XCTAssertEqual(state.collectionsFile.collections["Default"]?.publish?.intent.pathMarks["ledger"], marks)
        XCTAssertNil(state.publishError)
        let bytes = try Data(contentsOf: document)
        let published = try CollectionDocument.decode(bytes)
        XCTAssertNil(published.connectors["added-since"])
        XCTAssertEqual(published.connectors["ledger"]?.needs.keys.sorted(), ["server_path"])
        XCTAssertFalse(String(decoding: bytes, as: UTF8.self).contains(path))
    }

    /// A Claude's config backup recorded under another spelling of a collection's name (decomposed, the
    /// store's composed) goes back into the collection held, which becomes active: it is not gone, so
    /// nothing is created and the confirmation says nothing of it.
    func testAClaudeConfigBackupRecordedUnderAnotherSpellingGoesIntoTheOneHeld() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let composed = "Caf\u{E9}", decomposed = "Cafe\u{301}"
        XCTAssertNil(state.addEmptyCollection(named: composed))
        try FileManager.default.createDirectory(at: h.backupsDir, withIntermediateDirectories: true)
        let claudeBackup = h.backupsDir.appendingPathComponent("claude_desktop_config.2026-11-01T00-00-00-000Z.json")
        try Data(#"{"mcpServers": {"c1": {"command": "c1"}}}"#.utf8).write(to: claudeBackup)
        try BackupCollections.record(decomposed, for: claudeBackup, in: h.backupsDir, staging: nil)

        let model = RestoreModel(state: state)
        model.load()
        model.selection = claudeBackup
        model.requestRestore()
        XCTAssertTrue(model.confirming)
        XCTAssertNil(model.confirmInformative)
        XCTAssertTrue(model.confirmRestore())
        XCTAssertTrue(state.activeCollection.hasSameScalars(as: composed))
        let names = state.store.collections.keys.filter { $0.hasPrefix("Caf") }
        XCTAssertEqual(names.count, 1)
        XCTAssertTrue(names[0].hasSameScalars(as: composed))
        XCTAssertNotNil(state.store.collections[composed]?.mcps["c1"])
    }

    /// For a collection this machine publishes, a connector that comes back has no review: the restore
    /// lands in the store, and nothing reaches the folder until the Publish sheet reviews it.
    func testAConnectorThatComesBackToAPublishedCollectionIsHeldForReview() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "extra", entry: MCPEntry(config: config("x")), renamedFrom: nil))
        let taken = try backup(h, of: state.store)
        let document = try h.publish(state, "Default")
        state.delete(names: ["extra"])
        XCTAssertNil(state.publishError)
        let published = try Data(contentsOf: document)

        try state.restoreCollection("Default", from: taken.url, data: taken.data)
        XCTAssertNotNil(state.store.collections["Default"]?.mcps["extra"])
        XCTAssertEqual(try Data(contentsOf: document), published, "nothing reached the folder")
        XCTAssertEqual(state.publishError?.collection, "Default")
        XCTAssertEqual(state.publishError?.message, AppState.unreviewedConnectorError("extra"))
        XCTAssertEqual(state.publishError?.kind, .blockedForReview)
    }
}
