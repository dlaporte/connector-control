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
        XCTAssertTrue(rig.h.notifier.sent.isEmpty)
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
                       AppState.collectionUpdateBanner("Data team", "deletes github"))
        try Data("{half".utf8).write(to: url)
        XCTAssertEqual(rig.call("refresh_subscription", ["collection": .string("Data team")])?.isError, true,
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
}
