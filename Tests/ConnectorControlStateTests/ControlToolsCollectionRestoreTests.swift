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

    /// The master list's backups and their bytes: a save takes one, and writing Claude's config again
    /// takes only a backup of Claude's config.
    private func collectionBackupFiles(_ rig: ControlRig) throws -> [String: Data] {
        try backupFiles(rig).filter { $0.key.hasPrefix(CollectionRestoreModel.series + ".") }
    }

    /// A connector holding a value Claude never sees.
    private func vault(_ value: String, on: Bool = true) -> MCPEntry {
        MCPEntry(enabled: on, config: .object(["command": .string("node"), "env": .object(["API_KEY": .string(value)])]))
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
    /// read, a collection the backup does not hold, one subscribed now, and, from the command line, one
    /// already as it was. The names they echo show as escapes.
    func testEachRefusalComesBeforeAnyoneIsAsked() throws {
        let rig = rig(); defer { rig.dispose() }
        try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        let id = try backup(rig, of: rig.state.store)
        let unreadable = "mcps.2026-01-02T00-00-00-000Z.json"
        try Data("{".utf8).write(to: rig.h.backupsDir.appendingPathComponent(unreadable))
        let claudeID = try XCTUnwrap(try rig.state.service.backups.backups(series: RestoreModel.series).first?.lastPathComponent)
        let odd = "Odd\u{201D}\nname"
        let cases: [(String, [String: JSONValue], ControlCaller, ControlResult)] = [
            ("restore_collection", arguments("../mcps.json", "Default"), .relay(clientName: "claude-ai"), .refusal(ControlTools.noBackupError("../mcps.json"))),
            ("restore_collection", arguments(claudeID, "Default"), .relay(clientName: "claude-ai"), .refusal(ControlTools.noBackupError(claudeID))),
            ("restore_backup", ["id": .string(id)], .relay(clientName: "claude-ai"), .refusal(ControlTools.noBackupError(id))),
            ("restore_collection", arguments(unreadable, "Default"), .relay(clientName: "claude-ai"),
             .refusal(AppState.collectionBackupUnreadableError(unreadable))),
            ("restore_collection", arguments(id, odd), .relay(clientName: "claude-ai"),
             .refusal(AppState.collectionNotInBackupError(id, ControlText.escapedName(odd)))),
            ("restore_collection", arguments(id, "Data team"), .relay(clientName: "claude-ai"),
             .refusal(AppState.restoreCollectionSubscribedError("Data team"))),
            ("restore_collection", arguments(id, "Default"), .cli, .refusal(AppState.restoreCollectionUnchangedError("Default"))),
        ]
        for (tool, arguments, caller, expected) in cases {
            XCTAssertEqual(rig.call(tool, arguments, as: caller), expected, "\(tool) \(arguments)")
        }
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
    }

    /// From Claude, a collection already as the backup holds it is not refused: the question lists its
    /// only line, how many connectors stay as they are, and the answer is a restore's. Nothing in the
    /// master list changes and nothing is saved; a collection Claude doesn't run leaves Claude's config
    /// alone too.
    func testAnUnchangedCollectionIsAskedAndAnsweredAsRestored() throws {
        let rig = rig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
        XCTAssertNil(rig.state.upsert(name: "one", entry: MCPEntry(config: config("a")), renamedFrom: nil, in: "Work"))
        XCTAssertNil(rig.state.upsert(name: "two", entry: MCPEntry(enabled: false, config: config("b")), renamedFrom: nil, in: "Work"))
        let id = try backup(rig, of: rig.state.store)
        let time = try label(rig, id)
        let store = try rig.h.storeOnDisk(), claude = try rig.h.claudeServers(), files = try backupFiles(rig)
        let result = try XCTUnwrap(rig.call("restore_collection", arguments(id, "Work")))
        XCTAssertEqual(rig.confirmer.requests, [ConfirmationRequest(
            title: ControlTools.restoreTitle("Claude", "Work"),
            message: ControlTools.restoreCollectionText("Work", time),
            code: ["= 2 unchanged"], footer: nil)])
        XCTAssertEqual(result, ControlResult(structured: .object([
            "id": .string(id), "collection": .string("Work"), "activeCollection": .string("Default"),
            "restartRequired": .bool(false), "applied": .bool(true),
        ]), text: ControlTools.restoredText("Work"), isError: false))
        XCTAssertEqual(try rig.h.storeOnDisk(), store)
        XCTAssertEqual(try rig.h.claudeServers(), claude)
        XCTAssertEqual(try backupFiles(rig), files, "nothing saved")
    }

    /// The collection Claude runs, already as the backup holds it, with a connector on: allowed, Claude's
    /// config is written again and the answer says to restart, as a restore that changed it would. The
    /// master list and its backups stay as they were.
    func testAnUnchangedActiveCollectionHasClaudesConfigWrittenAgain() throws {
        let rig = rig(); defer { rig.dispose() }
        let held = try XCTUnwrap(rig.state.store.collections["Default"])
        XCTAssertTrue(held.mcps.values.contains(where: \.enabled))
        let id = try backup(rig, of: rig.state.store)
        let store = try rig.h.storeOnDisk(), files = try collectionBackupFiles(rig)
        rig.h.claudeRunningSince(hours: 1)
        rig.h.settings.lastApplyDate = nil
        rig.state.refreshRestartState()
        XCTAssertFalse(rig.state.needsClaudeRestart)
        let result = try XCTUnwrap(rig.call("restore_collection", arguments(id, "Default")))
        XCTAssertEqual(rig.confirmer.requests.last?.code, [CollectionRestoreModel.unchangedLine(held.mcps.count)])
        XCTAssertEqual(rig.confirmer.requests.last?.footer, ControlTools.takesEffectFooter)
        XCTAssertEqual(result.text, ControlTools.restoredText("Default") + " " + ControlTools.restartToApplyText)
        XCTAssertEqual(result.structured.value(at: JSONPointer(["restartRequired"])), .bool(true))
        XCTAssertNotNil(rig.h.settings.lastApplyDate, "Claude's config was written again")
        XCTAssertEqual(try rig.h.storeOnDisk(), store)
        XCTAssertEqual(rig.state.store.collections["Default"], held)
        XCTAssertEqual(try collectionBackupFiles(rig), files, "nothing saved")
    }

    /// The collection Claude runs, already as the backup holds it, with no connector on: allowed, a write of
    /// Claude's config that failed earlier is tried again, as a restore that changed the collection would try
    /// it, and the answer says it reached Claude.
    func testAnUnchangedActiveCollectionTriesAFailedWriteAgain() throws {
        let rig = rig(); defer { rig.dispose() }
        let block = try WriteBlock(rig.h.claudeConfigURL)
        defer { block.dispose() }
        XCTAssertTrue(block.isEffective)
        for (name, entry) in try XCTUnwrap(rig.state.store.collections["Default"]).mcps where entry.enabled {
            rig.state.setEnabled(name, false)
        }
        XCTAssertTrue(rig.state.applyRetryNeeded)
        block.dispose()
        let held = try XCTUnwrap(rig.state.store.collections["Default"])
        let id = try backup(rig, of: rig.state.store)
        let result = try XCTUnwrap(rig.call("restore_collection", arguments(id, "Default")))
        XCTAssertEqual(rig.confirmer.requests.last?.code, [CollectionRestoreModel.unchangedLine(held.mcps.count)])
        XCTAssertEqual(result.text, ControlTools.restoredText("Default"))
        XCTAssertEqual(result.structured.value(at: JSONPointer(["applied"])), .bool(true))
        XCTAssertFalse(rig.state.applyRetryNeeded)
        XCTAssertEqual(Set(try rig.h.claudeServers().keys), [ClaudeIntegration.entryName], "Claude's config was written")
        XCTAssertEqual(rig.state.store.collections["Default"], held)
    }

    /// Claude can't see a value, so a right guess at one leaves the collection as the backup holds it and a
    /// wrong one doesn't: both restores get the same answer, restart and all, whether the connector holding
    /// the value is on or off, and whether the collection is the one Claude runs.
    func testAnUnchangedRestoreIsAnsweredAsOneThatChangesSomething() throws {
        for (collection, on) in [("Default", false), ("Default", true), ("Work", true)] {
            let rig = rig(); defer { rig.dispose() }
            if collection != "Default" { XCTAssertNil(rig.state.addEmptyCollection(named: collection)) }
            XCTAssertTrue(rig.state.store.collections["Default"]?.mcps.values.contains(where: \.enabled) == true)
            XCTAssertNil(rig.state.upsert(name: "vault", entry: vault(secret, on: on), renamedFrom: nil, in: collection))
            rig.state.applyInteractively()
            let id = try backup(rig, of: rig.state.store)
            rig.h.claudeRunningSince(hours: 1)
            rig.state.refreshRestartState()
            let right = try XCTUnwrap(rig.call("restore_collection", arguments(id, collection)))
            XCTAssertNil(rig.state.upsert(name: "vault", entry: vault("guess", on: on), renamedFrom: "vault", in: collection))
            rig.state.applyInteractively()
            let wrong = try XCTUnwrap(rig.call("restore_collection", arguments(id, collection)))
            let shown = "\(collection), \(on ? "on" : "off")"
            XCTAssertEqual(rig.state.store.collections[collection]?.mcps["vault"], vault(secret, on: on), shown)
            XCTAssertEqual(right, wrong, "\(shown): the same answer")
            XCTAssertEqual(right.text, collection == "Default" ? ControlTools.restoredText(collection) + " " + ControlTools.restartToApplyText
                                                               : ControlTools.restoredText(collection), shown)
        }
    }

    /// The guess end to end: Claude sets a variable to the value it already holds, which is asked and
    /// answered as a change, then restores the collection from the newest backup, which holds it as it is.
    /// The restore is asked about, not refused, so no refusal tells Claude it guessed right.
    func testAGuessThenARestoreIsAskedNotRefused() throws {
        let rig = rig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.upsert(name: "vault", entry: vault(secret), renamedFrom: nil))
        rig.state.applyInteractively()
        rig.h.claudeRunningSince(hours: 1)
        let set = try XCTUnwrap(rig.call("set_connector_env", ["name": .string("vault"), "set": .object(["API_KEY": .string(secret)])]))
        XCTAssertFalse(set.isError)
        let newest = try XCTUnwrap(try backups(rig).first).lastPathComponent
        let count = try XCTUnwrap(rig.state.store.collections["Default"]).mcps.count
        let result = try XCTUnwrap(rig.call("restore_collection", arguments(newest, "Default")))
        XCTAssertEqual(rig.confirmer.requests.count, 2)
        XCTAssertEqual(rig.confirmer.requests.last?.code, [CollectionRestoreModel.unchangedLine(count)])
        XCTAssertEqual(result.text, ControlTools.restoredText("Default") + " " + ControlTools.restartToApplyText)
        XCTAssertEqual(rig.state.store.collections["Default"]?.mcps["vault"], vault(secret))
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

    /// With confirmations off nobody is asked, so the restore is announced, naming who asked; restored
    /// again, with nothing to change, it is announced the same way.
    func testARestoreNobodyWasAskedAboutIsAnnounced() throws {
        let rig = ControlRig(confirm: false); defer { rig.dispose() }
        let (id, personal) = try deletedPersonal(rig)
        XCTAssertEqual(rig.call("restore_collection", arguments(id, "Personal"))?.text, ControlTools.restoredText("Personal"))
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [ControlTools.restoredNotification(ControlCaller.claudeName, "Personal")])
        XCTAssertEqual(rig.state.store.collections["Personal"], personal)
        XCTAssertEqual(rig.call("restore_collection", arguments(id, "Personal"))?.text, ControlTools.restoredText("Personal"))
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), Array(repeating: ControlTools.restoredNotification(ControlCaller.claudeName, "Personal"), count: 2))
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
