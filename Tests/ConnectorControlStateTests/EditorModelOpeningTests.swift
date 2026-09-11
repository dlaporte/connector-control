import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/EditorModelTests.cs — the
/// opening slice: split from EditorModelTests.swift, which grouped every
/// EditorModel behavior in one file. `EditorRig` (Tests/ConnectorControlStateTests/TestSupport)
/// holds the harness, AppState and the `local`/`editor` builders every test here needs.
@MainActor
final class EditorModelOpeningTests: XCTestCase {
    private let url = "https://scoutbook.example.com/mcp"

    func testNewRemoteTargetOpensInTheRemoteFormWithAnEmptyUrl() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.newRemote())
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
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.existing(name: "scoutbook", entry: MCPEntry(config: RemotePattern.make(url: url))))
        XCTAssertEqual(editor.windowTitle, "Edit “scoutbook”")
        XCTAssertFalse(editor.showTypePicker)
        XCTAssertTrue(editor.isRemote)
        XCTAssertEqual(editor.remoteURL, url)
        XCTAssertTrue(editor.canSave)
        XCTAssertTrue(editor.canRemove)
    }

    func testExistingRemoteWithAuthFlagsOpensInTheLocalForm() throws {
        // detect() requires exactly two stripped args, so auth flags push the
        // connector into the Local form even though decode() populated the auth fields.
        let rig = EditorRig()
        defer { rig.dispose() }
        let config = RemotePattern.encode(RemoteConfig(url: url, auth: .bearer(token: "tok")))
        let editor = rig.editor(.existing(name: "scoutbook", entry: MCPEntry(config: config)))
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
        let rig = EditorRig()
        defer { rig.dispose() }
        let config = rig.local("node", ["server.js", "--port", "3000"], env: [("TOKEN", "s3cret"), ("A", "1")],
                           extra: [("disabled", .bool(false)), ("type", .string("stdio"))])
        let editor = rig.editor(.existing(name: "local", entry: MCPEntry(enabled: false, config: config, lastEditView: .json)))
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

    /// additionalPreview now goes through editorText(), which (unlike the
    /// old manual serialize+decode) does not escape forward slashes.
    func testAdditionalPreviewDoesNotEscapeSlashes() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let config = rig.local("node", ["server.js"], extra: [("homepage", .string("https://example.com/docs"))])
        let editor = rig.editor(.existing(name: "local", entry: MCPEntry(config: config, lastEditView: .json)))
        editor.requestView(.form)
        XCTAssertEqual(editor.additionalPreview, "{\n  \"homepage\" : \"https://example.com/docs\"\n}")
    }

    func testSwitchingANewTargetToLocalResetsTheBridgeInvocation() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.newRemote())
        editor.remoteURL = url
        editor.isRemote = false
        XCTAssertEqual(editor.command, "npx")
        XCTAssertEqual(editor.args.map(\.value), ["-y", ""])
        editor.isRemote = true
        XCTAssertEqual(editor.remoteURL, url)   // switching back changes nothing
    }

    func testSwitchingAnExistingTargetToLocalDoesNotResetTheBridgeInvocation() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.existing(name: "scoutbook", entry: MCPEntry(config: RemotePattern.make(url: url))))
        XCTAssertEqual(editor.view, .form)
        XCTAssertTrue(editor.isRemote)
        editor.isRemote = false
        XCTAssertFalse(editor.isRemote)
        XCTAssertEqual(editor.command, "npx")
        XCTAssertEqual(editor.args.map(\.value), ["-y", "mcp-remote", url])
    }

    /// The remote-URL quirk is now `load()`'s rule (used by both `init` and
    /// `adoptForm`): remoteURL is populated ONLY from `detect()`'s canonical
    /// 2-arg shape, never from `decode()`'s URL, even when isRemote is true
    /// via the forcesRemote/isRemoteShaped fallback.
    func testLoadOnlyTakesRemoteUrlFromDetectNotDecodeEvenWhenAuthFlagsForceTheRemoteForm() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        let editor = rig.editor(.newRemote())
        editor.requestView(.json)
        let pasted = RemotePattern.encode(RemoteConfig(
            url: url, auth: .header(name: "X-API-Key", value: "v"), extraArgs: ["--transport", "sse-only"]))
        editor.jsonText = pasted.editorText()
        editor.name = "pasted"
        editor.requestView(.form)
        XCTAssertTrue(editor.isRemote)            // forcesRemote + isRemoteShaped
        XCTAssertEqual(editor.remoteURL, "")      // quirk: load() only takes the URL from detect()
        XCTAssertEqual(editor.authKind, .header)
        XCTAssertEqual(editor.headerName, "X-API-Key")
        editor.remoteURL = url
        XCTAssertTrue(editor.save())
        XCTAssertEqual(state.store.mcps["pasted"]?.config, pasted)   // the extra args survive
    }

    func testUrlHintShowsOnlyForANonEmptyInvalidUrl() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.newRemote())
        editor.remoteURL = "ftp://x"
        XCTAssertTrue(editor.showURLHint)
        XCTAssertFalse(editor.canSave)
        editor.remoteURL = "https://x.example/mcp"
        XCTAssertFalse(editor.showURLHint)
        XCTAssertTrue(editor.canSave)
    }

    func testEnvRowsAreMaskedExceptFreshlyAddedOnes() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.new(template: rig.local("node", [], env: [("K", "v")])))
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
