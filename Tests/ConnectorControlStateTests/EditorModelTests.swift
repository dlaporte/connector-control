import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/EditorModelTests.cs. The
/// sheet-style confirmations are asserted as pending state plus the method
/// the sheet's button calls; the save-conflict alert still goes through
/// FakeDialogs. Theory rows are looped with a fresh editor per row.
@MainActor
final class EditorModelTests: XCTestCase {
    private let url = "https://scoutbook.example.com/mcp"

    private func local(_ command: String, _ args: [String],
                       env: [(String, String)] = [], extra: [(String, JSONValue)] = []) -> JSONValue {
        var object: [String: JSONValue] = ["command": .string(command), "args": .array(args.map(JSONValue.string))]
        if !env.isEmpty {
            object["env"] = .object(Dictionary(uniqueKeysWithValues: env.map { ($0.0, JSONValue.string($0.1)) }))
        }
        for (key, value) in extra { object[key] = value }
        return .object(object)
    }

    private func editor(_ h: AppStateHarness, _ state: AppState, _ target: EditTarget) -> EditorModel {
        EditorModel(state: state, target: target, dialogs: h.dialogs)
    }

    // MARK: opening (catalog §3.3)

    func testNewRemoteTargetOpensInTheRemoteFormWithAnEmptyUrl() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .newRemote())
        XCTAssertEqual(editor.windowTitle, "Add Connector")
        XCTAssertEqual(editor.view, .form)
        XCTAssertTrue(editor.showTypePicker)
        XCTAssertTrue(editor.isRemote)
        XCTAssertEqual(editor.remoteURL, "")
        XCTAssertFalse(editor.showURLHint)   // hint only once something invalid is typed
        XCTAssertFalse(editor.canSave)
        XCTAssertFalse(editor.canRemove)
        XCTAssertEqual(editor.authKind, .automatic)
    }

    func testExistingBareRemoteOpensInTheRemoteForm() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .existing(name: "scoutbook", entry: MCPEntry(config: local("npx", ["-y", "mcp-remote", url]))))
        XCTAssertEqual(editor.windowTitle, "Edit “scoutbook”")
        XCTAssertFalse(editor.showTypePicker)
        XCTAssertTrue(editor.isRemote)
        XCTAssertEqual(editor.remoteURL, url)
        XCTAssertTrue(editor.canSave)
        XCTAssertTrue(editor.canRemove)
    }

    func testExistingRemoteWithAuthFlagsOpensInTheLocalForm() throws {
        // Catalog §3.3: detect() requires exactly two stripped args, so auth flags push the
        // connector into the Local form even though decode() populated the auth fields.
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let config = RemotePattern.encode(RemoteConfig(url: url, auth: .bearer(token: "tok")))
        let editor = editor(h, state, .existing(name: "scoutbook", entry: MCPEntry(config: config)))
        XCTAssertFalse(editor.isRemote)
        XCTAssertEqual(editor.command, "npx")
        XCTAssertEqual(editor.args.map(\.value), ["-y", "mcp-remote", url, "--header", "Authorization:${AUTH_HEADER}"])
        XCTAssertEqual(editor.envRows.count, 1)
        let row = try XCTUnwrap(editor.envRows.first)
        XCTAssertEqual(row.name, "AUTH_HEADER")
        XCTAssertEqual(row.value, "Bearer tok")
        XCTAssertFalse(row.revealed)
        XCTAssertEqual(editor.authKind, .bearer)
        XCTAssertEqual(editor.bearerToken, "tok")
    }

    func testExistingLocalOpensWithArgsEnvAndAdditionalFields() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let config = local("node", ["server.js", "--port", "3000"], env: [("TOKEN", "s3cret"), ("A", "1")],
                           extra: [("disabled", .bool(false)), ("type", .string("stdio"))])
        let editor = editor(h, state, .existing(name: "local", entry: MCPEntry(enabled: false, config: config, lastEditView: .json)))
        XCTAssertEqual(editor.view, .json)   // reopens in the view last used to save
        editor.requestView(.form)
        XCTAssertFalse(editor.isRemote)
        XCTAssertEqual(editor.command, "node")
        XCTAssertEqual(editor.args.map(\.value), ["server.js", "--port", "3000"])
        XCTAssertEqual(editor.envRows.map(\.name), ["A", "TOKEN"])   // sorted by key
        XCTAssertTrue(editor.hasAdditional)
        XCTAssertEqual(editor.additionalTitle, "2 field(s) not editable here: disabled, type — switch to JSON to edit")
        XCTAssertEqual(editor.additionalPreview, "{\n  \"disabled\" : false,\n  \"type\" : \"stdio\"\n}")
    }

    func testSwitchingANewTargetToLocalResetsTheBridgeInvocation() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .newRemote())
        editor.remoteURL = url
        editor.isRemote = false
        XCTAssertEqual(editor.command, "npx")
        XCTAssertEqual(editor.args.map(\.value), ["-y", ""])
        editor.isRemote = true
        XCTAssertEqual(editor.remoteURL, url)   // switching back changes nothing
    }

    func testSwitchingAnExistingTargetToLocalDoesNotResetTheBridgeInvocation() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .existing(name: "scoutbook", entry: MCPEntry(config: local("npx", ["-y", "mcp-remote", url]))))
        XCTAssertEqual(editor.view, .form)
        XCTAssertTrue(editor.isRemote)
        editor.isRemote = false
        XCTAssertFalse(editor.isRemote)
        XCTAssertEqual(editor.command, "npx")
        XCTAssertEqual(editor.args.map(\.value), ["-y", "mcp-remote", url])
    }

    // MARK: view switching (catalog §3.5)

    func testSettingIsJsonViewSwitchesToJsonAndClearsIsFormView() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .new(template: local("node", ["x.js"])))
        XCTAssertEqual(editor.viewSelection, .form)
        editor.viewSelection = .json
        XCTAssertEqual(editor.view, .json)
        XCTAssertEqual(editor.viewSelection, .json)
    }

    func testSettingIsFormViewFromValidJsonSwitchesBack() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .new(template: local("node", ["x.js"])))
        editor.requestView(.json)
        editor.jsonText = "{\"command\": \"node\", \"args\": [\"y.js\"]}"
        editor.viewSelection = .form
        XCTAssertEqual(editor.view, .form)
        XCTAssertEqual(editor.viewSelection, .form)
        XCTAssertEqual(editor.args.map(\.value), ["y.js"])
    }

    /// An unparseable JSON text refuses the switch and snaps the segmented control
    /// back (objectWillChange fires so the Picker re-reads viewSelection), without
    /// ever reaching the loss-warning sheet.
    func testSettingIsFormViewWithUnrecoverableJsonIsRefusedAndSnapsBack() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .new(template: local("node", ["x.js"])))
        editor.requestView(.json)
        editor.jsonText = "{\"command\": "
        var raised = 0
        let subscription = editor.objectWillChange.sink { _ in raised += 1 }
        defer { subscription.cancel() }
        editor.viewSelection = .form
        XCTAssertEqual(editor.view, .json)
        XCTAssertEqual(editor.viewSelection, .json)
        XCTAssertEqual(editor.jsonError, EditorModel.notValidJSON)
        XCTAssertGreaterThan(raised, 0)
        XCTAssertNil(editor.lossWarning)
        XCTAssertTrue(h.dialogs.confirms.isEmpty)
    }

    func testSaveWithUnrecoverableJsonWritesNothing() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .new(template: local("node", ["x.js"])))
        editor.name = "broken"
        editor.requestView(.json)
        editor.jsonText = "{\"command\": "
        XCTAssertFalse(editor.save())
        XCTAssertNil(state.store.mcps["broken"])
        XCTAssertTrue(h.dialogs.confirms.isEmpty)
    }

    func testFormToJsonSyncsTheTextAndJsonToFormAdoptsIt() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .newRemote())
        editor.remoteURL = url
        editor.requestView(.json)
        XCTAssertEqual(editor.view, .json)
        XCTAssertEqual(editor.jsonText,
                       "{\n  \"args\" : [\n    \"-y\",\n    \"mcp-remote\",\n    \"" + url + "\"\n  ],\n  \"command\" : \"npx\"\n}")
        XCTAssertNil(editor.jsonError)
        XCTAssertEqual(editor.jsonStatusText, EditorModel.jsonTip)

        editor.jsonText = "{\"command\": \"node\", \"args\": [\"x.js\"], \"env\": {\"K\": \"v\"}}"
        editor.requestView(.form)
        XCTAssertEqual(editor.view, .form)
        XCTAssertFalse(editor.isRemote)
        XCTAssertEqual(editor.command, "node")
        XCTAssertEqual(editor.args.map(\.value), ["x.js"])
        XCTAssertEqual(try XCTUnwrap(editor.envRows.first).revealed, false)   // re-adopted values are masked again
    }

    func testFormToJsonIsBlockedByEnvValidation() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .new(template: local("node", ["x.js"])))
        editor.addEnvRow()
        editor.envRows[0].value = "orphan"
        editor.requestView(.json)
        XCTAssertEqual(editor.view, .form)
        XCTAssertEqual(editor.validationError, "An environment variable value is missing its name.")
        XCTAssertEqual(editor.viewSelection, .form)
    }

    func testJsonToFormWithLossPromptsAndStaysUnlessForced() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .new(template: local("node", ["x.js"])))
        editor.requestView(.json)
        editor.jsonText = "{\"command\": 1, \"args\": [\"a\", 2], \"env\": {\"K\": true}}"
        editor.requestView(.form)
        XCTAssertEqual(editor.view, .json)
        XCTAssertEqual(editor.lossWarning, ["args[1] (number)", "command (number)", "env.K (boolean)"])
        XCTAssertEqual(editor.lossWarningMessage,
                       "Switching to Form view can’t fully represent this configuration. These elements would be lost or altered:\nargs[1] (number)\ncommand (number)\nenv.K (boolean)")
        XCTAssertEqual(EditorModel.switchAnywayButton, "Switch Anyway")
        XCTAssertEqual(EditorModel.stayInJSONButton, "Stay in JSON")
        editor.stayInJSON()
        XCTAssertNil(editor.lossWarning)
        XCTAssertEqual(editor.view, .json)

        editor.requestView(.form)
        XCTAssertNotNil(editor.lossWarning)
        editor.forceSwitchToForm()
        XCTAssertNil(editor.lossWarning)
        XCTAssertEqual(editor.view, .form)
        XCTAssertEqual(editor.command, "")
        XCTAssertEqual(editor.args.map(\.value), ["a"])
        XCTAssertTrue(editor.envRows.isEmpty)
        XCTAssertTrue(h.dialogs.confirms.isEmpty)   // a sheet, not an NSAlert
    }

    // MARK: JSON view (catalog §3.7)

    func testJsonValidationErrorDisablesSave() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .new(template: local("node", ["x.js"])))
        editor.requestView(.json)
        editor.jsonText = "{\"command\": "
        XCTAssertEqual(editor.jsonError, "Not valid JSON — check for a stray brace, missing comma, or unquoted value.")
        XCTAssertEqual(editor.jsonStatusText, editor.jsonError)
        XCTAssertTrue(editor.hasJSONError)
        XCTAssertFalse(editor.canSave)
        editor.jsonText = "{\"command\": \"node\"}"
        XCTAssertNil(editor.jsonError)
        XCTAssertTrue(editor.canSave)
    }

    func testJsonPasteFillsTheNameWhenBlankAndCanonicalizesTheText() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .newRemote())
        editor.requestView(.json)
        editor.jsonText = "{\"mcpServers\": {\"pasted\": {\"command\": \"node\", \"args\": [\"x.js\"]}}}"
        XCTAssertTrue(editor.save())
        XCTAssertEqual(editor.name, "pasted")
        XCTAssertEqual(editor.jsonText, "{\n  \"args\" : [\n    \"x.js\"\n  ],\n  \"command\" : \"node\"\n}")
        XCTAssertEqual(state.store.mcps["pasted"]?.lastEditView, .json)
    }

    func testAuthKindTitlesMatchTheMacApp() {
        XCTAssertEqual(RemoteAuthKind.allCases.map(\.title),
                       ["Automatic (OAuth / none)", "Bearer token", "Custom header", "OAuth client ID/secret"])
        XCTAssertEqual(EditorModel.remoteFooter, "Runs via npx mcp-remote — managed for you.")
        XCTAssertEqual(EditorModel.urlHint, "Enter a valid http(s) URL, e.g. https://example.com/mcp")
        XCTAssertEqual(EditorModel.automaticCaption,
                       "Uses the server's OAuth (a browser window opens on first use), or no auth if the server is open.")
        XCTAssertEqual(EditorModel.bearerCaption, "Sent as Authorization: Bearer …")
        XCTAssertEqual(EditorModel.addArgumentTitle, "＋ Add argument")
        XCTAssertEqual(EditorModel.addVariableTitle, "＋ Add variable")
        XCTAssertEqual(EditorModel.jsonTip,
                       "Tip: paste a README snippet or an mcpServers stanza — a wrapper or a bare \"name\": {…} entry is unwrapped automatically, and the name filled in.")
        XCTAssertEqual(EditorModel.removeMessage("x"), "Remove “x”? A copy remains in Backups.")
        XCTAssertEqual(EditorModel.changedOutsideMessage("x"), "“x” changed outside this editor.")
        XCTAssertEqual(EditorModel.removedOutsideMessage("x"), "“x” was removed outside this editor.")
        XCTAssertEqual(EditorModel.changedOutsideDetail, "Saving will overwrite that change with this editor's version.")
        XCTAssertEqual(EditorModel.removedOutsideDetail, "Saving will add it back.")
        XCTAssertEqual(EditorModel.saveAnywayButton, "Save Anyway")
        XCTAssertEqual(EditTarget.addTitle, "Add Connector")
        XCTAssertEqual(EditTarget.editTitle("x"), "Edit “x”")
    }

    func testUrlHintShowsOnlyForANonEmptyInvalidUrl() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .newRemote())
        editor.remoteURL = "ftp://x"
        XCTAssertTrue(editor.showURLHint)
        XCTAssertFalse(editor.canSave)
        editor.remoteURL = "https://x.example/mcp"
        XCTAssertFalse(editor.showURLHint)
        XCTAssertTrue(editor.canSave)
    }

    func testEnvRowsAreMaskedExceptFreshlyAddedOnes() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .new(template: local("node", [], env: [("K", "v")])))
        XCTAssertFalse(editor.envRows[0].revealed)
        editor.toggleReveal(id: editor.envRows[0].id)
        XCTAssertTrue(editor.envRows[0].revealed)
        let focused = editor.addEnvRow()
        XCTAssertTrue(editor.envRows[1].revealed)
        XCTAssertEqual(editor.envRows[1].id, focused)
        editor.removeEnvRow(id: editor.envRows[0].id)
        XCTAssertEqual(editor.envRows.count, 1)
        editor.addArg()
        editor.args[0].value = "--flag"
        editor.removeArg(id: editor.args[0].id)
        XCTAssertTrue(editor.args.isEmpty)
    }
}
