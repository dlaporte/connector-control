import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlToolsReadTests.cs
@MainActor
final class ControlToolsReadTests: XCTestCase {
    private let token = "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB"

    /// The token in its environment and, after a flag, in its arguments, where a summary of what it
    /// runs could show it.
    private var github: MCPEntry {
        MCPEntry(config: .object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("@modelcontextprotocol/server-github"), .string("--token"), .string(token)]),
            "env": .object(["GITHUB_PERSONAL_ACCESS_TOKEN": .string(token)]),
        ]))
    }

    func testStatusSaysWhatClaudeRunsAndWhetherItNeedsARestart() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.h.claudeRunningSince(hours: 1)
        rig.state.refreshRestartState()
        let result = try XCTUnwrap(rig.call("get_status"))
        XCTAssertEqual(result.text, [
            "Connector Control 1.2.2",
            "Active collection    Default · 3 connectors, 3 on",
            "Claude               running · restart required",
            "Claude integration   on · confirmations on",
            "Master list          \(rig.state.service.paths.masterStoreURL.path)",
        ].joined(separator: "\n"))
        XCTAssertFalse(result.isError)
        XCTAssertEqual(result.structured.value(at: JSONPointer(["claude", "restartRequired"])), .bool(true))
        XCTAssertEqual(result.structured.value(at: JSONPointer(["connectors"])), .int(3))
        XCTAssertEqual(result.structured.value(at: JSONPointer(["integration", "enabled"])), .bool(true))
        XCTAssertEqual(result.structured.value(at: JSONPointer(["problem"])), .null)
    }

    func testCheckToolsReportsEachLauncher() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.h.tools.statuses[.uvx] = .notFound
        let result = try XCTUnwrap(rig.call("check_tools"))
        XCTAssertEqual(result.text, [
            "npx    Found       /fake/bin/npx",
            "node   Found       /fake/bin/node",
            "uvx    Not found   \(ControlTools.installHint(ToolFamily.uv.linkTitle, ToolFamily.uv.installCommand))",
            "uv     Found       /fake/bin/uv",
        ].joined(separator: "\n"))
        XCTAssertEqual(result.structured.value(at: JSONPointer(["tools", "2", "status"])), .string("notFound"))
        XCTAssertEqual(result.structured.value(at: JSONPointer(["tools", "0", "path"])), .string("/fake/bin/npx"))
    }

    func testWaitingOnProbesRunsAtOnceWhenNoneIsInFlight() {
        let rig = ControlRig(); defer { rig.dispose() }
        var ran = false
        rig.state.whenToolsProbed { ran = true }
        XCTAssertTrue(ran)
    }

    func testListCollectionsMarksTheActiveOne() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
        let result = try XCTUnwrap(rig.call("list_collections"))
        XCTAssertEqual(result.text, [
            "         NAME      KIND    CONNECTORS",
            "active   Default   local   3 connectors, 3 on",
            "         Work      local   0 connectors, 0 on",
        ].joined(separator: "\n"))
        XCTAssertEqual(result.structured.value(at: JSONPointer(["active"])), .string("Default"))
        XCTAssertEqual(result.structured.value(at: JSONPointer(["collections", "1", "connectors"])), .int(0))
    }

    func testListConnectorsShowsWhatEachRunsAndNeverASecret() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.upsert(name: "github", entry: github, renamedFrom: nil))
        let result = try XCTUnwrap(rig.call("list_connectors"))
        XCTAssertEqual(result.text, [
            "     NAME          RUNS",
            "on   aws-mcp       aws-mcp.us-east-1.api.aws",
            "on   github        \(CollectionsModel.target(of: github.config))",
            "on   scoutbook     scoutbook.example.com",
            "on   service-now   snow.example.com",
        ].joined(separator: "\n"))
        XCTAssertFalse(result.text.contains("ghp_"))
        XCTAssertEqual(rig.call("list_connectors", ["collection": .string("Nope")]),
                       .refusal(MasterStore.noCollectionError("Nope")))
    }

    func testGetConnectorMasksSecretsAndOnlyTheCommandLineMayReveal() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.upsert(name: "github", entry: github, renamedFrom: nil))
        let shown = try XCTUnwrap(rig.call("get_connector", ["name": .string("github")]))
        XCTAssertEqual(shown.text, (["github · Default · on"] + ControlMasking.codeLines(github.config)).joined(separator: "\n"))
        XCTAssertEqual(shown.structured.value(at: JSONPointer(["config", "env", "GITHUB_PERSONAL_ACCESS_TOKEN"])), .string("set"))
        let asked = try XCTUnwrap(rig.call("get_connector", ["name": .string("github"), "reveal": .bool(true)]))
        XCTAssertFalse(asked.text.contains(token), "Claude never sees a secret, whatever it asks for")
        XCTAssertFalse(asked.structured.compactLine().contains(token))
        let revealed = try XCTUnwrap(rig.call("get_connector", ["name": .string("github"), "reveal": .bool(true)], as: .cli))
        XCTAssertTrue(revealed.text.contains(token))
        XCTAssertEqual(rig.call("get_connector", ["name": .string("nope")]),
                       .refusal(ControlTools.noConnectorError("nope", "Default")))
    }

    func testPendingUpdateRefusesALocalCollectionAndDescribesASubscribedOne() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertEqual(rig.call("get_pending_update", ["collection": .string("Default")]),
                       .refusal(ControlTools.notSubscribedError("Default")))
        let url = try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        XCTAssertEqual(rig.call("get_pending_update", ["collection": .string("Data team")])?.text,
                       ControlTools.upToDateText("Data team"))
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["github"] = nil
        try rig.h.writeDocument(doc, at: url)
        rig.state.refreshSource(for: "Data team")
        let pending = try XCTUnwrap(rig.call("get_pending_update", ["collection": .string("Data team")]))
        XCTAssertEqual(pending.text, ControlTools.pendingUpdateText("Data team", ControlTools.deletesClause("“github”")))
        XCTAssertEqual(pending.structured.value(at: JSONPointer(["removed"])), .array([.string("github")]))
    }

    func testListBackupsGivesEachIdTheRestoreTakes() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        // The backups the Restore sheet lists, as it finds them. Platform idiom: BackupManager names
        // the first-run original's URL here; the Windows mirror looks for it beside the others.
        let expected = try rig.state.service.backups.backups(series: RestoreModel.series)
            + [rig.state.service.backups.originalSnapshotURL(series: RestoreModel.series)].compactMap { $0 }
        XCTAssertFalse(expected.isEmpty, "the first load's apply backed Claude's config up")
        let result = try XCTUnwrap(rig.call("list_backups"))
        guard case .array(let items)? = result.structured.value(at: JSONPointer(["backups"])) else { return XCTFail() }
        XCTAssertEqual(items.map { $0.value(at: JSONPointer(["id"])) }, expected.map { .string($0.lastPathComponent) })
        XCTAssertTrue(result.text.hasPrefix("ID "))
        XCTAssertTrue(result.text.contains(expected[0].lastPathComponent))
    }

    func testSettingsListsEveryKeySetSettingTakes() {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertEqual(rig.call("get_settings")?.text, [
            "launch-at-login           off",
            "confirm-restart           on",
            "confirm-quit              on",
            "notify-external-changes   on",
            "automatic-updates         off",
            "backups-to-keep           20",
            "claude-integration        on",
            "confirm-claude-changes    on",
        ].joined(separator: "\n"))
    }

    /// Arguments for the read tools that need some: each names something that is there, so every
    /// read answers in full.
    private let readArguments: [String: [String: JSONValue]] = [
        "get_connector": ["name": .string("github")],
        "get_pending_update": ["collection": .string("Data team")],
    ]

    func testEveryReadToolLeavesTheStoreTheSettingsAndClaudesConfigByteIdentical() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.upsert(name: "github", entry: github, renamedFrom: nil))
        rig.state.applyInteractively()
        // A subscribed collection with an update waiting, so the pending-update read describes one.
        let url = try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["github"] = nil
        try rig.h.writeDocument(doc, at: url)
        rig.state.refreshSource(for: "Data team")
        rig.h.drain()
        XCTAssertNotNil(rig.state.pendingUpdates["Data team"])
        let files = [rig.h.masterStoreURL, rig.h.storeDir.appendingPathComponent(CollectionsFile.fileName),
                     rig.h.claudeConfigURL, rig.state.service.paths.collectionsCacheURL]
        func bytes() -> [Data?] { files.map { try? Data(contentsOf: $0) } }
        // Platform idiom: each side lists its own settings seam, which differ - the Mac keeps Claude's
        // app path, Windows its config path, launch target, tray tip and declined update.
        func settings() -> [String] {
            let s = rig.h.settings
            return [s.masterStoreDir ?? "", s.claudeAppPath ?? "", "\(s.backupKeepCount)", "\(s.notifyExternalChanges)",
                    "\(s.confirmBeforeRestart)", "\(s.confirmBeforeQuit)", "\(String(describing: s.lastApplyDate))",
                    "\(s.sweepVersion)", "\(s.claudeIntegrationEnabled)", "\(s.confirmClaudeChanges)",
                    "\(s.claudeIntegrationOfferAnswered)", "\(rig.autostart.enabled)",
                    "\(rig.updater.automaticallyDownloadsUpdates)"]
        }
        let filesBefore = bytes()
        let settingsBefore = settings()
        let backupsBefore = try rig.state.service.backups.backups(series: RestoreModel.series)
        let sentBefore = rig.h.notifier.sent.count
        for tool in ControlTools.catalog where tool.gate == .read {
            XCTAssertEqual(rig.call(tool.name, readArguments[tool.name] ?? [:])?.isError, false, tool.name)
        }
        XCTAssertEqual(bytes(), filesBefore)
        XCTAssertEqual(settings(), settingsBefore)
        XCTAssertEqual(try rig.state.service.backups.backups(series: RestoreModel.series), backupsBefore)
        XCTAssertEqual(rig.h.notifier.sent.count, sentBefore)
    }

    /// The names a read tool echoes in a sentence reach the command line's terminal, so they show as every
    /// other sentence shows a name: no control character goes out as itself, and a backslash is doubled.
    func testTheReadToolsShowTheNamesTheyEchoOneWayOnly() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        let name = "e\u{1B}[31m\\x"
        let shown = #"e\u{001B}[31m\\x"#
        XCTAssertEqual(rig.call("list_connectors", ["collection": .string(name)]), .refusal("No collection named “\(shown)”."))
        XCTAssertEqual(rig.call("get_connector", ["name": .string("x"), "collection": .string(name)]), .refusal("No collection named “\(shown)”."))
        XCTAssertEqual(rig.call("get_connector", ["name": .string(name)]), .refusal("There’s no connector named “\(shown)” in “Default”."))
        XCTAssertNil(rig.state.addEmptyCollection(named: name))
        XCTAssertEqual(rig.call("list_connectors", ["collection": .string(name)])?.text, "“\(shown)” has no connectors.")
        XCTAssertEqual(rig.call("get_connector", ["name": .string(name), "collection": .string(name)]),
                       .refusal("There’s no connector named “\(shown)” in “\(shown)”."))
    }

    /// The heading of a connector names it and its collection as every other sentence does.
    func testAConnectorsHeadingShowsItsNamesAsEscapes() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.upsert(name: "n\u{202E}x\\y", entry: github, renamedFrom: nil))
        let shown = try XCTUnwrap(rig.call("get_connector", ["name": .string("n\u{202E}x\\y")]))
        XCTAssertEqual(shown.text.components(separatedBy: "\n").first, #"n\u{202E}x\\y · Default · on"#)
    }

    /// The lists show a name one way only, as every sentence does: a backslash in it is doubled.
    func testTheListsShowNamesOneWayOnly() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.upsert(name: #"a\b"#, entry: github, renamedFrom: nil))
        XCTAssertNil(rig.state.addEmptyCollection(named: #"c\d"#))
        XCTAssertTrue(try XCTUnwrap(rig.call("list_connectors")?.text).contains(#"a\\b"#))
        XCTAssertTrue(try XCTUnwrap(rig.call("list_collections")?.text).contains(#"c\\d"#))
    }

    /// A variable that names a program to run shows in full only in the confirmation dialog; what
    /// Claude reads of it is that it is set.
    func testGetConnectorMasksAVariableThatRunsAProgram() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        let entry = MCPEntry(config: .object(["command": .string("git-mcp"), "env": .object(["GIT_ASKPASS": .string("/tmp/evil.sh")])]))
        XCTAssertNil(rig.state.upsert(name: "git", entry: entry, renamedFrom: nil))
        let shown = try XCTUnwrap(rig.call("get_connector", ["name": .string("git")]))
        XCTAssertFalse(shown.text.contains("/tmp/evil.sh"))
        XCTAssertFalse(shown.structured.compactLine().contains("evil"))
        XCTAssertEqual(shown.structured.value(at: JSONPointer(["config", "env", "GIT_ASKPASS"])), .string("set"))
    }
}
