import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlToolsDirectTests.cs
@MainActor
final class ControlToolsDirectTests: XCTestCase {
    func testTurningAConnectorOffWritesClaudesConfigAndAsksForARestart() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.h.claudeRunningSince(hours: 1)
        rig.state.refreshRestartState()
        let result = try XCTUnwrap(rig.call("set_connector_enabled", ["name": .string("aws-mcp"), "enabled": .bool(false)]))
        XCTAssertEqual(result.text, ControlTools.turnedOffText("aws-mcp", "Default") + " " + ControlTools.restartToApplyText)
        XCTAssertEqual(result.structured.value(at: JSONPointer(["restartRequired"])), .bool(true))
        XCTAssertNil(try rig.h.claudeServers()["aws-mcp"])
        XCTAssertEqual(rig.state.store.mcps["aws-mcp"]?.enabled, false)
        XCTAssertTrue(rig.confirmer.requests.isEmpty, "a switch is a direct change: nobody is asked")
        XCTAssertEqual(rig.h.notifier.sent, [FakeNotifier.Sent(
            title: Notifications.title, body: ControlTools.turnedOffNotification("Claude", "aws-mcp") + " " + ControlTools.restartToApplyText,
            category: Notifications.restartCategory)], "what Claude runs changed, so the user hears of it")
    }

    /// Claude's own switches change what Claude runs without anyone being asked, so the user hears
    /// of each one that does, whatever the confirmation setting: on, off, and switching the
    /// collection, the asker and the names as the gated tools' notifications show them.
    func testClaudesSwitchesThatChangeWhatItRunsAreAnnounced() {
        for confirm in [true, false] {
            let rig = ControlRig(confirm: confirm); defer { rig.dispose() }
            let work = "Work\n\u{202E}z"
            XCTAssertNil(rig.state.addEmptyCollection(named: work))
            XCTAssertEqual(rig.call("set_connector_enabled", ["name": .string("aws-mcp"), "enabled": .bool(false)])?.isError, false)
            XCTAssertEqual(rig.call("set_connector_enabled", ["name": .string("aws-mcp"), "enabled": .bool(true)],
                                    as: .relay(clientName: "Cursor"))?.isError, false)
            XCTAssertEqual(rig.call("switch_collection", ["collection": .string(work)])?.isError, false)
            XCTAssertEqual(rig.h.notifier.sent.map(\.body), [
                ControlTools.turnedOffNotification("Claude", "aws-mcp"),
                ControlTools.turnedOnNotification("Cursor", "aws-mcp"),
                ControlTools.switchedNotification("Claude", #"Work\n\u{202E}z"#),
            ], "confirm:\(confirm)")
            XCTAssertTrue(rig.confirmer.requests.isEmpty, "confirm:\(confirm)")
        }
    }

    /// A switch that leaves what Claude runs as it was is not announced: one already in place, and
    /// the command line's, which are the user's own.
    func testASwitchThatChangesNothingClaudeRunsOrComesFromTheCommandLineIsNotAnnounced() {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
        XCTAssertEqual(rig.call("set_connector_enabled", ["name": .string("aws-mcp"), "enabled": .bool(true)])?.isError, false)
        XCTAssertEqual(rig.call("switch_collection", ["collection": .string("Default")])?.isError, false)
        XCTAssertTrue(rig.h.notifier.sent.isEmpty, "nothing Claude runs changed")
        XCTAssertEqual(rig.call("set_connector_enabled", ["name": .string("aws-mcp"), "enabled": .bool(false)], as: .cli)?.isError, false)
        XCTAssertEqual(rig.call("switch_collection", ["collection": .string("Work")], as: .cli)?.isError, false)
        XCTAssertEqual(rig.state.activeCollection, "Work")
        XCTAssertTrue(rig.h.notifier.sent.isEmpty, "the command line's changes are the user's")
    }

    func testTurningOnNamesNoRestartWhileClaudeIsNotRunning() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.state.setEnabled("aws-mcp", false)
        XCTAssertEqual(rig.call("set_connector_enabled", ["name": .string("aws-mcp"), "enabled": .bool(true)])?.text,
                       ControlTools.turnedOnText("aws-mcp", "Default"))
    }

    func testOnAndOffActOnTheActiveCollectionOnly() {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
        XCTAssertNil(rig.state.upsert(name: "only-in-work", entry: MCPEntry(config: .object(["command": .string("x")])),
                                      renamedFrom: nil, in: "Work"))
        XCTAssertEqual(rig.call("set_connector_enabled", ["name": .string("only-in-work"), "enabled": .bool(false)]),
                       .refusal(ControlTools.noConnectorError("only-in-work", "Default")))
    }

    func testASubscribedCollectionsSwitchesStillWork() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        rig.state.switchCollection(to: "Data team")
        XCTAssertEqual(rig.call("set_connector_enabled", ["name": .string("github"), "enabled": .bool(true)])?.isError, false)
        XCTAssertEqual(rig.state.store.collections["Data team"]?.mcps["github"]?.enabled, true)
    }

    func testAFailedWriteSaysTheChangeWasSavedAndWhyClaudeDidNotGetIt() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        let block = try WriteBlock(rig.h.claudeConfigURL)
        defer { block.dispose() }
        XCTAssertTrue(block.isEffective)
        let result = try XCTUnwrap(rig.call("set_connector_enabled", ["name": .string("aws-mcp"), "enabled": .bool(false)]))
        XCTAssertFalse(result.isError, "the change is in the store")
        XCTAssertEqual(result.text, ControlTools.turnedOffText("aws-mcp", "Default") + "\n"
                       + ControlTools.savedNotAppliedText(try XCTUnwrap(rig.state.lastError)))
        XCTAssertEqual(result.structured.value(at: JSONPointer(["applied"])), .bool(false))
        XCTAssertTrue(rig.state.applyRetryNeeded)
        XCTAssertTrue(rig.h.notifier.sent.isEmpty, "what Claude runs did not change")
    }

    func testSwitchingCollections() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertEqual(rig.call("switch_collection", ["collection": .string("Nope")]), .refusal(MasterStore.noCollectionError("Nope")))
        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
        XCTAssertEqual(rig.call("switch_collection", ["collection": .string("Work")])?.text, ControlTools.switchedText("Work"))
        XCTAssertEqual(rig.state.activeCollection, "Work")
        XCTAssertEqual(Set(try rig.h.claudeServers().keys), [ClaudeIntegration.entryName],
                       "an empty collection leaves Claude only Connector Control's own entry")
    }

    func testCreatingRenamingAndDuplicatingCollections() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertEqual(rig.call("create_collection", ["name": .string(" Work ")])?.text, ControlTools.createdText("Work"))
        XCTAssertEqual(rig.call("create_collection", ["name": .string("Work")])?.isError, true, "the store's own refusal")
        XCTAssertEqual(rig.call("rename_collection", ["collection": .string("Work"), "newName": .string("Team")])?.text,
                       ControlTools.renamedCollectionText("Work", "Team"))
        XCTAssertEqual(rig.call("duplicate_collection", ["collection": .string("Default"), "newName": .string("Copy")])?.text,
                       ControlTools.duplicatedText("Default", "Copy"))
        XCTAssertEqual(rig.state.store.collections["Copy"], rig.state.store.collections["Default"])
        XCTAssertEqual(rig.state.activeCollection, "Default")
        XCTAssertEqual(rig.call("rename_collection", ["collection": .string("Nope"), "newName": .string("X")])?.isError, true)
    }

    func testASubscribedCollectionIsNotDuplicatedHere() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        XCTAssertEqual(rig.call("duplicate_collection", ["collection": .string("Data team"), "newName": .string("Mine")]),
                       .refusal(ControlTools.duplicateSubscribedError("Data team")))
        XCTAssertNil(rig.state.store.collections["Mine"])
    }

    func testRefreshingASubscription() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertEqual(rig.call("refresh_subscription", ["collection": .string("Default")]),
                       .refusal(ControlTools.notSubscribedError("Default")))
        let url = try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        XCTAssertEqual(rig.call("refresh_subscription", ["collection": .string("Data team")])?.text,
                       ControlTools.upToDateText("Data team"))
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["github"] = nil
        try rig.h.writeDocument(doc, at: url)
        XCTAssertEqual(rig.call("refresh_subscription", ["collection": .string("Data team")])?.text,
                       ControlTools.pendingUpdateText("Data team", ControlTools.deletesClause("“github”")))
        try Data("{half".utf8).write(to: url)
        let unreadable = try XCTUnwrap(rig.call("refresh_subscription", ["collection": .string("Data team")]))
        XCTAssertTrue(unreadable.isError)
        XCTAssertEqual(unreadable.text, try XCTUnwrap(rig.state.sourceErrors["Data team"]),
                       "an unreadable file is refused with the app's own sentence")
    }

    func testSettingsAreSetThroughTheSettingsModel() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertEqual(rig.call("set_setting", ["key": .string("confirm-quit"), "value": .string("off")])?.text,
                       ControlTools.settingChangedText("confirm-quit", "off"))
        XCTAssertFalse(rig.h.settings.confirmBeforeQuit)
        XCTAssertEqual(rig.call("set_setting", ["key": .string("launch-at-login"), "value": .string("ON")])?.isError, false)
        XCTAssertTrue(rig.autostart.enabled)
        XCTAssertEqual(rig.call("set_setting", ["key": .string("automatic-updates"), "value": .string("true")])?.isError, false)
        XCTAssertTrue(rig.settingsModel.autoUpdate)
        XCTAssertEqual(rig.call("set_setting", ["key": .string("backups-to-keep"), "value": .string("30")])?.text,
                       ControlTools.settingChangedText("backups-to-keep", "30"))
        XCTAssertEqual(rig.h.settings.backupKeepCount, 30)
        XCTAssertEqual(rig.state.service.backups.keepCount, 30, "the service is rebuilt, as the Settings stepper does")
        XCTAssertEqual(rig.call("set_setting", ["key": .string("backups-to-keep"), "value": .string("500")]),
                       .refusal(ControlTools.keepCountError))
        XCTAssertEqual(rig.call("set_setting", ["key": .string("confirm-quit"), "value": .string("maybe")]),
                       .refusal(ControlTools.switchValueError("confirm-quit")))
    }

    func testALoginItemThatCannotBeChangedIsRefusedWithTheAppsNote() {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.autostart.failWith = "denied"
        XCTAssertEqual(rig.call("set_setting", ["key": .string("launch-at-login"), "value": .string("on")]),
                       .refusal(SettingsModel.loginItemFailureNote("denied")))
    }

    func testOnlyTheUserChangesWhatClaudeIsAllowedToDo() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        for key in [ControlSettingKey.claudeIntegration, ControlSettingKey.confirmClaudeChanges] {
            XCTAssertEqual(rig.call("set_setting", ["key": .string(key), "value": .string("off")]),
                           .refusal(ControlTools.settingNotForClaudeError(key)))
        }
        XCTAssertTrue(rig.h.settings.claudeIntegrationEnabled)
        XCTAssertTrue(rig.h.settings.confirmClaudeChanges)
        XCTAssertEqual(rig.call("set_setting", ["key": .string("confirm-claude-changes"), "value": .string("off")], as: .cli)?.isError, false)
        XCTAssertFalse(rig.h.settings.confirmClaudeChanges)
        XCTAssertEqual(rig.call("set_setting", ["key": .string("claude-integration"), "value": .string("off")], as: .cli)?.text,
                       ControlTools.integrationSetText("off"))
        XCTAssertNil(try rig.h.claudeServers()[ClaudeIntegration.entryName], "turning it off from the command line applies")
    }

    func testTurningTheIntegrationOnFromTheCommandLineSaysToRestartClaude() {
        let rig = ControlRig(integration: false); defer { rig.dispose() }
        rig.h.claudeRunningSince(hours: 1)
        XCTAssertEqual(rig.call("set_setting", ["key": .string("claude-integration"), "value": .string("on")], as: .cli)?.text,
                       ControlTools.integrationSetText("on") + " " + ControlTools.restartToUseText)
    }

    func testTurningTheIntegrationOnFromTheCommandLineIsRefusedDuringAClash() throws {
        let rig = ControlRig(integration: false); defer { rig.dispose() }
        XCTAssertNil(rig.state.upsert(name: "mine", entry: MCPEntry(config: .object(["command": .string("x")])), renamedFrom: nil))
        try rig.h.editStoreOnDisk { store in
            let active = store.activeCollection
            let mine = store.collections[active]?.mcps["mine"]
            store.collections[active]?.mcps[ClaudeIntegration.entryName] = mine
        }
        rig.state.reload(trigger: .quietStoreAdoption)
        XCTAssertEqual(rig.call("set_setting", ["key": .string("claude-integration"), "value": .string("on")], as: .cli),
                       .refusal(ClaudeIntegration.clashNote))
    }

    private func settingsSnapshot(_ rig: ControlRig) -> String {
        let s = rig.h.settings
        return "\(s.backupKeepCount) \(s.notifyExternalChanges) \(s.confirmBeforeRestart) \(s.confirmBeforeQuit) "
            + "\(s.claudeIntegrationEnabled) \(s.confirmClaudeChanges) \(rig.autostart.enabled) \(rig.settingsModel.autoUpdate)"
    }

    func testTheKeepCountIsDigitsOnlyWithAsciiSpaceAndTabTrimmed() {
        let rig = ControlRig(); defer { rig.dispose() }
        for bad in ["+30", "-5", "30\u{00A0}", "\u{00A0}30", "3 0", ""] {
            XCTAssertEqual(rig.call("set_setting", ["key": .string("backups-to-keep"), "value": .string(bad)]),
                           .refusal(ControlTools.keepCountError), bad)
        }
        XCTAssertEqual(rig.h.settings.backupKeepCount, 20)
        XCTAssertEqual(rig.call("set_setting", ["key": .string("backups-to-keep"), "value": .string(" \t30\t ")])?.text,
                       ControlTools.settingChangedText("backups-to-keep", "30"))
        XCTAssertEqual(rig.h.settings.backupKeepCount, 30)
    }

    func testTheRelayCannotReachAKeyOutsideItsAllowlist() {
        let rig = ControlRig(); defer { rig.dispose() }
        let before = settingsSnapshot(rig)
        for key in ["Claude-Integration", " claude-integration", "confirm-claude-changes ", "master-list"] {
            let result = rig.call("set_setting", ["key": .string(key), "value": .string("off")])
            XCTAssertEqual(result?.isError, true, key)
        }
        XCTAssertEqual(settingsSnapshot(rig), before)
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
    }

    func testTheCommandLineIsRefusedAnUnknownKeyWithTheNotOneOfSentence() {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertEqual(rig.call("set_setting", ["key": .string("master-list"), "value": .string("off")], as: .cli),
                       .refusal(ControlTools.notOneOfError("key", ControlSettingKey.all.joined(separator: ", "))))
    }

    func testAnUnknownKeyIsRefusedWhenPreparedBeforeItsValueIsChecked() {
        let rig = ControlRig(); defer { rig.dispose() }
        for caller in [ControlCaller.cli, .relay(clientName: "claude-ai")] {
            guard case .refused(let reason) = rig.tools.prepare("set_setting",
                ControlArguments(values: ["key": .string("foo"), "value": .string("maybe")]), caller) else {
                return XCTFail("an unknown key must be refused in prepare")
            }
            XCTAssertEqual(reason, ControlTools.notOneOfError("key", ControlSettingKey.all.joined(separator: ", ")))
        }
    }

    func testAWriteThatChangesNothingClaudeRunsNamesNoRestart() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.h.claudeRunningSince(hours: 1)
        rig.state.refreshRestartState()
        let restart = JSONPointer(["restartRequired"])
        let quit = try XCTUnwrap(rig.call("set_setting", ["key": .string("confirm-quit"), "value": .string("off")]))
        XCTAssertEqual(quit.text, ControlTools.settingChangedText("confirm-quit", "off"))
        XCTAssertEqual(quit.structured.value(at: restart), .bool(false))
        let created = try XCTUnwrap(rig.call("create_collection", ["name": .string("Work")]))
        XCTAssertEqual(created.text, ControlTools.createdText("Work"))
        XCTAssertEqual(created.structured.value(at: restart), .bool(false))
        let already = try XCTUnwrap(rig.call("set_connector_enabled", ["name": .string("aws-mcp"), "enabled": .bool(true)]))
        XCTAssertEqual(already.text, ControlTools.turnedOnText("aws-mcp", "Default"))
        XCTAssertEqual(already.structured.value(at: restart), .bool(false))
    }

    func testAWriteThatChangesWhatClaudeRunsNamesTheRestart() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.h.claudeRunningSince(hours: 1)
        rig.state.refreshRestartState()
        let restart = JSONPointer(["restartRequired"]), applied = JSONPointer(["applied"])
        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
        let switched = try XCTUnwrap(rig.call("switch_collection", ["collection": .string("Work")]))
        XCTAssertEqual(switched.text, ControlTools.switchedText("Work") + " " + ControlTools.restartToApplyText)
        XCTAssertEqual(switched.structured.value(at: restart), .bool(true))
        XCTAssertEqual(switched.structured.value(at: applied), .bool(true))
        let renamed = try XCTUnwrap(rig.call("rename_collection", ["collection": .string("Work"), "newName": .string("Team")]))
        XCTAssertEqual(renamed.structured.value(at: applied), .bool(true))
        XCTAssertEqual(renamed.structured.value(at: restart), .bool(false), "the active collection's name is not in Claude's config")
        let copy = try XCTUnwrap(rig.call("duplicate_collection", ["collection": .string("Team"), "newName": .string("Copy")]))
        XCTAssertEqual(copy.text, ControlTools.duplicatedText("Team", "Copy"))
        XCTAssertEqual(copy.structured.value(at: restart), .bool(false))
        XCTAssertEqual(copy.structured.value(at: applied), .bool(true))
    }

    /// A write that moved what Claude's config holds only in how a character is composed has still
    /// moved it, as on Windows, so it names the restart while Claude runs.
    func testAMoveOnlyInHowACharacterIsComposedStillNamesTheRestart() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        let composed: JSONValue = .object(["command": .string("node"), "args": .array([.string("caf\u{E9}.js")])])
        let decomposed: JSONValue = .object(["command": .string("node"), "args": .array([.string("cafe\u{301}.js")])])
        XCTAssertNil(rig.state.upsert(name: "scoutbook", entry: MCPEntry(config: decomposed), renamedFrom: "scoutbook"))
        rig.state.applyInteractively()
        rig.h.claudeRunningSince(hours: 1)
        rig.state.refreshRestartState()
        var mark = rig.state.appliedServers
        mark["scoutbook"] = composed
        let outcome = rig.tools.done(["Did it."], [:], touching: "Default", since: mark, restart: ControlTools.restartToApplyText)
        XCTAssertEqual(outcome.result.text, "Did it. " + ControlTools.restartToApplyText)
        XCTAssertEqual(outcome.result.structured.value(at: JSONPointer(["restartRequired"])), .bool(true))
    }

    func testAStaleRetryFlagDoesNotColourAnUnrelatedSetting() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        do {
            let block = try WriteBlock(rig.h.claudeConfigURL)
            defer { block.dispose() }
            XCTAssertTrue(block.isEffective)
            rig.call("set_connector_enabled", ["name": .string("aws-mcp"), "enabled": .bool(false)])
        }
        XCTAssertTrue(rig.state.applyRetryNeeded)
        let result = try XCTUnwrap(rig.call("set_setting", ["key": .string("confirm-quit"), "value": .string("off")]))
        XCTAssertEqual(result.text, ControlTools.settingChangedText("confirm-quit", "off"))
        XCTAssertEqual(result.structured.value(at: JSONPointer(["applied"])), .bool(true))
        let keep = try XCTUnwrap(rig.call("set_setting", ["key": .string("backups-to-keep"), "value": .string("30")]))
        XCTAssertEqual(keep.text, ControlTools.settingChangedText("backups-to-keep", "30"))
    }

    // MARK: Names

    /// Nothing in `text` that a line would show as an escape: nothing that breaks a line, or
    /// reorders or hides text.
    private func assertPlain(_ text: String?, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertFalse((text ?? "").unicodeScalars.contains { ControlText.escape($0) != nil }, text ?? "nil", file: file, line: line)
    }

    /// A direct change's answer is also what announces it when it lands late, and it names
    /// connectors and collections as others chose them: Claude creates and renames collections
    /// without asking. Each name shows as an escape what would break a line or reorder or hide text,
    /// and so does the app's own refusal that echoes one.
    func testADirectChangeShowsNamesAsEscapes() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        let work = "Work\n\u{202E}z", shownWork = #"Work\n\u{202E}z"#
        let team = "Team\n\u{202E}y", shownTeam = #"Team\n\u{202E}y"#
        let copy = "Copy\u{2066}”", shownCopy = #"Copy\u{2066}\u{201D}"#
        let name = "a\nb\u{202E}c", shownName = #"a\nb\u{202E}c"#
        let data = "Data\n\u{202E}team", shownData = #"Data\n\u{202E}team"#
        var results = [
            rig.call("create_collection", ["name": .string(work)]),
            rig.call("rename_collection", ["collection": .string(work), "newName": .string(team)]),
            rig.call("duplicate_collection", ["collection": .string(team), "newName": .string(copy)]),
            rig.call("switch_collection", ["collection": .string(team)]),
        ]
        XCTAssertNil(rig.state.upsert(name: name, entry: MCPEntry(config: .object(["command": .string("x")])), renamedFrom: nil))
        results.append(rig.call("set_connector_enabled", ["name": .string(name), "enabled": .bool(false)]))
        results.append(rig.call("set_connector_enabled", ["name": .string("\u{202E}"), "enabled": .bool(false)]))
        results.append(rig.call("switch_collection", ["collection": .string("\u{202E}")]))
        results.append(rig.call("refresh_subscription", ["collection": .string(team)]))
        results.append(rig.call("rename_collection", ["collection": .string(copy), "newName": .string(team)]))
        try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam, as: data)
        results.append(rig.call("refresh_subscription", ["collection": .string(data)]))
        XCTAssertEqual(results.map { $0?.text }, [
            ControlTools.createdText(shownWork),
            ControlTools.renamedCollectionText(shownWork, shownTeam),
            ControlTools.duplicatedText(shownTeam, shownCopy),
            ControlTools.switchedText(shownTeam),
            ControlTools.turnedOffText(shownName, shownTeam),
            ControlTools.noConnectorError(#"\u{202E}"#, shownTeam),
            MasterStore.noCollectionError(#"\u{202E}"#),
            ControlTools.notSubscribedError(shownTeam),
            #"A collection named “Team\n\u{202E}y” already exists."#,
            ControlTools.upToDateText(shownData),
        ])
        for result in results { assertPlain(result?.text) }
        XCTAssertEqual(rig.state.store.collections[team]?.mcps[name]?.enabled, false, "each change was made as asked")
    }

    /// A direct change Claude asked for that lands after its early answer is announced in the
    /// same words, names escaped.
    func testALateDirectChangeIsAnnouncedWithNamesAsEscapes() {
        let rig = ControlRig(); defer { rig.dispose() }
        let name = "a\nb\u{202E}c"
        XCTAssertNil(rig.state.upsert(name: name, entry: MCPEntry(config: .object(["command": .string("x")])), renamedFrom: nil))
        rig.start("add_connector", ["name": .string("held"), "config": .object(["command": .string("node")])])
        let queued = rig.start("set_connector_enabled", ["name": .string(name), "enabled": .bool(false)])
        for _ in 1...20 where !rig.h.delays.pending.isEmpty { rig.h.delays.runNext() }
        XCTAssertEqual(queued.result, ControlTools.queuedResult)
        rig.confirmer.answer(false)
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [ControlTools.turnedOffText(#"a\nb\u{202E}c"#, "Default")])
    }

    /// A switch Claude asked for that lands after its early answer and moves what Claude runs is
    /// announced once, in its notification's words.
    func testALateSwitchThatChangesWhatClaudeRunsIsAnnouncedOnce() {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.start("add_connector", ["name": .string("held"), "config": .object(["command": .string("node")])])
        let queued = rig.start("set_connector_enabled", ["name": .string("aws-mcp"), "enabled": .bool(false)])
        for _ in 1...20 where !rig.h.delays.pending.isEmpty { rig.h.delays.runNext() }
        XCTAssertEqual(queued.result, ControlTools.queuedResult)
        rig.confirmer.answer(false)
        XCTAssertEqual(rig.state.store.mcps["aws-mcp"]?.enabled, false)
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [ControlTools.turnedOffNotification("Claude", "aws-mcp")])
        XCTAssertEqual(queued.completions, 1)
    }
}
