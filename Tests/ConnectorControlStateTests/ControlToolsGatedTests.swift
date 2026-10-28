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

    /// A config the masked view and the dialog show differently: an argument after a flag named for
    /// a secret, a URL with a query value, and an environment value.
    private let secretive: JSONValue = .object([
        "command": .string("npx"),
        "args": .array([.string("-y"), .string("acme-mcp"), .string("--api-key"), .string("sk-live-abc123"),
                        .string("https://h.example/mcp?token=abc")]),
        "env": .object(["ACME_TOKEN": .string("literal-env-secret")]),
    ])

    /// The same config twice over, but for how one character is composed: é as one character, and
    /// as e and a combining acute accent.
    private let composed: JSONValue = .object(["command": .string("node"), "args": .array([.string("caf\u{E9}.js")])])
    private let decomposed: JSONValue = .object(["command": .string("node"), "args": .array([.string("cafe\u{301}.js")])])

    private func rig(answer: Bool? = true) -> ControlRig {
        let rig = ControlRig()
        rig.confirmer.scripted = answer
        return rig
    }

    /// Nothing in `text` that a line would show as an escape: nothing that breaks a line, or
    /// reorders or hides text.
    private func assertPlain(_ text: String, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertFalse(text.unicodeScalars.contains { ControlText.escape($0) != nil }, text, file: file, line: line)
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
            (["name": .string("x"), "config": .object(["url": .string(" ")])], ControlTools.configShapeError),
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

    /// A config that differs only in how a character is composed is a different config, as it is
    /// on Windows, which compares ordinally: the change is asked about, not refused as no change.
    func testAChangeOnlyInHowACharacterIsComposedIsStillAChange() throws {
        let rig = rig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.upsert(name: "scoutbook", entry: MCPEntry(config: composed), renamedFrom: "scoutbook"))
        let result = try XCTUnwrap(rig.call("update_connector", ["name": .string("scoutbook"), "config": decomposed]))
        XCTAssertEqual(result.text, ControlTools.changedText("scoutbook", "Default"))
        XCTAssertEqual(rig.confirmer.requests.count, 1)
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

    // MARK: What the question shows

    /// The dialog shows what would run, argument and URL as written, with the environment's values
    /// masked, where the masked view would hide all three; the answer to the caller shows none of them.
    func testTheQuestionShowsWhatWouldRunAndTheAnswerNoSecret() throws {
        let rig = rig(); defer { rig.dispose() }
        let added = try XCTUnwrap(rig.call("add_connector", ["name": .string("acme"), "config": secretive]))
        let changed = try XCTUnwrap(rig.call("update_connector", ["name": .string("scoutbook"), "config": secretive]))
        XCTAssertEqual(rig.confirmer.requests.count, 2)
        XCTAssertNotEqual(rig.confirmer.requests.first?.code, ControlMasking.codeLines(secretive), "the masked view hides them")
        for request in rig.confirmer.requests {
            let code = request.code.joined(separator: "\n")
            XCTAssertTrue(code.contains("--api-key sk-live-abc123 https://h.example/mcp?token=abc"), code)
            XCTAssertTrue(code.contains("ACME_TOKEN"), code)
            XCTAssertFalse(code.contains("literal-env-secret"), code)
        }
        for result in [added, changed] {
            XCTAssertFalse(result.isError)
            for secret in ["sk-live-abc123", "token=abc", "literal-env-secret"] {
                XCTAssertFalse(result.text.contains(secret), result.text)
                XCTAssertFalse(result.structured.compactLine().contains(secret), result.structured.compactLine())
            }
        }
    }

    /// The question's names are chosen by others: a connector's by Claude, a collection's by Claude
    /// too (it creates and renames collections without asking), and the asker's by whichever client
    /// connects. Each shows as an escape what would break a line or reorder or hide text.
    func testNamesCannotForgeWhatTheQuestionSays() throws {
        let rig = rig(); defer { rig.dispose() }
        let collection = "Work\n\u{202E}z", shownCollection = #"Work\n\u{202E}z"#
        let name = "a\nb\u{202E}c", shownName = #"a\nb\u{202E}c"#
        let caller = ControlCaller.relay(clientName: "Cursor\n\u{202E}d"), shownAsker = #"Cursor\n\u{202E}d"#
        XCTAssertNil(rig.state.addEmptyCollection(named: collection))
        let target: [String: JSONValue] = ["collection": .string(collection)]
        let results = [
            rig.call("add_connector", target.merging(["name": .string(name), "config": notion]) { $1 }, as: caller),
            rig.call("update_connector", target.merging(["name": .string(name), "newName": .string("b")]) { $1 }, as: caller),
            rig.call("delete_connectors", target.merging(["names": .array([.string("b")])]) { $1 }, as: caller),
            rig.call("delete_connectors", target.merging(["names": .array([.string(name)])]) { $1 }, as: caller),
        ]
        XCTAssertEqual(results.map { $0?.text }, [
            ControlTools.addedText(shownName, shownCollection, ControlTools.onWord) + "\n" + ControlTools.needsValuesText("“NOTION_TOKEN”"),
            ControlTools.renamedText(shownName, "b"),
            ControlTools.deletedText("“b”", shownCollection),
            ControlTools.noConnectorError(shownName, shownCollection),
        ], "the answers name them so too")
        let requests = rig.confirmer.requests
        XCTAssertEqual(requests.map(\.title), [ControlTools.addTitle(shownAsker, shownName),
                                               ControlTools.changeTitle(shownAsker, shownName),
                                               ControlTools.deleteTitle(shownAsker, "“b”")])
        XCTAssertEqual(requests.map(\.message), [ControlTools.addMessage(shownCollection, ControlTools.onWord),
                                                 ControlTools.changeMessage(shownCollection),
                                                 ControlTools.deleteMessage(shownCollection)])
        XCTAssertEqual(requests.dropFirst().first?.code,
                       [ControlMasking.nameLabel, ControlMasking.removedPrefix + shownName, ControlMasking.addedPrefix + "b"])
        for request in requests {
            for text in [request.title, request.message] + request.code { assertPlain(text) }
        }
        for result in results {
            for line in (result?.text ?? "").split(separator: "\n") { assertPlain(String(line)) }
        }
        XCTAssertEqual(rig.state.store.collections[collection]?.mcps.isEmpty, true, "added, renamed and deleted as asked")
    }

    /// A name cannot close the quotes around it, so one name never reads as two.
    func testANameCannotReadAsTwoNames() throws {
        let rig = rig(); defer { rig.dispose() }
        let name = "x” and “github"
        XCTAssertNil(rig.state.upsert(name: name, entry: MCPEntry(config: notion), renamedFrom: nil))
        _ = rig.call("delete_connectors", ["names": .array([.string(name)])])
        XCTAssertEqual(rig.confirmer.requests.last?.title, ControlTools.deleteTitle("Claude", #"“x\u{201D} and \u{201C}github”"#))
    }

    /// A new name is one line of what the question shows, however many lines it holds.
    func testANewNameCannotAddLinesToWhatTheQuestionShows() throws {
        let rig = rig(); defer { rig.dispose() }
        _ = rig.call("update_connector", ["name": .string("scoutbook"), "newName": .string("a\n  + command   evil")])
        XCTAssertEqual(rig.confirmer.requests.last?.code, [ControlMasking.nameLabel, ControlMasking.removedPrefix + "scoutbook",
                                                          ControlMasking.addedPrefix + #"a\n  + command   evil"#])
    }
}
