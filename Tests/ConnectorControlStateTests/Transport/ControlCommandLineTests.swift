import XCTest
import ConnectorControlCore
@testable import ConnectorControlState
@testable import ConnectorControlTransport

/// Mirror: windows/tests/ConnectorControl.Core.Tests/Transport/ControlCommandLineTests.cs
@MainActor
final class ControlCommandLineTests: XCTestCase {
    // Platform-forced differences: the Windows mirror's working directory is a Windows path and its
    // expected paths are built with Path.GetFullPath against it; its missing file throws a
    // FileNotFoundException where this one throws a CocoaError, each read through AppState.friendly.
    private let cwd = "/Users/me/work"
    private var files: [String: Data] = [:]

    private func parse(_ words: String...) -> ControlInvocation {
        ControlCommandLine.parse(words, workingDirectory: cwd, readFile: { [files] path in
            guard let data = files[path] else { throw CocoaError(.fileReadNoSuchFile) }
            return data
        })
    }

    private func call(_ tool: String, _ arguments: [String: JSONValue] = [:], json: Bool = false) -> ControlInvocation {
        .call(tool: tool, arguments: .object(arguments), json: json)
    }

    func testEveryCommandRunsItsToolAndEveryToolHasACommand() {
        let id = "claude_desktop_config.2026-10-28T12-00-00.000Z.json"
        let table: [([String], ControlInvocation)] = [
            (["status"], call("get_status")),
            (["doctor"], call("check_tools")),
            (["connectors"], call("list_connectors")),
            (["connectors", "--collection", "Team"], call("list_connectors", ["collection": .string("Team")])),
            (["show", "github"], call("get_connector", ["name": .string("github")])),
            (["show", "github", "--reveal", "--collection", "Team"],
             call("get_connector", ["name": .string("github"), "reveal": .bool(true), "collection": .string("Team")])),
            (["enable", "github"], call("set_connector_enabled", ["name": .string("github"), "enabled": .bool(true)])),
            (["disable", "github"], call("set_connector_enabled", ["name": .string("github"), "enabled": .bool(false)])),
            (["add", "gh", "--json", "{}"], call("add_connector", ["name": .string("gh"), "config": .object([:])])),
            (["add", "gh", "--json", "{}", "--off"],
             call("add_connector", ["name": .string("gh"), "config": .object([:]), "enabled": .bool(false)])),
            (["edit", "gh", "--rename", "hub"], call("update_connector", ["name": .string("gh"), "newName": .string("hub")])),
            (["delete", "a", "b", "--collection", "Team"],
             call("delete_connectors", ["names": .array([.string("a"), .string("b")]), "collection": .string("Team")])),
            (["collections"], call("list_collections")),
            (["use", "Team"], call("switch_collection", ["collection": .string("Team")])),
            (["collection", "new", "Team"], call("create_collection", ["name": .string("Team")])),
            (["collection", "rename", "Team", "Crew"], call("rename_collection", ["collection": .string("Team"), "newName": .string("Crew")])),
            (["collection", "duplicate", "Team", "Copy"],
             call("duplicate_collection", ["collection": .string("Team"), "newName": .string("Copy")])),
            (["collection", "delete", "Team"], call("delete_collection", ["collection": .string("Team")])),
            (["import", "team.json", "--into", "Team", "--on-clash", "replace"],
             call("import_document", ["path": .string("/Users/me/work/team.json"), "into": .string("Team"), "onClash": .string("replace")])),
            (["import", "/x/team.json", "--new", "Crew"], call("import_document", ["path": .string("/x/team.json"), "newCollection": .string("Crew")])),
            (["subscribe", "../team.json", "--name", "Team"], call("subscribe", ["path": .string("/Users/me/team.json"), "name": .string("Team")])),
            (["updates", "Team"], call("get_pending_update", ["collection": .string("Team")])),
            (["apply-update", "Team"], call("apply_update", ["collection": .string("Team")])),
            (["refresh", "Team"], call("refresh_subscription", ["collection": .string("Team")])),
            (["settings"], call("get_settings")),
            (["settings", "set", "backups-to-keep", "30"], call("set_setting", ["key": .string("backups-to-keep"), "value": .string("30")])),
            (["backups"], call("list_backups")),
            (["backups", "restore", id], call("restore_backup", ["id": .string(id)])),
            (["restart-claude"], call("restart_claude")),
        ]
        var tools: Set<String> = []
        for (words, expected) in table {
            let parsed = ControlCommandLine.parse(words, workingDirectory: cwd, readFile: { _ in Data() })
            XCTAssertEqual(parsed, expected, words.joined(separator: " "))
            if case .call(let tool, _, _) = parsed { tools.insert(tool) }
        }
        XCTAssertEqual(tools, Set(ControlTools.catalog.map(\.name)), "every tool has a command")
    }

    func testJSONAnywhereAsksForTheStructure() {
        XCTAssertEqual(parse("status", "--json"), call("get_status", json: true))
        XCTAssertEqual(parse("--json", "status"), call("get_status", json: true))
        XCTAssertEqual(parse("connectors", "--json", "--collection", "Team"), call("list_connectors", ["collection": .string("Team")], json: true))
    }

    func testAddAndEditTakeTheirConfigAfterJSONOrFromAFile() {
        files["/Users/me/work/github.json"] = Data(#"{"command":"npx","args":["-y","x"]}"#.utf8)
        let config: JSONValue = .object(["command": .string("npx"), "args": .array([.string("-y"), .string("x")])])
        let usage = ControlCommandLine.usageError("add <name> --json <config> | --file <path> [--off]")
        XCTAssertEqual(parse("add", "github", "--json", #"{"command":"npx","args":["-y","x"]}"#),
                       call("add_connector", ["name": .string("github"), "config": config]))
        XCTAssertEqual(parse("add", "github", "--json", #"  {"command":"npx","args":["-y","x"]}"#, "--json"),
                       call("add_connector", ["name": .string("github"), "config": config], json: true), "the second --json is the output flag")
        XCTAssertEqual(parse("add", "github", "--file", "github.json", "--json"),
                       call("add_connector", ["name": .string("github"), "config": config], json: true), "--json not followed by an object")
        XCTAssertEqual(parse("edit", "github", "--file", "github.json", "--rename", "gh"),
                       call("update_connector", ["name": .string("github"), "config": config, "newName": .string("gh")]))
        XCTAssertEqual(parse("add", "github"), .usage(usage), "add needs a config")
        XCTAssertEqual(parse("add", "github", "--json", "{}", "--file", "github.json"), .usage(usage), "not both")
        XCTAssertEqual(parse("add", "github", "--json", "{not json"), .usage(EditorModel.notValidJSON))
        XCTAssertEqual(parse("add", "github", "--file", "missing.json"),
                       .usage(AppState.sourceUnreadableError("missing.json", AppState.friendly(CocoaError(.fileReadNoSuchFile)))))
    }

    func testAReadErrorOfAnyKindNamesTheFileAndAFolderIsNamedWithoutItsSlash() {
        struct Odd: Error {}
        let odd = ControlCommandLine.parse(["add", "github", "--file", "odd.json"], workingDirectory: cwd, readFile: { _ in throw Odd() })
        XCTAssertEqual(odd, .usage(AppState.sourceUnreadableError("odd.json", AppState.friendly(Odd()))))
        XCTAssertEqual(parse("add", "github", "--file", "sub/"),
                       .usage(AppState.sourceUnreadableError("sub", AppState.friendly(CocoaError(.fileReadNoSuchFile)))))
    }

    func testHelpVersionAndTheRelay() {
        XCTAssertEqual(parse("help"), .help)
        XCTAssertEqual(parse("--help"), .help)
        XCTAssertEqual(parse("-h"), .help)
        XCTAssertEqual(parse("--version"), .version)
        XCTAssertEqual(parse("--json", "--version"), .version)
        XCTAssertEqual(parse("--version", "--json"), .version)
        XCTAssertEqual(parse("mcp"), .relay)
        XCTAssertEqual(ControlCommandLine.parse([], workingDirectory: cwd, readFile: { _ in Data() }), .usage(ControlTools.helpText()))
        XCTAssertEqual(ControlCommandLine.versionLine("1.5.0"), "connector-control 1.5.0")
    }

    func testWhatTheToolCannotRunIsAUsageError() {
        XCTAssertEqual(parse("frobnicate"), .usage(ControlCommandLine.unknownCommandError("frobnicate")))
        XCTAssertEqual(parse("collection", "frob"), .usage(ControlCommandLine.unknownCommandError("collection frob")))
        XCTAssertEqual(parse("collection"), .usage(ControlCommandLine.unknownCommandError("collection")))
        XCTAssertEqual(parse("show"), .usage(ControlCommandLine.usageError("show <name> [--reveal]")))
        XCTAssertEqual(parse("status", "extra"), .usage(ControlCommandLine.usageError("status")))
        XCTAssertEqual(parse("status", "--reveal"), .usage(ControlCommandLine.unknownOptionError("--reveal")))
        XCTAssertEqual(parse("connectors", "--collection"), .usage(ControlCommandLine.missingValueError("--collection")))
        XCTAssertEqual(parse("connectors", "--\u{301}x"), .usage(ControlCommandLine.unknownOptionError("--\u{301}x")),
                       "a combining mark after the dashes does not make the word a name")
        XCTAssertEqual(parse("mcp", "extra"), .usage(ControlCommandLine.unknownOptionError("extra")))
        XCTAssertEqual(parse("help", "status"), .usage(ControlCommandLine.unknownOptionError("status")))
    }

    func testAfterTwoDashesEveryWordIsAName() {
        XCTAssertEqual(parse("delete", "--", "--odd", "plain"),
                       call("delete_connectors", ["names": .array([.string("--odd"), .string("plain")])]))
    }
}
