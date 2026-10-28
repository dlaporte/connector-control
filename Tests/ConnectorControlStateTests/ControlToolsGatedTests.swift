import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlToolsGatedTests.cs
@MainActor
final class ControlToolsGatedTests: XCTestCase {
    private let notion: JSONValue = .object([
        "command": .string("npx"),
        "args": .array([.string("-y"), .string("@notionhq/notion-mcp-server")]),
        "env": .object(["NOTION_TOKEN": .string("${CC_NEEDS:NOTION_TOKEN}")]),
    ])

    private func rig(answer: Bool? = true) -> ControlRig {
        let rig = ControlRig()
        rig.confirmer.scripted = answer
        return rig
    }

    // MARK: add_connector

    func testAddingAsksWhatWouldRunThenAddsAndNamesWhatItNeeds() throws {
        let rig = rig(); defer { rig.dispose() }
        let result = try XCTUnwrap(rig.call("add_connector", ["name": .string("notion"), "config": notion]))
        XCTAssertEqual(rig.confirmer.requests, [ConfirmationRequest(
            title: "Claude wants to add “notion”",
            message: "It would join “Default”, the collection Claude is running, switched on, and run:",
            code: ControlMasking.codeLines(notion, exposure: .dialog),
            footer: ControlTools.takesEffectFooter)])
        XCTAssertEqual(result.text, ControlTools.addedText("notion", "Default", ControlTools.onWord) + "\n"
                       + ControlTools.needsValuesText("“NOTION_TOKEN”"))
        XCTAssertEqual(result.structured.value(at: JSONPointer(["needs"])), .array([.string("NOTION_TOKEN")]))
        XCTAssertEqual(try rig.h.claudeServers()["notion"], notion)
        XCTAssertEqual(rig.state.store.mcps["notion"], MCPEntry(enabled: true, config: notion, lastEditView: .json))
    }

    func testAddingSwitchedOffElsewhereSaysSo() throws {
        let rig = rig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
        _ = rig.call("add_connector", ["name": .string("notion"), "config": notion,
                                       "collection": .string("Work"), "enabled": .bool(false)])
        XCTAssertEqual(rig.confirmer.requests.last?.message, ControlTools.addMessage("Work", ControlTools.offWord))
        XCTAssertNil(rig.confirmer.requests.last?.footer, "nothing Claude runs changes")
        XCTAssertEqual(rig.state.store.collections["Work"]?.mcps["notion"]?.enabled, false)
        XCTAssertNil(try rig.h.claudeServers()["notion"])
    }

    func testAddingIsRefusedForEveryReasonTheEditorRefusesBeforeAnyoneIsAsked() throws {
        let rig = rig(); defer { rig.dispose() }
        try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        let cases: [([String: JSONValue], String)] = [
            (["name": .string("  "), "config": notion], AppState.nameEmptyError),
            (["name": .string("connector-control"), "config": notion], ClaudeIntegration.reservedNameError),
            (["name": .string("aws-mcp"), "config": notion], AppState.duplicateNameError("aws-mcp")),
            (["name": .string("x"), "config": .object(["args": .array([])])], ControlTools.configShapeError),
            (["name": .string("x"), "config": .object(["command": .string("npx"), "args": .array([.string("-y"), .string("mcp-remote"), .string("nope")])])],
             EditorModel.invalidURLError),
            (["name": .string("x"), "config": notion, "collection": .string("Data team")], CollectionsModel.addConnectorDisabledTooltip),
            (["name": .string("x"), "config": notion, "collection": .string("Nope")], MasterStore.noCollectionError("Nope")),
        ]
        for (arguments, refusal) in cases {
            XCTAssertEqual(rig.call("add_connector", arguments), .refusal(refusal), refusal)
        }
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
    }

    // The Windows mirror also refuses a cmd /c launcher with characters cmd.exe would act on; a Mac
    // never writes that launcher, so there is no such test here.

    // MARK: update_connector

    func testChangingShowsOnlyWhatDiffersAndKeepsTheSwitch() throws {
        let rig = rig(); defer { rig.dispose() }
        let before = try XCTUnwrap(rig.state.store.mcps["scoutbook"])
        let after = RemotePattern.make(url: "https://scoutbook.example.com/v2/mcp")
        let result = try XCTUnwrap(rig.call("update_connector", ["name": .string("scoutbook"), "config": after]))
        XCTAssertEqual(rig.confirmer.requests.last, ConfirmationRequest(
            title: "Claude wants to change “scoutbook”",
            message: "In “Default”, the collection Claude is running:",
            code: ControlMasking.changeLines(from: before.config, to: after, exposure: .dialog),
            footer: ControlTools.takesEffectFooter))
        XCTAssertEqual(result.text, ControlTools.changedText("scoutbook", "Default"))
        XCTAssertEqual(rig.state.store.mcps["scoutbook"]?.config, after)
        XCTAssertEqual(rig.state.store.mcps["scoutbook"]?.enabled, before.enabled)
        XCTAssertEqual(try rig.h.claudeServers()["scoutbook"], after)
    }

    func testRenamingThroughUpdate() throws {
        let rig = rig(); defer { rig.dispose() }
        let result = try XCTUnwrap(rig.call("update_connector", ["name": .string("scoutbook"), "newName": .string("scouts")]))
        XCTAssertEqual(rig.confirmer.requests.last?.code, [ControlMasking.nameLabel,
                                                          ControlMasking.removedPrefix + "scoutbook",
                                                          ControlMasking.addedPrefix + "scouts"])
        XCTAssertEqual(result.text, ControlTools.renamedText("scoutbook", "scouts"))
        XCTAssertNil(rig.state.store.mcps["scoutbook"])
        XCTAssertNotNil(try rig.h.claudeServers()["scouts"])
    }

    func testChangingIsRefusedBeforeAnyoneIsAsked() throws {
        let rig = rig(); defer { rig.dispose() }
        try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        let same = try XCTUnwrap(rig.state.store.mcps["scoutbook"]).config
        let cases: [([String: JSONValue], String)] = [
            (["name": .string("nope"), "config": notion], ControlTools.noConnectorError("nope", "Default")),
            (["name": .string("scoutbook")], ControlTools.nothingToChangeError),
            (["name": .string("scoutbook"), "config": same], ControlTools.unchangedError("scoutbook")),
            (["name": .string("scoutbook"), "newName": .string("aws-mcp")], AppState.duplicateNameError("aws-mcp")),
            (["name": .string("scoutbook"), "newName": .string("connector-control")], ClaudeIntegration.reservedNameError),
            (["name": .string("scoutbook"), "newName": .string(" ")], AppState.nameEmptyError),
            (["name": .string("scoutbook"), "config": .object([:])], ControlTools.configShapeError),
            (["name": .string("github"), "config": notion, "collection": .string("Data team")], CollectionsModel.lockedGlyphTooltip),
        ]
        for (arguments, refusal) in cases {
            XCTAssertEqual(rig.call("update_connector", arguments), .refusal(refusal), refusal)
        }
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
    }

    // MARK: delete_connectors

    func testDeletingAsksThenDeletes() throws {
        let rig = rig(); defer { rig.dispose() }
        let result = try XCTUnwrap(rig.call("delete_connectors", ["names": .array([.string("aws-mcp"), .string("scoutbook")])]))
        XCTAssertEqual(rig.confirmer.requests.last, ConfirmationRequest(
            title: "Claude wants to delete “aws-mcp” and “scoutbook”",
            message: "From “Default”, the collection Claude is running. A copy remains in Backups.",
            code: [], footer: ControlTools.takesEffectFooter))
        XCTAssertEqual(result.text, ControlTools.deletedText("“aws-mcp” and “scoutbook”", "Default"))
        XCTAssertEqual(Set(try rig.h.claudeServers().keys), ["service-now", ClaudeIntegration.entryName])
    }

    func testDeletingIsRefusedForANameThatIsNotThereOrASubscribedCollection() throws {
        let rig = rig(); defer { rig.dispose() }
        try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        XCTAssertEqual(rig.call("delete_connectors", ["names": .array([.string("aws-mcp"), .string("nope")])]),
                       .refusal(ControlTools.noConnectorError("nope", "Default")))
        XCTAssertEqual(rig.call("delete_connectors", ["names": .array([])]), .refusal(ControlTools.missingArgumentError("names")))
        XCTAssertEqual(rig.call("delete_connectors", ["names": .array([.string("github")]), "collection": .string("Data team")]),
                       .refusal(CollectionsModel.lockedGlyphTooltip))
        XCTAssertNotNil(rig.state.store.mcps["aws-mcp"], "nothing is deleted when one name is wrong")
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
    }
}
