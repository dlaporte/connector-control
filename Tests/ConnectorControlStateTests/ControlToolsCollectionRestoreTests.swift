import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// `restore_collection`: one collection put back from a master-list backup, through the gate.
///
/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlToolsCollectionRestoreTests.cs
@MainActor
final class ControlToolsCollectionRestoreTests: XCTestCase {
    private let secret = "s3cr3t-notes-token"

    private func rig(answer: Bool? = true) -> ControlRig {
        let rig = ControlRig()
        rig.confirmer.scripted = answer
        return rig
    }

    private func config(_ command: String) -> JSONValue { .object(["command": .string(command)]) }

    private func arguments(_ id: String, _ collection: String) -> [String: JSONValue] {
        ["id": .string(id), "collection": .string(collection)]
    }

    /// The master list's backups, newest first, as `list_backups` lists them.
    private func backups(_ rig: ControlRig) throws -> [URL] {
        try rig.state.service.backups.backups(series: CollectionRestoreModel.series)
    }

    /// `store` saved where the backups are, as a master-list backup holds it, under a name older than
    /// any backup a save takes. Returns its id.
    private func backup(_ rig: ControlRig, of store: MasterStore, named name: String = "mcps.2026-01-01T00-00-00-000Z.json") throws -> String {
        try MasterStoreIO.save(store, to: rig.h.backupsDir.appendingPathComponent(name))
        return name
    }

    /// What the question calls the backup `id`: its label among the backups listed now, as the Restore
    /// sheet labels it. Local time, so never written out.
    private func label(_ rig: ControlRig, _ id: String) throws -> String {
        try XCTUnwrap(BackupManager.labels(for: try backups(rig))[id])
    }

    /// Every file in the backups folder and its bytes.
    private func backupFiles(_ rig: ControlRig) throws -> [String: Data] {
        let urls = try FileManager.default.contentsOfDirectory(at: rig.h.backupsDir, includingPropertiesForKeys: nil)
        return Dictionary(uniqueKeysWithValues: try urls.map { ($0.lastPathComponent, try Data(contentsOf: $0)) })
    }

    /// Personal, two connectors and one of them on (holding the secret), deleted; the id of the backup
    /// the delete took, which holds it whole.
    private func deletedPersonal(_ rig: ControlRig) throws -> (id: String, personal: Collection) {
        XCTAssertNil(rig.state.addEmptyCollection(named: "Personal"))
        let notes = MCPEntry(config: .object(["command": .string("node"), "env": .object(["NOTES_TOKEN": .string(secret)])]))
        XCTAssertNil(rig.state.upsert(name: "notes", entry: notes, renamedFrom: nil, in: "Personal"))
        XCTAssertNil(rig.state.upsert(name: "calendar", entry: MCPEntry(enabled: false, config: config("calendar")), renamedFrom: nil, in: "Personal"))
        let personal = try XCTUnwrap(rig.state.store.collections["Personal"])
        XCTAssertNil(rig.state.deleteCollection(named: "Personal"))
        let taken = try XCTUnwrap(try backups(rig).first { MasterStoreIO.read(from: $0)?.collections["Personal"] == personal })
        return (taken.lastPathComponent, personal)
    }

    /// A deleted collection: the question names the backup by its label and lists every connector that
    /// comes back with its switch; allowed, the collection is back exactly as it was, and the answer
    /// carries no config value.
    func testRestoringADeletedCollectionAsksWithEveryLineAndCreatesItAgain() throws {
        let rig = rig(); defer { rig.dispose() }
        let (id, personal) = try deletedPersonal(rig)
        let time = try label(rig, id)
        let result = try XCTUnwrap(rig.call("restore_collection", arguments(id, "Personal")))
        XCTAssertEqual(rig.confirmer.requests, [ConfirmationRequest(
            title: ControlTools.restoreTitle("Claude", "Personal"),
            message: ControlTools.restoreCollectionCreatedText("Personal", time),
            code: ["+ calendar (off)", "+ notes (on)"], footer: nil)])
        XCTAssertEqual(result, ControlResult(structured: .object([
            "id": .string(id), "collection": .string("Personal"), "activeCollection": .string("Default"),
            "restartRequired": .bool(false), "applied": .bool(true),
        ]), text: ControlTools.restoredText("Personal"), isError: false))
        XCTAssertEqual(rig.state.store.collections["Personal"], personal)
        XCTAssertFalse(rig.state.isSynced("Personal"))
        XCTAssertEqual(rig.state.activeCollection, "Default")
        XCTAssertFalse(result.text.contains(secret))
        XCTAssertFalse(result.structured.compactLine().contains(secret))
    }

    /// Each refusal comes before anyone is asked: an id that is not a master-list backup's (a Claude's
    /// config backup's among them, as restore_backup refuses a master-list backup's), one that cannot be
    /// read, a collection the backup does not hold, one subscribed now, and one already as it was. The
    /// names they echo show as escapes.
    func testEachRefusalComesBeforeAnyoneIsAsked() throws {
        let rig = rig(); defer { rig.dispose() }
        try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        let id = try backup(rig, of: rig.state.store)
        let unreadable = "mcps.2026-01-02T00-00-00-000Z.json"
        try Data("{".utf8).write(to: rig.h.backupsDir.appendingPathComponent(unreadable))
        let claudeID = try XCTUnwrap(try rig.state.service.backups.backups(series: RestoreModel.series).first?.lastPathComponent)
        let odd = "Odd\u{201D}\nname"
        let cases: [(String, [String: JSONValue], ControlResult)] = [
            ("restore_collection", arguments("../mcps.json", "Default"), .refusal(ControlTools.noBackupError("../mcps.json"))),
            ("restore_collection", arguments(claudeID, "Default"), .refusal(ControlTools.noBackupError(claudeID))),
            ("restore_backup", ["id": .string(id)], .refusal(ControlTools.noBackupError(id))),
            ("restore_collection", arguments(unreadable, "Default"), .refusal(AppState.collectionBackupUnreadableError(unreadable))),
            ("restore_collection", arguments(id, odd),
             .refusal(AppState.collectionNotInBackupError(id, ControlText.escapedName(odd)))),
            ("restore_collection", arguments(id, "Data team"), .refusal(AppState.restoreCollectionSubscribedError("Data team"))),
            ("restore_collection", arguments(id, "Default"), .refusal(AppState.restoreCollectionUnchangedError("Default"))),
        ]
        for (tool, arguments, expected) in cases {
            XCTAssertEqual(rig.call(tool, arguments), expected, "\(tool) \(arguments)")
        }
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
    }

    /// Don't Allow leaves the master list, Claude's config and every backup as they were.
    func testDontAllowLeavesTheStoreAndTheBackupsUntouched() throws {
        let rig = rig(answer: false); defer { rig.dispose() }
        let (id, _) = try deletedPersonal(rig)
        let store = try rig.h.storeOnDisk(), claude = try rig.h.claudeServers(), files = try backupFiles(rig)
        XCTAssertEqual(rig.call("restore_collection", arguments(id, "Personal")), .refusal(ControlTools.declinedError))
        XCTAssertEqual(rig.confirmer.requests.count, 1)
        XCTAssertNil(rig.state.store.collections["Personal"])
        XCTAssertEqual(try rig.h.storeOnDisk(), store)
        XCTAssertEqual(try rig.h.claudeServers(), claude)
        XCTAssertEqual(try backupFiles(rig), files)
    }

    /// A config changed while the question was up, where the question reads the same (the connector
    /// changes either way), is not overwritten: the Allow lands nothing.
    func testACollectionThatChangedWhileAskingIsNotRestored() throws {
        let rig = rig(answer: nil); defer { rig.dispose() }
        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
        XCTAssertNil(rig.state.upsert(name: "one", entry: MCPEntry(config: config("a")), renamedFrom: nil, in: "Work"))
        let id = try backup(rig, of: rig.state.store)
        XCTAssertNil(rig.state.upsert(name: "one", entry: MCPEntry(config: config("b")), renamedFrom: "one", in: "Work"))
        let answer = rig.start("restore_collection", arguments(id, "Work"))
        XCTAssertEqual(rig.confirmer.requests.last?.code, ["~ one (on)"])
        XCTAssertNil(rig.state.upsert(name: "one", entry: MCPEntry(config: config("c")), renamedFrom: "one", in: "Work"))
        rig.confirmer.answer(true)
        rig.h.drain()
        XCTAssertEqual(answer.result, .refusal(ControlTools.changedWhileAskingError))
        XCTAssertEqual(rig.state.store.collections["Work"]?.mcps["one"]?.config, config("c"), "nothing restored")
        _ = rig.start("restore_collection", arguments(id, "Work"))
        XCTAssertEqual(rig.confirmer.requests.count, 2)
        XCTAssertEqual(rig.confirmer.requests.first, rig.confirmer.requests.last, "the question itself reads the same")
    }

    /// A backup rewritten while the question was up, where the question reads the same, is not restored
    /// from: the Allow lands nothing.
    func testABackupThatChangedWhileAskingIsNotRestoredFrom() throws {
        let rig = rig(answer: nil); defer { rig.dispose() }
        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
        XCTAssertNil(rig.state.upsert(name: "one", entry: MCPEntry(config: config("a")), renamedFrom: nil, in: "Work"))
        let id = try backup(rig, of: rig.state.store)
        XCTAssertNil(rig.state.upsert(name: "one", entry: MCPEntry(config: config("b")), renamedFrom: "one", in: "Work"))
        var rewritten = try XCTUnwrap(MasterStoreIO.read(from: rig.h.backupsDir.appendingPathComponent(id)))
        rewritten.collections["Work"]?.mcps["one"] = MCPEntry(config: config("z"))
        let answer = rig.start("restore_collection", arguments(id, "Work"))
        _ = try backup(rig, of: rewritten, named: id)
        rig.confirmer.answer(true)
        rig.h.drain()
        XCTAssertEqual(answer.result, .refusal(ControlTools.changedWhileAskingError))
        XCTAssertEqual(rig.state.store.collections["Work"]?.mcps["one"]?.config, config("b"), "nothing restored")
        _ = rig.start("restore_collection", arguments(id, "Work"))
        XCTAssertEqual(rig.confirmer.requests.count, 2)
        XCTAssertEqual(rig.confirmer.requests.first, rig.confirmer.requests.last, "the question itself reads the same")
    }

    /// With confirmations off nobody is asked, so the restore is announced, naming who asked.
    func testARestoreNobodyWasAskedAboutIsAnnounced() throws {
        let rig = ControlRig(confirm: false); defer { rig.dispose() }
        let (id, personal) = try deletedPersonal(rig)
        XCTAssertEqual(rig.call("restore_collection", arguments(id, "Personal"))?.text, ControlTools.restoredText("Personal"))
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [ControlTools.restoredNotification(ControlCaller.claudeName, "Personal")])
        XCTAssertEqual(rig.state.store.collections["Personal"], personal)
    }

    /// Only the collection Claude runs takes effect when Claude restarts: its question says so, and its
    /// answer says to restart. Another collection's says neither.
    func testOnlyTheActiveCollectionsRestoreSaysToRestart() throws {
        let rig = rig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
        let id = try backup(rig, of: rig.state.store)
        XCTAssertNil(rig.state.upsert(name: "extra", entry: MCPEntry(config: config("x")), renamedFrom: nil))
        rig.state.applyInteractively()
        XCTAssertNil(rig.state.upsert(name: "extra", entry: MCPEntry(config: config("x")), renamedFrom: nil, in: "Work"))
        rig.h.claudeRunningSince(hours: 1)
        rig.state.refreshRestartState()
        let restart = JSONPointer(["restartRequired"])
        // Before the call: the restore's own backup is listed after it, and the question never named it.
        let workLabel = try label(rig, id)
        let work = try XCTUnwrap(rig.call("restore_collection", arguments(id, "Work")))
        XCTAssertEqual(rig.confirmer.requests.last?.code, ["− extra"])
        XCTAssertNil(rig.confirmer.requests.last?.footer)
        XCTAssertEqual(rig.confirmer.requests.last?.message, ControlTools.restoreCollectionText("Work", workLabel))
        XCTAssertEqual(work.text, ControlTools.restoredText("Work"))
        XCTAssertEqual(work.structured.value(at: restart), .bool(false))
        let active = try XCTUnwrap(rig.call("restore_collection", arguments(id, "Default")))
        XCTAssertEqual(rig.confirmer.requests.last?.footer, ControlTools.takesEffectFooter)
        XCTAssertEqual(active.text, ControlTools.restoredText("Default") + " " + ControlTools.restartToApplyText)
        XCTAssertEqual(active.structured.value(at: restart), .bool(true))
        XCTAssertNil(try rig.h.claudeServers()["extra"])
    }

    /// Two backups of one second: the question names the one asked about with its milliseconds, so
    /// its label is worked out over every backup listed, as `list_backups` and the sheet list them.
    func testTheQuestionLabelsTheBackupAmongAllListedOnes() throws {
        let rig = rig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
        let first = try backup(rig, of: rig.state.store, named: "mcps.2026-09-04T12-00-00-000Z.json")
        _ = try backup(rig, of: rig.state.store, named: "mcps.2026-09-04T12-00-00-500Z.json")
        XCTAssertNil(rig.state.upsert(name: "extra", entry: MCPEntry(config: config("x")), renamedFrom: nil, in: "Work"))
        let listed = try label(rig, first)
        XCTAssertTrue(listed.hasSuffix(".000"), listed)
        XCTAssertNotEqual(listed, BackupManager.labels(for: try backups(rig).filter { $0.lastPathComponent == first })[first])
        XCTAssertNotNil(rig.call("restore_collection", arguments(first, "Work")))
        XCTAssertEqual(rig.confirmer.requests.last?.message, ControlTools.restoreCollectionText("Work", listed))
    }

    /// A deleted collection and one of its connectors, each named with a quote and a newline: the
    /// question's title, message and code lines show both as escapes, the way every control text does.
    func testTheQuestionEscapesTheNamesItShows() throws {
        let rig = rig(); defer { rig.dispose() }
        let odd = "Odd\"\nname", connector = "con\"\nnector"
        XCTAssertNil(rig.state.addEmptyCollection(named: odd))
        XCTAssertNil(rig.state.upsert(name: connector, entry: MCPEntry(config: config("x")), renamedFrom: nil, in: odd))
        let id = try backup(rig, of: rig.state.store)
        XCTAssertNil(rig.state.deleteCollection(named: odd))
        let time = try label(rig, id)
        XCTAssertNotNil(rig.call("restore_collection", arguments(id, odd)))
        let shown = ControlText.escapedName(odd)
        XCTAssertFalse(shown.contains("\n"))
        XCTAssertEqual(rig.confirmer.requests, [ConfirmationRequest(
            title: ControlTools.restoreTitle("Claude", shown),
            message: ControlTools.restoreCollectionCreatedText(shown, time),
            code: ["+ \(ControlText.escapedName(connector)) (on)"], footer: nil)])
    }
}
