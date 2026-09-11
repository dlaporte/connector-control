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
        XCTAssertTrue(rig.h.dialogs.confirms.isEmpty)   // a sheet, not an NSAlert
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
