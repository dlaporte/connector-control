import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlConfirmationFlowTests.cs
///
/// The confirmation flow on the real gated tools: what the user's answer does, what the caller
/// hears, and what is announced when nobody was asked or the caller had already been answered.
@MainActor
final class ControlConfirmationFlowTests: XCTestCase {
    private let config: JSONValue = .object(["command": .string("node"), "args": .array([.string("server.js")])])

    private func add(_ name: String) -> [String: JSONValue] { ["name": .string(name), "config": config] }

    func testDecliningChangesNothing() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.confirmer.scripted = false
        let before = try Data(contentsOf: rig.h.claudeConfigURL)
        XCTAssertEqual(rig.call("add_connector", add("x")), .refusal(ControlTools.declinedError))
        XCTAssertNil(rig.state.store.mcps["x"])
        XCTAssertEqual(try Data(contentsOf: rig.h.claudeConfigURL), before)
        XCTAssertTrue(rig.h.notifier.sent.isEmpty)
    }

    func testWithConfirmationsOffAChangeAppliesAndIsAnnounced() throws {
        let rig = ControlRig(confirm: false); defer { rig.dispose() }
        rig.h.claudeRunningSince(hours: 1)
        XCTAssertEqual(rig.call("add_connector", add("x"))?.isError, false)
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
        XCTAssertEqual(rig.h.notifier.sent.map(\.body),
                       [ControlTools.addedNotification("Claude", "x", "Default") + " " + ControlTools.restartToUseText])
        XCTAssertEqual(rig.h.notifier.sent.last?.category, Notifications.restartCategory)
    }

    func testTheAnnouncementIgnoresNotifyOnExternalChanges() {
        let rig = ControlRig(confirm: false); defer { rig.dispose() }
        rig.h.settings.notifyExternalChanges = false
        XCTAssertEqual(rig.call("add_connector", add("x"))?.isError, false)
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [ControlTools.addedNotification("Claude", "x", "Default")])
    }

    /// A rename and nothing else is announced as a rename; a rename that also changes the config is
    /// announced as a change, under the new name.
    func testARenameIsAnnouncedAsARename() {
        let rig = ControlRig(confirm: false); defer { rig.dispose() }
        _ = rig.call("update_connector", ["name": .string("scoutbook"), "newName": .string("scouts")])
        _ = rig.call("update_connector", ["name": .string("scouts"), "newName": .string("scoutbook"), "config": config])
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [ControlTools.renamedNotification("Claude", "scoutbook", "scouts"),
                                                         ControlTools.changedNotification("Claude", "scoutbook", "Default")])
    }

    /// An announcement shows names as the question would: what would break a line or reorder or
    /// hide text shows as an escape.
    func testAnAnnouncementShowsNamesAsEscapes() {
        let rig = ControlRig(confirm: false); defer { rig.dispose() }
        let collection = "Work\n\u{202E}z", shownCollection = #"Work\n\u{202E}z"#
        let caller = ControlCaller.relay(clientName: "Cursor\n\u{202E}d"), shownAsker = #"Cursor\n\u{202E}d"#
        XCTAssertNil(rig.state.addEmptyCollection(named: collection))
        let target: [String: JSONValue] = ["collection": .string(collection)]
        _ = rig.call("add_connector", target.merging(["name": .string("a\nb"), "config": config]) { $1 }, as: caller)
        _ = rig.call("update_connector", target.merging(["name": .string("a\nb"), "newName": .string("c\u{202E}")]) { $1 }, as: caller)
        _ = rig.call("delete_connectors", target.merging(["names": .array([.string("c\u{202E}")])]) { $1 }, as: caller)
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [
            ControlTools.addedNotification(shownAsker, #"a\nb"#, shownCollection),
            ControlTools.renamedNotification(shownAsker, #"a\nb"#, #"c\u{202E}"#),
            ControlTools.deletedNotification(shownAsker, #"“c\u{202E}”"#, shownCollection),
        ])
    }

    func testTheCommandLineIsNeverAskedAndNeverAnnounced() {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertEqual(rig.call("add_connector", add("x"), as: .cli)?.isError, false)
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
        XCTAssertTrue(rig.h.notifier.sent.isEmpty)
    }

    func testAnotherClientIsNamedInTheQuestion() {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.confirmer.scripted = true
        _ = rig.call("add_connector", add("x"), as: .relay(clientName: "Cursor"))
        XCTAssertEqual(rig.confirmer.requests.last?.title, ControlTools.addTitle("Cursor", "x"))
    }

    func testAWaitingCallReportsProgressThenSaysItIsWaitingAndALateAllowStillApplies() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        let answer = rig.start("add_connector", add("x"))
        XCTAssertNil(answer.result)
        XCTAssertEqual(rig.confirmer.waitingCount, 1)
        for _ in 1..<10 { rig.h.delays.runNext() }
        XCTAssertEqual(answer.progress, [5, 10, 15, 20, 25, 30, 35, 40, 45])
        XCTAssertNil(answer.result)
        rig.h.delays.runNext()
        XCTAssertEqual(answer.progress.last, 50)
        XCTAssertEqual(answer.result, ControlResult(structured: .object(["status": .string("waiting")]),
                                                   text: ControlTools.waitingText, isError: false))
        XCTAssertTrue(rig.h.delays.pending.isEmpty, "the ticking stops once the caller has its answer")
        rig.confirmer.answer(true)
        XCTAssertNotNil(rig.state.store.mcps["x"], "the dialog stayed, and the answer still counts")
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [ControlTools.addedNotification("Claude", "x", "Default")],
                       "announced once, since the caller had already been answered")
        XCTAssertEqual(answer.result?.text, ControlTools.waitingText, "the caller hears once")
        XCTAssertEqual(answer.completions, 1)
    }

    func testALateDeclineAppliesNothingAndSaysNothing() {
        let rig = ControlRig(); defer { rig.dispose() }
        let answer = rig.start("add_connector", add("x"))
        for _ in 1...10 { rig.h.delays.runNext() }
        XCTAssertEqual(answer.result?.text, ControlTools.waitingText)
        rig.confirmer.answer(false)
        XCTAssertNil(rig.state.store.mcps["x"])
        XCTAssertTrue(rig.h.notifier.sent.isEmpty)
        XCTAssertEqual(answer.completions, 1)
    }

    func testAnAnswerBeforeTheWaitRunsOutIsTheAnswer() {
        let rig = ControlRig(); defer { rig.dispose() }
        let answer = rig.start("add_connector", add("x"))
        rig.h.delays.runNext()
        rig.confirmer.answer(true)
        XCTAssertEqual(answer.result?.text, ControlTools.addedText("x", "Default", ControlTools.onWord))
        XCTAssertEqual(answer.progress, [5])
        rig.h.delays.runNext()
        XCTAssertTrue(rig.h.delays.pending.isEmpty)
        XCTAssertEqual(answer.progress, [5], "a tick after the answer reports nothing")
        XCTAssertTrue(rig.h.notifier.sent.isEmpty, "the caller heard the outcome itself")
    }

    /// The app's dialog is modal: `confirm` returns only once the user has answered, while its run
    /// loop runs what the host delays. The wait starts before the question is put, so the caller
    /// still hears progress and, at `confirmationWait`, that the answer is awaited; the Allow that
    /// follows applies and is announced once.
    func testAModalDialogStillLetsTheCallerHearItIsWaiting() {
        let rig = ControlRig(); defer { rig.dispose() }
        let delays = rig.h.delays
        rig.confirmer.scripted = true
        rig.confirmer.whileOpen = { while !delays.pending.isEmpty { delays.runNext() } }
        let answer = rig.start("add_connector", add("x"))
        XCTAssertEqual(answer.progress, [5, 10, 15, 20, 25, 30, 35, 40, 45, 50])
        XCTAssertEqual(answer.result, ControlTools.waitingResult)
        XCTAssertEqual(answer.completions, 1)
        XCTAssertNotNil(rig.state.store.mcps["x"], "the Allow, given after the early answer, still counts")
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [ControlTools.addedNotification("Claude", "x", "Default")],
                       "announced once, since the caller had already been answered")
        XCTAssertTrue(rig.h.delays.pending.isEmpty)
    }

    func testWritesRunOneAtATimeAndReadsDoNotWait() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        let first = rig.start("add_connector", add("a"))
        let second = rig.start("add_connector", add("b"))
        XCTAssertEqual(rig.confirmer.requests.count, 1, "the second question waits behind the first")
        XCTAssertNotNil(rig.call("get_status"), "a read answers at once")
        let direct = rig.start("set_connector_enabled", ["name": .string("aws-mcp"), "enabled": .bool(false)])
        XCTAssertNil(direct.result, "a direct change waits its turn too")
        rig.confirmer.answer(true)
        XCTAssertEqual(first.result?.isError, false)
        XCTAssertEqual(rig.confirmer.requests.count, 2)
        XCTAssertNil(second.result)
        rig.confirmer.answer(true)
        XCTAssertEqual(second.result?.isError, false)
        XCTAssertEqual(direct.result?.isError, false)
        XCTAssertTrue(Set(["a", "b"]).isSubset(of: Set(try rig.h.claudeServers().keys)))
        XCTAssertNil(try rig.h.claudeServers()["aws-mcp"])
    }

    /// A call queued behind an open dialog has asked nobody anything, so its early answer says it
    /// is waiting its turn rather than for an answer; its turn still comes.
    func testAQueuedCallHearsItIsWaitingTooAndStillRunsInTurn() {
        let rig = ControlRig(); defer { rig.dispose() }
        _ = rig.start("add_connector", add("a"))
        let queued = rig.start("add_connector", add("b"))
        for _ in 1...20 { if !rig.h.delays.pending.isEmpty { rig.h.delays.runNext() } }
        XCTAssertEqual(queued.result, ControlTools.queuedResult)
        rig.confirmer.answer(true)
        rig.confirmer.answer(true)
        XCTAssertNotNil(rig.state.store.mcps["b"])
        XCTAssertEqual(queued.completions, 1)
    }

    func testAChangeThatMovedWhileAskingIsNotApplied() {
        let rig = ControlRig(); defer { rig.dispose() }
        let answer = rig.start("add_connector", add("x"))
        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
        rig.state.switchCollection(to: "Work")
        rig.confirmer.answer(true)
        XCTAssertEqual(answer.result, .refusal(ControlTools.changedWhileAskingError),
                       "the question named Default as the collection Claude runs; it no longer is")
        XCTAssertNil(rig.state.store.collections["Default"]?.mcps["x"])
    }

    func testAConnectorThatChangedWhileAskingIsLeftAsTheUserMadeIt() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        let answer = rig.start("update_connector", ["name": .string("scoutbook"), "config": config])
        let theirs = AppStateHarness.remote("https://scoutbook.example.com/v3/mcp")
        let entry = try XCTUnwrap(rig.state.store.mcps["scoutbook"])
        XCTAssertNil(rig.state.upsert(name: "scoutbook", entry: MCPEntry(enabled: entry.enabled, config: theirs),
                                      renamedFrom: "scoutbook"))
        rig.confirmer.answer(true)
        XCTAssertEqual(answer.result, .refusal(ControlTools.changedWhileAskingError),
                       "the question showed a change from a config the connector no longer has")
        XCTAssertEqual(rig.state.store.mcps["scoutbook"]?.config, theirs)
        XCTAssertTrue(rig.h.notifier.sent.isEmpty)
    }

    /// The question asked again is compared with the one the user answered scalar by scalar, as on
    /// Windows: a connector that changed only in how a character is composed while the dialog was
    /// open has changed, and nothing is done.
    func testAChangeWhileAskingOnlyInHowACharacterIsComposedStillCounts() {
        let rig = ControlRig(); defer { rig.dispose() }
        let composed: JSONValue = .object(["command": .string("node"), "args": .array([.string("caf\u{E9}.js")])])
        let decomposed: JSONValue = .object(["command": .string("node"), "args": .array([.string("cafe\u{301}.js")])])
        XCTAssertNil(rig.state.upsert(name: "scoutbook", entry: MCPEntry(config: composed), renamedFrom: "scoutbook"))
        let answer = rig.start("update_connector", ["name": .string("scoutbook"), "config": config])
        XCTAssertNil(rig.state.upsert(name: "scoutbook", entry: MCPEntry(config: decomposed), renamedFrom: "scoutbook"))
        rig.confirmer.answer(true)
        XCTAssertEqual(answer.result, .refusal(ControlTools.changedWhileAskingError))
        XCTAssertNotEqual(rig.state.store.mcps["scoutbook"]?.config, config, "nothing was done")
    }

    func testARefusalThatAroseWhileAskingWins() {
        let rig = ControlRig(); defer { rig.dispose() }
        let answer = rig.start("add_connector", add("x"))
        XCTAssertNil(rig.state.upsert(name: "x", entry: MCPEntry(config: config), renamedFrom: nil))
        rig.confirmer.answer(true)
        XCTAssertEqual(answer.result, .refusal(AppState.duplicateNameError("x")))
    }

    func testAWriteQueuedWhenTheIntegrationGoesOffIsRefused() {
        let rig = ControlRig(); defer { rig.dispose() }
        _ = rig.start("add_connector", add("a"))
        let queued = rig.start("add_connector", add("b"))
        rig.state.setClaudeIntegrationEnabled(false)
        rig.confirmer.answer(true)
        XCTAssertEqual(queued.result, .refusal(ControlTools.integrationOffError))
        XCTAssertEqual(rig.confirmer.requests.count, 1)
        XCTAssertNil(rig.state.store.mcps["b"])
    }

    func testAnEditorOpenOnTheConnectorStillCatchesTheChange() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.confirmer.scripted = true
        let entry = try XCTUnwrap(rig.state.store.mcps["scoutbook"])
        let editor = EditorModel(state: rig.state, target: .existing(name: "scoutbook", entry: entry, in: "Default"),
                                 dialogs: rig.h.dialogs)
        defer { editor.dispose() }
        _ = rig.call("update_connector", ["name": .string("scoutbook"), "config": config])
        rig.h.dialogs.nextConfirm = false
        XCTAssertFalse(editor.save(), "the editor's own changed-outside check still guards it")
        XCTAssertEqual(rig.h.dialogs.confirms.last?.message, EditorModel.changedOutsideMessage("scoutbook"))
        XCTAssertEqual(rig.state.store.mcps["scoutbook"]?.config, config, "Claude's change stands")
    }

    /// The confirmer of a host that can show no dialog answers every question Don't Allow at once:
    /// the relay's gated change is refused and nothing changes, while the command line, never asked,
    /// still runs.
    func testTheDecliningConfirmerRefusesEveryQuestion() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        let tools = ControlTools(state: rig.state, settings: rig.settingsModel, confirmer: DecliningConfirmer(), host: rig.h.host)
        var relay: ControlResult?
        tools.call(name: "delete_connectors", arguments: .object(["names": .array([.string("aws-mcp")])]),
                   caller: .relay(clientName: "claude-ai"), progress: { _ in }, completion: { relay = $0 })
        rig.h.drain()
        XCTAssertEqual(relay, .refusal(ControlTools.declinedError))
        XCTAssertNotNil(rig.state.store.mcps["aws-mcp"])
        var cli: ControlResult?
        tools.call(name: "delete_connectors", arguments: .object(["names": .array([.string("aws-mcp")])]),
                   caller: .cli, progress: { _ in }, completion: { cli = $0 })
        rig.h.drain()
        XCTAssertEqual(cli?.isError, false)
        XCTAssertNil(rig.state.store.mcps["aws-mcp"])
    }
}
