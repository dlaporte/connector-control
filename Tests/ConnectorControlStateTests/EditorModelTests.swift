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

    // MARK: save (catalog §3.8–§3.9)

    func testEnvNamesReachTheStoreVerbatim() throws {
        // Names are kept as typed: trimming once silently renamed a user's keys.
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .new(template: local("node", ["server.js"])))
        editor.name = "spaced"
        editor.addEnvRow()
        editor.envRows[0].name = " K "
        editor.envRows[0].value = "v"
        XCTAssertTrue(editor.save())
        XCTAssertEqual(try XCTUnwrap(state.store.mcps["spaced"]).config,
                       local("node", ["server.js"], env: [(" K ", "v")]))
    }

    func testAdoptingAFormFromJsonEvaluatesTheToolOnceAtTheEnd() {
        // adoptForm assigns command, args and isRemote one after another; with
        // evaluation suppressed until the end, a config whose tool is unchanged
        // costs no probe at all, and a changed one costs exactly one batch.
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .new(template: local("node", ["server.js"])))
        defer { editor.dispose() }
        XCTAssertTrue(h.ui.pumpUntil({ state.toolStatuses[.node] != nil }, timeout: 5))
        let batches = h.tools.batches
        editor.requestView(.json)
        editor.jsonText = "{\"command\": \"node\", \"args\": [\"other.js\"]}"
        editor.requestView(.form)
        XCTAssertEqual(editor.view, .form)
        XCTAssertEqual(editor.requiredTool, .node)
        XCTAssertEqual(h.tools.batches, batches, "same tool after adoption: nothing to probe")
        editor.requestView(.json)
        editor.jsonText = "{\"command\": \"uvx\", \"args\": [\"tool\"]}"
        XCTAssertTrue(h.ui.pumpUntil({ h.tools.batches == batches + 1 }, timeout: 5))   // the JSON view evaluates as it parses
        editor.requestView(.form)
        XCTAssertEqual(editor.requiredTool, .uvx)
        XCTAssertEqual(h.tools.batches, batches + 1, "adoption of an already-evaluated config probes nothing more")
    }

    func testSaveValidatesTheRemoteForm() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let rows: [(kind: RemoteAuthKind, url: String, token: String, headerName: String, headerValue: String, expected: String)] = [
            (.automatic, "", "", "", "", "Server URL must be a valid http(s) URL."),
            (.bearer, url, "", "", "", "Enter a bearer token."),
            (.header, url, "", "", "", "Enter a header name."),
            (.header, url, "", "X-API-Key", "", "Enter a header value."),
            (.oauthClient, url, "", "", "", "Enter a client ID."),
        ]
        for row in rows {
            let editor = editor(h, state, .newRemote())
            editor.name = "r"
            editor.remoteURL = row.url
            editor.authKind = row.kind
            editor.bearerToken = row.token
            editor.headerName = row.headerName
            editor.headerValue = row.headerValue
            XCTAssertFalse(editor.save(), row.expected)
            XCTAssertEqual(editor.validationError, row.expected)
            XCTAssertNil(state.store.mcps["r"])
        }
    }

    func testSaveValidatesTheLocalForm() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .new(template: local("", [])))
        editor.name = "l"
        XCTAssertFalse(editor.save())
        XCTAssertEqual(editor.validationError, "Command must not be empty.")

        editor.command = "node"
        editor.addEnvRow()
        editor.envRows[0].name = "K"
        editor.addEnvRow()
        editor.envRows[1].name = "K"
        XCTAssertFalse(editor.save())
        XCTAssertEqual(editor.validationError, "Duplicate environment variable name: K")
    }

    func testSaveRejectsACanonicalBridgeShapeWithAnInvalidUrl() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .new(template: local("node", [])))
        editor.requestView(.json)
        editor.jsonText = "{\"command\": \"npx\", \"args\": [\"-y\", \"mcp-remote\", \"nope\"]}"
        editor.name = "bad"
        XCTAssertFalse(editor.save())
        XCTAssertEqual(editor.validationError, "Server URL must be a valid http(s) URL.")
    }

    func testSaveNewRemoteWritesTheNpxShapeAndAppliesImmediately() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .newRemote())
        editor.name = "new-remote"
        editor.remoteURL = "https://new.example/mcp"
        XCTAssertTrue(editor.save())
        let entry = try XCTUnwrap(state.store.mcps["new-remote"])
        XCTAssertTrue(entry.enabled)
        XCTAssertEqual(entry.lastEditView, .form)
        XCTAssertEqual(entry.config, local("npx", ["-y", "mcp-remote", "https://new.example/mcp"]))
        XCTAssertNotNil(try h.claudeServers()["new-remote"])
        XCTAssertEqual(h.settings.lastApplyDate, h.now)
    }

    func testSaveEncodesEachAuthKind() {
        let cases: [(kind: RemoteAuthKind, expected: RemoteAuth)] = [
            (.bearer, .bearer(token: "tok")),
            (.header, .header(name: "X-API-Key", value: "v")),
            (.oauthClient, .oauthClient(clientID: "id", clientSecret: "sec", scopes: "a b")),
        ]
        for row in cases {
            let h = AppStateHarness()
            defer { h.dispose() }
            let state = h.create()
            let editor = editor(h, state, .newRemote())
            editor.name = "auth"
            editor.remoteURL = url
            editor.authKind = row.kind
            editor.bearerToken = "tok"
            editor.headerName = "X-API-Key"
            editor.headerValue = "v"
            editor.oauthClientID = "id"
            editor.oauthClientSecret = "sec"
            editor.oauthScopes = "a b"
            XCTAssertTrue(editor.save())
            XCTAssertEqual(state.store.mcps["auth"]?.config, RemotePattern.encode(RemoteConfig(url: url, auth: row.expected)))
        }
    }

    func testSaveExistingPreservesTheEnabledStateAndRecordsTheView() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.setEnabled("scoutbook", false)
        let editor = editor(h, state, .existing(name: "scoutbook", entry: try XCTUnwrap(state.store.mcps["scoutbook"])))
        editor.remoteURL = "https://moved.example/mcp"
        editor.requestView(.json)
        XCTAssertTrue(editor.save())
        let entry = try XCTUnwrap(state.store.mcps["scoutbook"])
        XCTAssertFalse(entry.enabled)
        XCTAssertEqual(entry.lastEditView, .json)
        XCTAssertEqual(entry.config, local("npx", ["-y", "mcp-remote", "https://moved.example/mcp"]))   // decoded as bare npx, re-encoded as bare npx
        XCTAssertNil(try h.claudeServers()["scoutbook"])   // disabled: not applied to Claude
    }

    func testEditingAPinnedConnectorKeepsItsPin() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let pinned = local("npx", ["-y", "mcp-remote@0.1.16", url])
        XCTAssertNil(state.upsert(name: "pinned", entry: MCPEntry(config: pinned), renamedFrom: nil))
        let editor = editor(h, state, .existing(name: "pinned", entry: try XCTUnwrap(state.store.mcps["pinned"])))
        editor.remoteURL = "https://moved.example/mcp"
        XCTAssertTrue(editor.save())
        XCTAssertEqual(state.store.mcps["pinned"]?.config, local("npx", ["-y", "mcp-remote@0.1.16", "https://moved.example/mcp"]))

        let again = self.editor(h, state, .existing(name: "pinned", entry: try XCTUnwrap(state.store.mcps["pinned"])))
        again.requestView(.json)
        XCTAssertTrue(again.jsonText.contains("mcp-remote@0.1.16"), "the JSON view shows the pin too")
    }

    func testSaveRenameRemovesTheOldKeyAndNameErrorsSurface() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .existing(name: "scoutbook", entry: try XCTUnwrap(state.store.mcps["scoutbook"])))
        editor.name = "aws-mcp"
        XCTAssertFalse(editor.save())
        XCTAssertEqual(editor.validationError, "A connector named “aws-mcp” already exists.")
        editor.name = " "
        XCTAssertFalse(editor.save())
        XCTAssertEqual(editor.validationError, "Name must not be empty.")
        editor.name = "scoutbook2"
        XCTAssertTrue(editor.save())
        XCTAssertNil(state.store.mcps["scoutbook"])
        XCTAssertNotNil(state.store.mcps["scoutbook2"])
        XCTAssertNotNil(try h.claudeServers()["scoutbook2"])
    }

    func testSaveConflictWhenTheEntryChangedOutsideTheEditor() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .existing(name: "scoutbook", entry: try XCTUnwrap(state.store.mcps["scoutbook"])))
        XCTAssertNil(state.upsert(name: "scoutbook", entry: MCPEntry(config: AppStateHarness.remote("https://elsewhere.example/mcp")), renamedFrom: "scoutbook"))
        editor.remoteURL = "https://mine.example/mcp"
        h.dialogs.nextConfirm = false
        XCTAssertFalse(editor.save())
        XCTAssertEqual(h.dialogs.confirms, [FakeDialogs.ConfirmCall(
            message: "“scoutbook” changed outside this editor.",
            informative: "Saving will overwrite that change with this editor's version.",
            primary: "Save Anyway", cancel: "Cancel", destructive: false)])
        XCTAssertEqual(state.store.mcps["scoutbook"]?.config, AppStateHarness.remote("https://elsewhere.example/mcp"))

        h.dialogs.nextConfirm = true
        XCTAssertTrue(editor.save())
        XCTAssertEqual(state.store.mcps["scoutbook"]?.config, local("npx", ["-y", "mcp-remote", "https://mine.example/mcp"]))
    }

    func testSaveConflictWhenTheEntryWasRemovedOutsideTheEditor() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .existing(name: "scoutbook", entry: try XCTUnwrap(state.store.mcps["scoutbook"])))
        state.remove(name: "scoutbook")
        XCTAssertTrue(editor.save())
        XCTAssertEqual(h.dialogs.confirms[0], FakeDialogs.ConfirmCall(
            message: "“scoutbook” was removed outside this editor.",
            informative: "Saving will add it back.",
            primary: "Save Anyway", cancel: "Cancel", destructive: false))
        XCTAssertEqual(state.store.mcps["scoutbook"]?.enabled, true)   // a re-added entry takes the editor's snapshot enabled state
    }

    // MARK: remove (catalog §3.10)

    func testRemoveConfirmsThenRemovesAndAppliesInOneTurn() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .existing(name: "scoutbook", entry: try XCTUnwrap(state.store.mcps["scoutbook"])))
        XCTAssertFalse(editor.removeConfirmationPending)
        editor.requestRemove()
        XCTAssertTrue(editor.removeConfirmationPending)
        XCTAssertEqual(editor.removeConfirmationMessage, "Remove “scoutbook”? A copy remains in Backups.")
        XCTAssertEqual(EditorModel.removeButton, "Remove")
        editor.cancelRemove()
        XCTAssertFalse(editor.removeConfirmationPending)
        XCTAssertNotNil(state.store.mcps["scoutbook"])

        editor.requestRemove()
        editor.confirmRemove()
        XCTAssertFalse(editor.removeConfirmationPending)
        XCTAssertNil(state.store.mcps["scoutbook"])
        XCTAssertNil(try h.claudeServers()["scoutbook"])
        XCTAssertTrue(h.dialogs.confirms.isEmpty)   // a sheet, not an NSAlert
    }

    func testPastedAuthConfigOnANewRemoteTargetKeepsItsAuthAndExtraArgs() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let editor = editor(h, state, .newRemote())
        editor.requestView(.json)
        let pasted = RemotePattern.encode(RemoteConfig(
            url: url, auth: .header(name: "X-API-Key", value: "v"), extraArgs: ["--transport", "sse-only"]))
        editor.jsonText = pasted.editorText()
        editor.name = "pasted"
        editor.requestView(.form)
        XCTAssertTrue(editor.isRemote)            // forcesRemote + isRemoteShaped
        XCTAssertEqual(editor.remoteURL, "")      // catalog §3.5 quirk: adoptForm only takes the URL from detect()
        XCTAssertEqual(editor.authKind, .header)
        XCTAssertEqual(editor.headerName, "X-API-Key")
        editor.remoteURL = url
        XCTAssertTrue(editor.save())
        XCTAssertEqual(state.store.mcps["pasted"]?.config, pasted)   // the extra args survive
    }

    func testAdditionalKeysAreMergedOnARemoteSave() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let config = local("npx", ["-y", "mcp-remote", url], extra: [("disabled", .bool(true))])
        let editor = editor(h, state, .existing(name: "scoutbook", entry: MCPEntry(config: config)))
        XCTAssertTrue(editor.isRemote)
        XCTAssertTrue(editor.hasAdditional)
        editor.remoteURL = "https://moved.example/mcp"
        XCTAssertTrue(editor.save())
        guard case .object(let saved)? = state.store.mcps["scoutbook"]?.config else {
            return XCTFail("expected an object config")
        }
        XCTAssertEqual(saved["disabled"], .bool(true))
        XCTAssertEqual(saved["args"], .array([.string("-y"), .string("mcp-remote"), .string("https://moved.example/mcp")]))
    }

    // MARK: tool note (spec 2026-09-05-tool-probe §3.3–§3.4)

    func testNewRemoteConnectorNotesAMissingNpx() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.tools.statuses[.npx] = .notFound
        let state = h.create()
        let editor = editor(h, state, .newRemote())
        defer { editor.dispose() }
        XCTAssertEqual(editor.requiredTool, .npx)
        XCTAssertNil(editor.toolNote)   // not probed yet: no note, and nothing blocks
        XCTAssertFalse(editor.hasToolNote)
        XCTAssertTrue(h.ui.pumpUntil({ editor.hasToolNote }, timeout: 5))
        let note = try XCTUnwrap(editor.toolNote)
        XCTAssertEqual(note.text, "npx wasn’t found, so Claude Desktop won’t be able to start this connector.")
        XCTAssertEqual(note.linkTitle, "Install Node.js")
        XCTAssertEqual(note.linkURL.absoluteString, "https://nodejs.org/en/download")
        XCTAssertEqual(note.installCommand, "brew install node")
        editor.name = "example"
        editor.remoteURL = url
        XCTAssertTrue(editor.canSave)   // the note never blocks Save
        XCTAssertTrue(editor.save())
        XCTAssertNil(editor.validationError)
    }

    func testLocalCommandChangesReEvaluateAndReProbeTheTool() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.tools.statuses[.uvx] = .notFound
        let state = h.create()
        let editor = editor(h, state, .new(template: local("node", ["server.js"])))
        defer { editor.dispose() }
        XCTAssertEqual(editor.requiredTool, .node)
        XCTAssertTrue(h.ui.pumpUntil({ state.toolStatuses[.node] != nil }, timeout: 5))
        XCTAssertFalse(editor.hasToolNote)   // node is installed on this (fake) machine
        var raised = 0
        let subscription = editor.objectWillChange.sink { _ in raised += 1 }
        defer { subscription.cancel() }
        editor.command = "uvx"
        XCTAssertEqual(editor.requiredTool, .uvx)
        XCTAssertGreaterThan(raised, 0)
        XCTAssertTrue(h.ui.pumpUntil({ editor.hasToolNote }, timeout: 5))
        XCTAssertTrue(try XCTUnwrap(editor.toolNote).text.hasPrefix("uvx wasn’t found"))
        editor.command = "/usr/local/bin/uvx"   // a path is the user's deliberate choice: no PATH lookup, no note
        XCTAssertNil(editor.requiredTool)
        XCTAssertFalse(editor.hasToolNote)
        editor.command = "python"
        XCTAssertNil(editor.requiredTool)
        XCTAssertEqual(h.tools.probed.count, 2)   // node once, uvx once — the non-tools cost nothing
        editor.command = "uvx"
        // Back to a tool that is cached: probed again anyway — it may have been installed meanwhile.
        XCTAssertTrue(h.ui.pumpUntil({ h.tools.probed.count == 3 }, timeout: 5))
        XCTAssertTrue(editor.hasToolNote)
    }

    func testJsonViewEvaluatesTheParsedConfig() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.tools.statuses[.uv] = .notFound
        let state = h.create()
        let editor = editor(h, state, .new(template: local("node", ["x.js"])))
        defer { editor.dispose() }
        editor.requestView(.json)
        XCTAssertEqual(editor.requiredTool, .node)   // the same config, now read from the text
        editor.jsonText = "{\"command\": \"uv\", \"args\": [\"run\", \"server.py\"]}"
        XCTAssertEqual(editor.requiredTool, .uv)
        XCTAssertTrue(h.ui.pumpUntil({ editor.hasToolNote }, timeout: 5))
        editor.jsonText = "{ not json"
        XCTAssertNil(editor.requiredTool)   // unparseable: nothing to evaluate
        XCTAssertFalse(editor.hasToolNote)
        editor.jsonText = "{\"command\": \"npx\", \"args\": [\"-y\", \"mcp-remote\", \"" + url + "\"]}"
        XCTAssertEqual(editor.requiredTool, .npx)
        editor.requestView(.form)   // a bare bridge invocation: the remote form, still npx
        XCTAssertTrue(editor.isRemote)
        XCTAssertEqual(editor.requiredTool, .npx)
    }

    func testACachedStatusShowsTheNoteAtOnceAndAFoundToolShowsNone() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.tools.statuses[.npx] = .notFound
        let state = h.create()
        state.refreshTools()
        XCTAssertTrue(h.ui.pumpUntil({ state.toolStatuses[.npx] != nil }, timeout: 5))
        let batches = h.tools.batches
        let remote = editor(h, state, .existing(name: "scoutbook", entry: try XCTUnwrap(state.store.mcps["scoutbook"])))   // bare npx mcp-remote
        XCTAssertTrue(remote.hasToolNote)          // straight from the cache, no wait
        XCTAssertEqual(h.tools.batches, batches)   // and no re-probe on open
        let localEditor = editor(h, state, .existing(name: "local", entry: MCPEntry(config: local("node", ["x.js"]))))
        defer { localEditor.dispose() }
        XCTAssertEqual(localEditor.requiredTool, .node)
        XCTAssertFalse(localEditor.hasToolNote)
        XCTAssertEqual(h.tools.batches, batches)
        remote.dispose()
        state.refreshTools([.npx])   // a disposed editor no longer listens
        XCTAssertTrue(h.ui.pumpUntil({ h.tools.batches == batches + 1 }, timeout: 5))
    }

    func testDisposeStopsRelayingAppStateToolStatusChanges() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.tools.statuses[.npx] = .notFound
        let state = h.create()
        let editor = editor(h, state, .newRemote())
        XCTAssertTrue(h.ui.pumpUntil({ editor.hasToolNote }, timeout: 5))

        editor.dispose()
        var raised = 0
        let subscription = editor.objectWillChange.sink { _ in raised += 1 }
        defer { subscription.cancel() }

        // Publish a change that would clear the note on a live (not disposed) editor.
        h.tools.statuses[.npx] = .found(path: "/opt/homebrew/bin/npx", version: "1.0.0")
        state.refreshTools([.npx])
        XCTAssertTrue(h.ui.pumpUntil({ state.toolStatuses[.npx] == .found(path: "/opt/homebrew/bin/npx", version: "1.0.0") }, timeout: 5))
        XCTAssertEqual(raised, 0)   // the view was never told to re-read: dispose stopped the relay
    }

    /// macOS only (catalog §3.13): a launcher only the login shell can see gets the advice line.
    func testAShellOnlyToolShowsTheAdviceLine() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.tools.statuses[.npx] = .foundInShellOnly(path: "/Users/me/.nvm/versions/node/v22/bin/npx", version: "10.9.2")
        let state = h.create()
        let editor = editor(h, state, .newRemote())
        defer { editor.dispose() }
        XCTAssertTrue(h.ui.pumpUntil({ editor.hasToolNote }, timeout: 5))
        let note = try XCTUnwrap(editor.toolNote)
        XCTAssertEqual(note.text, "npx is at /Users/me/.nvm/versions/node/v22/bin/npx in your shell, but Claude Desktop launches connectors with its own PATH and may not see it there.")
        XCTAssertEqual(note.advice, ToolNote.shellOnlyAdvice)
        XCTAssertEqual(note.installCommand, "brew install node")
        editor.name = "example"
        editor.remoteURL = url
        XCTAssertTrue(editor.canSave)   // advisory: never blocks Save
    }
}
