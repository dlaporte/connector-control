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
        let long = String(repeating: "a", count: 50) + String(repeating: "z", count: 50)
        XCTAssertEqual(ConfirmationDialog(question(ControlTools.addTitle("Claude", long))).title,
                       [TextRun("Claude wants to add “"),
                        TextRun(String(repeating: "a", count: 31) + TextRun.cutMark + String(repeating: "z", count: 32), isSetApart: true),
                        TextRun("”")])
        let exact = String(repeating: "y", count: ConfirmationDialog.nameLimit)
        XCTAssertEqual(ConfirmationDialog(question(ControlTools.addTitle("Claude", exact))).title[1], TextRun(exact, isSetApart: true),
                       "a name at the limit is whole")
        // Counted in scalars, as the Windows mirror counts them: 64 of these are 128 UTF-16 units.
        let wide = String(repeating: "👍", count: ConfirmationDialog.nameLimit)
        XCTAssertEqual(ConfirmationDialog(question(ControlTools.addTitle("Claude", wide))).title[1].text, wide)
    }

    /// A long name is cut in the middle, so two names that differ only at their ends read apart, and the
    /// cut's own "…" is the only raw one: a name's own shows as its code.
    func testNamesPastTheLimitStillReadApart() {
        let prefix = String(repeating: "p", count: 63)
        let one = ConfirmationDialog(question(ControlTools.deleteTitle("Claude", ControlText.quotedList([prefix + "-prod-1"]))))
        let other = ConfirmationDialog(question(ControlTools.deleteTitle("Claude", ControlText.quotedList([prefix + "-prod-2"]))))
        XCTAssertNotEqual(one.title, other.title)
        XCTAssertEqual(one.title[1].text, String(repeating: "p", count: 31) + TextRun.cutMark + String(repeating: "p", count: 25) + "-prod-1")
        XCTAssertEqual(one.title[1].text.unicodeScalars.count, ConfirmationDialog.nameLimit)
        // A name that ends in its own "…" cannot pass for one that was cut.
        XCTAssertEqual(ControlText.escapedName("name\u{2026}"), #"name\u{2026}"#)
        let mimic = String(repeating: "m", count: 63) + "\u{2026}"
        XCTAssertFalse(ConfirmationDialog(question(ControlTools.addTitle("Claude", ControlText.escapedName(mimic)))).title[1].text
                        .hasSuffix(String(repeating: "m", count: 32) + TextRun.cutMark))
    }

    /// The name an app gives itself is cut as any other name is, wherever it is shown: a megabyte of it
    /// pushes neither the question nor what a notification says happened out of sight.
    func testAnAppsOwnNameIsCutLikeAnyOtherName() {
        let name = String(repeating: "a", count: 1_000_000 - 3) + "end"
        let cut = String(repeating: "a", count: 31) + TextRun.cutMark + String(repeating: "a", count: 29) + "end"
        let asker = ControlCaller.relay(clientName: name).shownAsker
        XCTAssertEqual(asker, ControlCaller.callingItselfText(cut))
        XCTAssertEqual(ConfirmationDialog(question(ControlTools.addTitle(asker, "x"))).title,
                       [TextRun("An app calling itself “"), TextRun(cut, isSetApart: true), TextRun("” wants to add “"),
                        TextRun("x", isSetApart: true), TextRun("”")])
        XCTAssertEqual(ControlTools.addedNotification(asker, "x", "Harvard"), "An app calling itself “\(cut)” added “x” to “Harvard”.")
        let exact = String(repeating: "e", count: ConfirmationDialog.nameLimit)
        XCTAssertEqual(ControlCaller.relay(clientName: exact).shownAsker, ControlCaller.callingItselfText(exact), "a name at the limit is whole")
    }

    /// The end kept after the "…" never starts with a combining mark, which would be drawn on the "…" and
    /// could pass for part of the cut; marks there are left out with the character they belonged to.
    func testTheEndKeptAfterTheCutNeverStartsWithAMark() {
        let name = ControlText.escapedName(String(repeating: "x", count: 68) + "\u{0301}\u{0301}" + String(repeating: "y", count: 30))
        let shown = ConfirmationDialog(question(ControlTools.addTitle("Claude", name))).title[1].text
        XCTAssertEqual(shown, String(repeating: "x", count: 31) + TextRun.cutMark + String(repeating: "y", count: 30))
        let after = Array(shown.unicodeScalars)[32]
        XCTAssertFalse(ControlText.isCombiningMark(after))
    }

    /// The cut never splits a character's escape: an escape is kept whole on its side of the "…", or left
    /// out whole.
    func testTheCutNeverSplitsAnEscape() {
        // An escape across the 31st scalar, and one across the last 32.
        let name = ControlText.escapedName(String(repeating: "a", count: 27) + "\u{202E}" + String(repeating: "b", count: 40)
                                           + "\u{202E}" + String(repeating: "c", count: 27))
        let shown = ConfirmationDialog(question(ControlTools.addTitle("Claude", name))).title[1].text
        XCTAssertEqual(shown, String(repeating: "a", count: 27) + TextRun.cutMark + String(repeating: "c", count: 27))
        let backslashes = ControlText.escapedName(String(repeating: "\\", count: 40))
        let cut = ConfirmationDialog(question(ControlTools.addTitle("Claude", backslashes))).title[1].text
        XCTAssertEqual(cut, String(repeating: "\\\\", count: 15) + TextRun.cutMark + String(repeating: "\\\\", count: 16))
        XCTAssertLessThanOrEqual(cut.unicodeScalars.count, ConfirmationDialog.nameLimit)
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

    /// The object replacement character is drawn as nothing, so it shows as its code: a rename that only
    /// adds one, or a variable whose name only gains one, reads as the change it is.
    func testAnObjectReplacementCharacterShowsAsItsCode() {
        XCTAssertEqual(ControlTools.renamedText(ControlText.escapedName("github"), ControlText.escapedName("github\u{FFFC}")),
                       #"Renamed “github” to “github\u{FFFC}”."#)
        let rename = ConfirmationDialog(question(ControlTools.changeTitle("Claude", ControlText.escapedName("github")),
                                                 code: [ControlMasking.removedPrefix + ControlText.escapedName("github"),
                                                        ControlMasking.addedPrefix + ControlText.escapedName("github\u{FFFC}")]))
        XCTAssertEqual(rename.code.last, ControlMasking.addedPrefix + #"github\u{FFFC}"#)
        let lines = ControlMasking.changeLines(from: .object(["command": .string("npx"), "env": .object(["READ_ONLY": .string("1")])]),
                                               to: .object(["command": .string("npx"), "env": .object(["READ_ONLY\u{FFFC}": .string("1")])]),
                                               exposure: .dialog)
        XCTAssertTrue(lines.contains { $0.contains(#"READ_ONLY\u{FFFC}"#) }, "\(lines)")
        XCTAssertFalse(lines.contains { $0.unicodeScalars.contains("\u{FFFC}") }, "\(lines)")
        XCTAssertEqual(ControlText.escaped("a\u{FFFD}"), "a\u{FFFD}", "the replacement character shows, so it stays")
    }

    // MARK: What would run

    func testCodeLinesKeepTheirTextAndSayWhatAChangeRemovesAndAdds() {
        let lines = ControlMasking.changeLines(from: .object(["url": .string("https://a.example/mcp")]),
                                               to: .object(["url": .string("https://b.example/mcp")]), exposure: .dialog)
        let dialog = ConfirmationDialog(question("t", "m", code: lines, footer: ControlTools.takesEffectFooter))
        XCTAssertEqual(dialog.code, lines)
        XCTAssertEqual(dialog.codeKinds, [.plain, .removed, .added])
        XCTAssertEqual(dialog.footer, [TextRun(ControlTools.takesEffectFooter)])
        // An update lists each connector under its heading, its lines indented once.
        let update = [ControlTools.connectorHeading("splunk", ReviewModel.changedLabel)] + ControlTools.indented(lines)
        XCTAssertEqual(ConfirmationDialog(question("t", code: update)).codeKinds, [.plain, .plain, .removed, .added])
        // A line that continues a field is padded past its label, so a variable named "−" stays plain.
        let added = ControlMasking.codeLines(.object(["command": .string("npx"), "env": .object(["A": .string("1"), "−": .string("2")])]),
                                             exposure: .dialog)
        XCTAssertEqual(ConfirmationDialog(question("t", code: added)).codeKinds, [.plain, .plain, .plain])
        XCTAssertEqual(ConfirmationDialog(question("t", code: ControlTools.indented(added))).codeKinds, [.plain, .plain, .plain])
    }

    /// A code line that holds a right-to-left letter is drawn with every character left to right in the
    /// order it is stored, so the letter cannot carry the numbers and arguments beside it into another
    /// order; a line with none is drawn, and copied, as it is. What the question holds, and what Allow
    /// compares, stays the line itself.
    func testACodeLineIsDrawnInTheOrderItIsStored() {
        let line = "--ports \u{0627} 80 443 22"
        let request = question("t", code: [line, "plain"])
        let dialog = ConfirmationDialog(request)
        XCTAssertEqual(dialog.shownCode, ["\u{202D}" + line + "\u{202C}", "plain"])
        // Each range, at both its ends: the same list the Windows mirror holds.
        let ends: [UInt32] = [0x0590, 0x08FF, 0xFB1D, 0xFDFF, 0xFE70, 0xFEFF, 0x10800, 0x10FFF, 0x1E800, 0x1EFFF]
        for value in ends {
            XCTAssertTrue(ConfirmationDialog.hasRightToLeft("a" + String(Unicode.Scalar(value)!)), String(value, radix: 16))
        }
        for plain in ["url  https://a.example/mcp", "caf\u{E9} \u{0E17}\u{0E35}\u{0E48}", "\u{058F}", "\u{0900}", "\u{FB1C}", "\u{1F600}", ""] {
            XCTAssertFalse(ConfirmationDialog.hasRightToLeft(plain), plain)
            XCTAssertEqual(ConfirmationDialog(question("t", code: [plain])).shownCode, [plain])
        }
        XCTAssertEqual(dialog.code, [line, "plain"])
        XCTAssertTrue(TextRun.same(dialog.code[0], line))
        XCTAssertEqual(request, question("t", code: [line, "plain"]))
        XCTAssertEqual(dialog, ConfirmationDialog(question("t", code: [line, "plain"])))
        XCTAssertEqual(ControlText.escaped("\u{202D}x\u{202C}"), #"\u{202D}x\u{202C}"#, "a line can hold neither, so none can end the wrapper early")
    }

    /// A code line's box grows past the code face's own line only as far as the line's letters, drawn in a
    /// fallback face, need: its marks are left out of the measure, and it never grows more than half a line
    /// above or below, so the box stays bounded whatever a line holds.
    func testACodeLineGrowsOnlyForLettersAndOnlyHalfALine() {
        XCTAssertEqual(ControlText.withoutMarks("  + l\u{1DD8}\u{1DD8} \u{0E17}\u{0E35}\u{0E48} cafe\u{0301}"), "  + l \u{0E17} cafe")
        XCTAssertEqual(ControlText.withoutMarks("\u{0939}\u{093F}\u{0928}\u{094D}"), "\u{0939}\u{093F}\u{0928}", "a spacing mark stays")
        XCTAssertEqual(ConfirmationDialog.extraRoom(needed: 10.6, own: 10.6, lineHeight: 14), 0)
        XCTAssertEqual(ConfirmationDialog.extraRoom(needed: 9, own: 10.6, lineHeight: 14), 0, "never less than the face's own")
        XCTAssertEqual(ConfirmationDialog.extraRoom(needed: 11.9, own: 10.6, lineHeight: 14), 2, "whole points, rounded up")
        XCTAssertEqual(ConfirmationDialog.extraRoom(needed: 40, own: 10.6, lineHeight: 14), 7, "at most half a line")
    }

    /// Text is compared scalar by scalar, as the Windows mirror compares it ordinally: a name composed
    /// another way is another name.
    func testRunsAndDialogsCompareTextScalarByScalar() {
        XCTAssertNotEqual(TextRun("caf\u{E9}", isSetApart: true), TextRun("cafe\u{301}", isSetApart: true))
        XCTAssertNotEqual(TextRun("x", isSetApart: true), TextRun("x"))
        XCTAssertNotEqual(ConfirmationDialog(question("t", code: ["caf\u{E9}"])), ConfirmationDialog(question("t", code: ["cafe\u{301}"])))
        XCTAssertEqual(ConfirmationDialog(restart), ConfirmationDialog(restart))
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

    /// The confirmer the app uses, end to end over the tools: while the dialog is up, the caller hears
    /// it is waiting at 50 s, and the Allow given after that applies and is announced once, whatever
    /// "Notify on external changes" says.
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
        XCTAssertNil(answer.result)
        var heardWhileAsking: ControlResult?
        // The ticks run while the question is up, as the modal dialog's run loop runs them.
        rig.h.dialogs.duringChange = {
            for _ in 0..<10 { rig.h.delays.runNext() }
            heardWhileAsking = answer.result
            XCTAssertNil(rig.state.store.mcps["x"], "nothing is applied before the answer")
        }
        rig.h.dialogs.nextChangeAnswer = true
        turns.removeFirst()()
        rig.h.drain()
        XCTAssertEqual(heardWhileAsking, ControlTools.waitingResult)
        XCTAssertEqual(answer.result, ControlTools.waitingResult, "the caller hears once")
        XCTAssertEqual(rig.h.dialogs.changeQuestions.first?.title.first, TextRun("Claude wants to add “"))
        XCTAssertNotNil(rig.state.store.mcps["x"])
        XCTAssertEqual(answer.completions, 1)
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [ControlTools.addedNotification("Claude", "x", "Default")])
    }
}
