import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ConfirmationDialogTests.cs
@MainActor
final class ConfirmationDialogTests: XCTestCase {
    private func question(_ title: String, _ message: String = "", code: [String] = [], footer: String? = nil) -> ConfirmationRequest {
        ConfirmationRequest(title: title, message: message, code: code, footer: footer)
    }

    private let restart = ConfirmationRequest(title: ControlTools.restartItselfTitle, message: ControlTools.restartMessage,
                                              code: [], footer: nil)

    // MARK: Text

    func testNamesAreSetApartFromTheSentence() {
        let dialog = ConfirmationDialog(question(ControlTools.deleteTitle("Claude", ControlText.quotedList(["aws-billing", "splunk"])),
                                                 ControlTools.deleteActiveMessage("Harvard")))
        XCTAssertEqual(dialog.title, [TextRun("Claude wants to delete “"), TextRun("aws-billing", isSetApart: true),
                                      TextRun("” and “"), TextRun("splunk", isSetApart: true), TextRun("”")])
        XCTAssertEqual(dialog.message, [TextRun("From “"), TextRun("Harvard", isSetApart: true),
                                        TextRun("”, the collection Claude is running. A copy remains in Backups.")])
        XCTAssertEqual(dialog.code, [])
        XCTAssertNil(dialog.footer)
        XCTAssertEqual(ConfirmationDialog(restart).title, [TextRun(ControlTools.restartItselfTitle)], "a question naming no one is one run")
    }

    /// A name's own “ and ” are escaped before it reaches a question, and a quote that only looks like
    /// one stays inside the name's run, drawn in the name's face, where it cannot pass for its end.
    func testAQuoteInsideANameCannotEndIt() {
        XCTAssertEqual(ConfirmationDialog(question(ControlTools.addTitle("Claude", ControlText.escapedName("a” and “b")))).title,
                       [TextRun("Claude wants to add “"), TextRun(#"a\u{201D} and \u{201C}b"#, isSetApart: true), TextRun("”")])
        XCTAssertEqual(ConfirmationDialog(question(ControlTools.addTitle("Claude", ControlText.escapedName("a＂ and ＂b")))).title,
                       [TextRun("Claude wants to add “"), TextRun("a＂ and ＂b", isSetApart: true), TextRun("”")])
        XCTAssertEqual(ConfirmationDialog(question(ControlTools.addTitle(ControlCaller.relay(clientName: "Cursor").shownAsker, "x"))).title,
                       [TextRun("An app calling itself “"), TextRun("Cursor", isSetApart: true), TextRun("” wants to add “"),
                        TextRun("x", isSetApart: true), TextRun("”")])
    }

    func testALongNameIsCutAndTheRestOfTheQuestionStays() {
        let long = String(repeating: "x", count: 100)
        XCTAssertEqual(ConfirmationDialog(question(ControlTools.addTitle("Claude", long))).title,
                       [TextRun("Claude wants to add “"), TextRun(String(repeating: "x", count: 63) + TextRun.cutMark, isSetApart: true),
                        TextRun("”")])
        let exact = String(repeating: "y", count: ConfirmationDialog.nameLimit)
        XCTAssertEqual(ConfirmationDialog(question(ControlTools.addTitle("Claude", exact))).title[1], TextRun(exact, isSetApart: true),
                       "a name at the limit is whole")
        // Counted in scalars, as the Windows mirror counts them: 64 of these are 128 UTF-16 units.
        let wide = String(repeating: "👍", count: ConfirmationDialog.nameLimit)
        XCTAssertEqual(ConfirmationDialog(question(ControlTools.addTitle("Claude", wide))).title[1].text, wide)
    }

    func testAQuoteNeverClosedOrAnEmptyNameStaysWords() {
        XCTAssertEqual(TextRun.names(in: "a “b", limit: 64), [TextRun("a “b")])
        XCTAssertEqual(TextRun.names(in: "“” x", limit: 64), [TextRun("“” x")])
        XCTAssertEqual(TextRun.names(in: "", limit: 64), [])
    }

    func testAPathOrCommandInACaptionIsSetApart() {
        XCTAssertEqual(TextRun.runs("Scripts can always use /x/connector-control", settingApart: ["/x/connector-control"]),
                       [TextRun("Scripts can always use "), TextRun("/x/connector-control", isSetApart: true)])
        XCTAssertEqual(TextRun.runs("Run connector-control help in Terminal.", settingApart: ["connector-control help"]),
                       [TextRun("Run "), TextRun("connector-control help", isSetApart: true), TextRun(" in Terminal.")])
        XCTAssertEqual(TextRun.runs("Not installed", settingApart: ["", "/x"]), [TextRun("Not installed")])
    }

    // MARK: What would run

    func testCodeLinesKeepTheirTextAndSayWhatAChangeRemovesAndAdds() {
        let lines = ControlMasking.changeLines(from: .object(["url": .string("https://a.example/mcp")]),
                                               to: .object(["url": .string("https://b.example/mcp")]), exposure: .dialog)
        let dialog = ConfirmationDialog(question("t", "m", code: lines, footer: ControlTools.takesEffectFooter))
        XCTAssertEqual(dialog.code, lines)
        XCTAssertEqual(dialog.codeKinds, [.plain, .removed, .added])
        XCTAssertEqual(dialog.footer, [TextRun(ControlTools.takesEffectFooter)])
    }

    /// The file a subscription reads is set apart in the footer, whole: which file it is is the question.
    func testThePathInTheFooterIsSetApartWhole() {
        let path = "/Users/me/Library/CloudStorage/OneDrive-Example/Team/a-rather-long-folder-name/team-tools.json"
        let dialog = ConfirmationDialog(question("t", footer: ControlTools.fromFooter(ControlText.escapedPath(path))))
        XCTAssertEqual(dialog.footer, [TextRun("From “"), TextRun(path, isSetApart: true), TextRun("”")])
    }

    /// Each set-apart piece is isolated where it is drawn, so right-to-left names side by side keep their
    /// order; words are drawn as they are.
    func testASetApartPieceIsIsolatedWhereItIsDrawn() {
        XCTAssertEqual(TextRun("אבג", isSetApart: true).shownText, "\u{2068}אבג\u{2069}")
        XCTAssertEqual(TextRun("words").shownText, "words")
    }

    // MARK: The confirmer

    /// `confirm` returns before anything is shown; the dialog comes on a turn of its own and its
    /// answer is the call's, once.
    func testTheQuestionIsPutOnATurnOfItsOwnAndAnsweredOnce() {
        let dialogs = FakeDialogs()
        var turns: [MainActorAction] = []
        let confirmer = DialogConfirmer(dialogs: dialogs, present: { turns.append($0) })
        var answers: [Bool] = []
        confirmer.confirm(restart) { answers.append($0) }
        XCTAssertTrue(dialogs.changeQuestions.isEmpty, "confirm returns before the dialog is shown")
        XCTAssertEqual(turns.count, 1)
        dialogs.nextChangeAnswer = true
        turns.removeFirst()()
        XCTAssertEqual(dialogs.changeQuestions, [ConfirmationDialog(restart)])
        XCTAssertEqual(answers, [true])
        confirmer.confirm(restart) { answers.append($0) }
        dialogs.nextChangeAnswer = false
        turns.removeFirst()()
        XCTAssertEqual(answers, [true, false], "Don’t Allow, or the window closed, answers false, once")
    }

    /// The confirmer the app uses, end to end over the tools: the caller hears it is waiting at 50 s,
    /// and the Allow given after that applies and is announced once, whatever "Notify on external
    /// changes" says.
    func testALateAllowThroughTheDialogAppliesAndIsAnnouncedOnce() {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.h.settings.notifyExternalChanges = false
        var turns: [MainActorAction] = []
        let tools = ControlTools(state: rig.state, settings: rig.settingsModel,
                                 confirmer: DialogConfirmer(dialogs: rig.h.dialogs, present: { turns.append($0) }), host: rig.h.host)
        let answer = ControlRig.Answer()
        tools.call(name: "add_connector", arguments: .object(["name": .string("x"), "config": .object(["command": .string("npx")])]),
                   caller: .relay(clientName: "claude-ai"), progress: { answer.progress.append($0) },
                   completion: { answer.result = $0; answer.completions += 1 })
        rig.h.drain()
        for _ in 0..<10 { rig.h.delays.runNext() }
        XCTAssertEqual(answer.result, ControlTools.waitingResult)
        rig.h.dialogs.nextChangeAnswer = true
        turns.removeFirst()()
        rig.h.drain()
        XCTAssertEqual(rig.h.dialogs.changeQuestions.first?.title.first, TextRun("Claude wants to add “"))
        XCTAssertNotNil(rig.state.store.mcps["x"])
        XCTAssertEqual(answer.completions, 1)
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [ControlTools.addedNotification("Claude", "x", "Default")])
    }
}
