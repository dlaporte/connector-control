import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/EditorModelTests.cs — the
/// save and remove slice. The save-conflict alert still goes through
/// FakeDialogs; remove's confirmation is a sheet (pending state + the
/// method the sheet's button calls).
@MainActor
final class EditorModelSaveTests: XCTestCase {
    private let url = "https://scoutbook.example.com/mcp"

    func testSaveWithUnrecoverableJsonWritesNothing() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        let editor = rig.editor(.new(template: rig.local("node", ["x.js"])))
        editor.name = "broken"
        editor.requestView(.json)
        editor.jsonText = "{\"command\": "
        XCTAssertFalse(editor.save())
        XCTAssertNil(state.store.mcps["broken"])
        XCTAssertTrue(rig.h.dialogs.confirms.isEmpty)
    }

    func testJsonPasteFillsTheNameWhenBlankAndCanonicalizesTheText() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        let editor = rig.editor(.newRemote())
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

    func testEnvNamesReachTheStoreVerbatim() throws {
        // Names are kept as typed: trimming once silently renamed a user's keys.
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        let editor = rig.editor(.new(template: rig.local("node", ["server.js"])))
        editor.name = "spaced"
        editor.addEnvRow()
        editor.envRows[0].name = " K "
        editor.envRows[0].value = "v"
        XCTAssertTrue(editor.save())
        XCTAssertEqual(try XCTUnwrap(state.store.mcps["spaced"]).config,
                       rig.local("node", ["server.js"], env: [(" K ", "v")]))
    }

    func testSaveValidatesTheRemoteForm() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        let rows: [(kind: RemoteAuthKind, url: String, token: String, headerName: String, headerValue: String, expected: String)] = [
            (.automatic, "", "", "", "", "Server URL must be a valid http(s) URL."),
            (.bearer, url, "", "", "", "Enter a bearer token."),
            (.header, url, "", "", "", "Enter a header name."),
            (.header, url, "", "X-API-Key", "", "Enter a header value."),
            (.oauthClient, url, "", "", "", "Enter a client ID."),
        ]
        for row in rows {
            let editor = rig.editor(.newRemote())
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
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.new(template: rig.local("", [])))
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
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.new(template: rig.local("node", [])))
        editor.requestView(.json)
        editor.jsonText = "{\"command\": \"npx\", \"args\": [\"-y\", \"mcp-remote\", \"nope\"]}"
        editor.name = "bad"
        XCTAssertFalse(editor.save())
        XCTAssertEqual(editor.validationError, "Server URL must be a valid http(s) URL.")
    }

    func testSaveNewRemoteWritesTheNpxShapeAndAppliesImmediately() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        let editor = rig.editor(.newRemote())
        editor.name = "new-remote"
        editor.remoteURL = "https://new.example/mcp"
        XCTAssertTrue(editor.save())
        let entry = try XCTUnwrap(state.store.mcps["new-remote"])
        XCTAssertTrue(entry.enabled)
        XCTAssertEqual(entry.lastEditView, .form)
        XCTAssertEqual(entry.config, RemotePattern.make(url: "https://new.example/mcp"))
        XCTAssertNotNil(try rig.h.claudeServers()["new-remote"])
        XCTAssertEqual(rig.h.settings.lastApplyDate, rig.h.now)
    }

    func testSaveEncodesEachAuthKind() {
        let cases: [(kind: RemoteAuthKind, expected: RemoteAuth)] = [
            (.bearer, .bearer(token: "tok")),
            (.header, .header(name: "X-API-Key", value: "v")),
            (.oauthClient, .oauthClient(clientID: "id", clientSecret: "sec", scopes: "a b")),
        ]
        for row in cases {
            let rig = EditorRig()
            defer { rig.dispose() }
            let state = rig.state
            let editor = rig.editor(.newRemote())
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
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        state.setEnabled("scoutbook", false)
        let editor = rig.editor(.existing(name: "scoutbook", entry: try XCTUnwrap(state.store.mcps["scoutbook"])))
        editor.remoteURL = "https://moved.example/mcp"
        editor.requestView(.json)
        XCTAssertTrue(editor.save())
        let entry = try XCTUnwrap(state.store.mcps["scoutbook"])
        XCTAssertFalse(entry.enabled)
        XCTAssertEqual(entry.lastEditView, .json)
        XCTAssertEqual(entry.config, RemotePattern.make(url: "https://moved.example/mcp"))   // decoded as bare npx, re-encoded as bare npx
        XCTAssertNil(try rig.h.claudeServers()["scoutbook"])   // disabled: not applied to Claude
    }

    func testEditingAPinnedConnectorKeepsItsPin() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        let pinned = rig.local("npx", ["-y", "mcp-remote@0.1.16", url])
        XCTAssertNil(state.upsert(name: "pinned", entry: MCPEntry(config: pinned), renamedFrom: nil))
        let editor = rig.editor(.existing(name: "pinned", entry: try XCTUnwrap(state.store.mcps["pinned"])))
        editor.remoteURL = "https://moved.example/mcp"
        XCTAssertTrue(editor.save())
        XCTAssertEqual(state.store.mcps["pinned"]?.config, rig.local("npx", ["-y", "mcp-remote@0.1.16", "https://moved.example/mcp"]))

        let again = rig.editor(.existing(name: "pinned", entry: try XCTUnwrap(state.store.mcps["pinned"])))
        again.requestView(.json)
        XCTAssertTrue(again.jsonText.contains("mcp-remote@0.1.16"), "the JSON view shows the pin too")
    }

    func testSaveRenameRemovesTheOldKeyAndNameErrorsSurface() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        let editor = rig.editor(.existing(name: "scoutbook", entry: try XCTUnwrap(state.store.mcps["scoutbook"])))
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
        XCTAssertNotNil(try rig.h.claudeServers()["scoutbook2"])
    }

    func testSaveConflictWhenTheEntryChangedOutsideTheEditor() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        let editor = rig.editor(.existing(name: "scoutbook", entry: try XCTUnwrap(state.store.mcps["scoutbook"])))
        XCTAssertNil(state.upsert(name: "scoutbook", entry: MCPEntry(config: AppStateHarness.remote("https://elsewhere.example/mcp")), renamedFrom: "scoutbook"))
        editor.remoteURL = "https://mine.example/mcp"
        rig.h.dialogs.nextConfirm = false
        XCTAssertFalse(editor.save())
        XCTAssertEqual(rig.h.dialogs.confirms, [FakeDialogs.ConfirmCall(
            message: "“scoutbook” changed outside this editor.",
            informative: "Saving will overwrite that change with this editor's version.",
            primary: "Save Anyway", cancel: "Cancel", destructive: false)])
        XCTAssertEqual(state.store.mcps["scoutbook"]?.config, AppStateHarness.remote("https://elsewhere.example/mcp"))

        rig.h.dialogs.nextConfirm = true
        XCTAssertTrue(editor.save())
        XCTAssertEqual(state.store.mcps["scoutbook"]?.config, RemotePattern.make(url: "https://mine.example/mcp"))
    }

    func testSaveConflictWhenTheEntryWasRemovedOutsideTheEditor() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        let editor = rig.editor(.existing(name: "scoutbook", entry: try XCTUnwrap(state.store.mcps["scoutbook"])))
        state.remove(name: "scoutbook")
        XCTAssertTrue(editor.save())
        XCTAssertEqual(rig.h.dialogs.confirms[0], FakeDialogs.ConfirmCall(
            message: "“scoutbook” was removed outside this editor.",
            informative: "Saving will add it back.",
            primary: "Save Anyway", cancel: "Cancel", destructive: false))
        XCTAssertEqual(state.store.mcps["scoutbook"]?.enabled, true)   // a re-added entry takes the editor's snapshot enabled state
    }

    func testRemoveConfirmsThenRemovesAndAppliesInOneTurn() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        let editor = rig.editor(.existing(name: "scoutbook", entry: try XCTUnwrap(state.store.mcps["scoutbook"])))
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
        XCTAssertNil(try rig.h.claudeServers()["scoutbook"])
        XCTAssertTrue(rig.h.dialogs.confirms.isEmpty)   // a sheet, not an NSAlert
    }

    func testAdditionalKeysAreMergedOnARemoteSave() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        let config = rig.local("npx", ["-y", "mcp-remote", url], extra: [("disabled", .bool(true))])
        let editor = rig.editor(.existing(name: "scoutbook", entry: MCPEntry(config: config)))
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
}
