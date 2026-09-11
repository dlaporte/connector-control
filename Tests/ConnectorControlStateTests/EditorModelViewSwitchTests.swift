import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/EditorModelTests.cs — the
/// Form/JSON view-switching slice. Sheet-style confirmations are asserted as
/// pending state plus the method the sheet's button calls.
@MainActor
final class EditorModelViewSwitchTests: XCTestCase {
    private let url = "https://scoutbook.example.com/mcp"

    func testSettingIsJsonViewSwitchesToJsonAndClearsIsFormView() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.new(template: rig.local("node", ["x.js"])))
        XCTAssertEqual(editor.viewSelection, .form)
        editor.viewSelection = .json
        XCTAssertEqual(editor.view, .json)
        XCTAssertEqual(editor.viewSelection, .json)
    }

    func testSettingIsFormViewFromValidJsonSwitchesBack() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.new(template: rig.local("node", ["x.js"])))
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
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.new(template: rig.local("node", ["x.js"])))
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
        XCTAssertTrue(rig.h.dialogs.confirms.isEmpty)
    }

    func testFormToJsonSyncsTheTextAndJsonToFormAdoptsIt() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.newRemote())
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
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.new(template: rig.local("node", ["x.js"])))
        editor.addEnvRow()
        editor.envRows[0].value = "orphan"
        editor.requestView(.json)
        XCTAssertEqual(editor.view, .form)
        XCTAssertEqual(editor.validationError, "An environment variable value is missing its name.")
        XCTAssertEqual(editor.viewSelection, .form)
    }

    func testJsonToFormWithLossPromptsAndStaysUnlessForced() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.new(template: rig.local("node", ["x.js"])))
        editor.requestView(.json)
        editor.jsonText = "{\"command\": 1, \"args\": [\"a\", 2], \"env\": {\"K\": true}}"
        editor.requestView(.form)
        XCTAssertEqual(editor.view, .json)
        XCTAssertEqual(editor.lossWarning, ["args[1] (number)", "command (number)", "env.K (boolean)"])
        XCTAssertEqual(editor.lossWarningMessage,
                       "Switching to Form view can’t fully represent this configuration. These elements would be lost or altered:\nargs[1] (number)\ncommand (number)\nenv.K (boolean)")
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
        XCTAssertTrue(rig.h.dialogs.confirms.isEmpty)   // a sheet, not an NSAlert
    }

    /// The template-discard rule fires once, at the first switch to Local; a
    /// second Type toggle must not re-derive it and wipe what the user typed.
    func testTogglingTheTypeTwiceKeepsATypedLocalCommand() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.newRemote())
        editor.isRemote = false
        editor.command = "node"
        editor.args = [ArgRow(value: "server.js")]
        editor.isRemote = true
        editor.isRemote = false
        XCTAssertEqual(editor.command, "node")
        XCTAssertEqual(editor.args.map(\.value), ["server.js"])
    }

    /// A JSON-view edit to a still-open remote template must survive the
    /// round trip back to Form: the template flag is consumed by an actual
    /// edit, not just by opening the JSON view, so a later Type toggle to
    /// Remote and back to Local must not re-derive and wipe the edited
    /// command/args. The edit is a LOCAL-shaped config on purpose: `load`
    /// assigns `isRemote`'s backing field directly (a plain `didSet`, not
    /// the `isRemote` setter's discard path — see `isRemoteChanged`'s
    /// `view == .form` gate), so switching to Form alone fires no discard
    /// regardless of this fix; only the later explicit toggle does, and only
    /// the fix keeps it from wiping what was just adopted.
    func testATemplateEditedInJsonKeepsItsCommandOnSwitchToLocal() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.newRemote())
        editor.requestView(.json)
        editor.jsonText = "{\"command\":\"node\",\"args\":[\"server.js\"]}"
        editor.requestView(.form)
        XCTAssertEqual(editor.view, .form)
        editor.isRemote = true
        editor.isRemote = false
        XCTAssertEqual(editor.command, "node")
        XCTAssertEqual(editor.args.map(\.value), ["server.js"])
    }

    /// An unedited JSON round trip (straight to JSON and back without
    /// touching the text) still counts as untouched — the discard on Type
    /// toggle to Local fires exactly as it did before this template flag was
    /// scoped to actual edits.
    func testAnUnchangedJsonRoundTripStillDiscardsTheTemplateOnSwitchToLocal() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.newRemote())
        editor.requestView(.json)
        editor.requestView(.form)
        XCTAssertEqual(editor.view, .form)
        editor.isRemote = false
        XCTAssertEqual(editor.command, "npx")
        XCTAssertEqual(editor.args.map(\.value), ["-y", ""])
    }

    func testJsonValidationErrorDisablesSave() {
        let rig = EditorRig()
        defer { rig.dispose() }
        let editor = rig.editor(.new(template: rig.local("node", ["x.js"])))
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
}
