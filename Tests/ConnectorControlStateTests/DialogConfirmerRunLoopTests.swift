import AppKit
import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// Mac only: the Windows counterpart is TheCallersWaitGoesOnWhileTheDialogIsOpen in
/// windows/tests/ConnectorControl.App.Tests/ConfirmationDialogTests.cs.
///
/// The app's alert is modal, and on the Mac a modal's nested run loop entered from inside a block on
/// the main queue runs no other main-queue block — and every line from the socket arrives in one.
/// Here the call arrives that way, each dialog holds the main thread in a nested run loop in AppKit's
/// modal-panel mode, as `NSAlert.runModal` does, and the tools' delays are the main queue's own, a
/// thousand times faster.
@MainActor
final class DialogConfirmerRunLoopTests: XCTestCase {
    /// The app's dialogs, modal: each holds the main thread in a nested run loop until its condition
    /// holds or five seconds pass, and a change question then answers Allow. The five seconds end each
    /// whatever the test asserts, so with the test's own wait a test is over within about forty seconds,
    /// never hung. Once the test is over (`close()`), a question still to come answers Don’t Allow at once.
    private final class ModalDialogs: Dialogs {
        /// What a change question waits for before it answers, and how much progress the call has heard.
        var changeUntil: () -> Bool = { true }
        var heard: () -> Int = { 0 }
        /// What another of the app's alerts waits for, and what happens once it is up.
        var informUntil: () -> Bool = { true }
        var whileInformIsUp: () -> Void = {}
        private(set) var shown = 0
        private(set) var answered = false
        /// For each change question as it was shown: the progress its call had heard, and whether another
        /// alert was up beneath it.
        private(set) var heardWhenShown: [Int] = []
        private(set) var shownOverAnother: [Bool] = []
        private(set) var informed = false
        private var informIsUp = false
        private var closed = false

        func close() { closed = true }

        func confirmChange(_ dialog: ConfirmationDialog) -> Bool {
            guard !closed else { return false }
            shown += 1
            heardWhenShown.append(heard())
            shownOverAnother.append(informIsUp)
            hold(until: changeUntil)
            answered = true
            return true
        }

        func inform(message: String, informative: String?) {
            guard !closed else { return }
            informIsUp = true
            whileInformIsUp()
            hold(until: informUntil)
            informIsUp = false
            informed = true
        }

        func confirm(message: String, informative: String?, primary: String, cancel: String, destructive: Bool,
                     cancelIsDefault: Bool) -> Bool { false }
        func promptForName(title: String, initial: String) -> String? { nil }

        /// Ends with a wake-up for the main run loop: the run this one is nested in lent its own wake-up
        /// to the nested runs, and without one it would sleep on — minutes, measured — until something
        /// else stirred the main thread.
        private func hold(until: () -> Bool) {
            let deadline = Date().addingTimeInterval(5)
            while !until(), Date() < deadline {
                _ = RunLoop.current.run(mode: DialogConfirmer.modalPanelMode, before: Date().addingTimeInterval(0.01))
            }
            CFRunLoopWakeUp(CFRunLoopGetMain())
        }
    }

    func testTheCallersWaitGoesOnWhileTheDialogIsOpen() {
        // AppKit puts its modal-panel mode among the common modes the main queue is served in, as it does in the app.
        _ = NSApplication.shared
        let rig = ControlRig(); defer { rig.dispose() }
        let answer = ControlRig.Answer()
        let dialogs = ModalDialogs()
        dialogs.changeUntil = { answer.result != nil }
        dialogs.heard = { answer.progress.count }
        defer { Self.close(dialogs) }
        let tools = ControlTools(state: rig.state, settings: rig.settingsModel, confirmer: DialogConfirmer(dialogs: dialogs),
                                 host: Self.host)
        Self.ask(tools, adding: "x", answer)
        Self.runMainLoop { dialogs.answered && rig.state.store.mcps["x"] != nil }
        guard dialogs.shown == 1 else { return XCTFail("the question was shown \(dialogs.shown) times, not once") }
        XCTAssertEqual(dialogs.heardWhenShown, [0], "the question was put at once, not after the call's early answer")
        XCTAssertEqual(answer.progress, [5, 10, 15, 20, 25, 30, 35, 40, 45, 50], "progress went out while the dialog was open")
        XCTAssertEqual(answer.result, ControlTools.waitingResult, "and so did the early answer")
        XCTAssertEqual(answer.completions, 1)
        XCTAssertNotNil(rig.state.store.mcps["x"], "the Allow given after it still counts")
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [ControlTools.addedNotification("Claude", "x", "Default")])
    }

    /// A question that arrives while another of the app's alerts is up — Settings' refusal, shown as the
    /// app shows it — is put on top of it at once, and its call's wait goes on inside both. The modal-panel
    /// mode in the presenter's modes is what lets it in; without it the question would wait for the other
    /// alert's answer. (Claude's own questions never stack: a second waits its turn behind the first.)
    func testAQuestionThatArrivesWhileAnotherAlertIsUpIsShownOnTopOfIt() {
        _ = NSApplication.shared
        let rig = ControlRig(); defer { rig.dispose() }
        let answer = ControlRig.Answer()
        let dialogs = ModalDialogs()
        dialogs.changeUntil = { answer.result != nil }
        dialogs.heard = { answer.progress.count }
        dialogs.informUntil = { [unowned dialogs] in dialogs.answered }
        defer { Self.close(dialogs) }
        let tools = ControlTools(state: rig.state, settings: rig.settingsModel, confirmer: DialogConfirmer(dialogs: dialogs),
                                 host: Self.host)
        // Claude's line arrives once the other alert is up.
        dialogs.whileInformIsUp = { Self.ask(tools, adding: "x", answer) }
        DialogConfirmer.nextRunLoopTurn {
            dialogs.inform(message: SettingsModel.claudeAppRejectedTitle, informative: nil)
        }
        Self.runMainLoop { dialogs.informed && rig.state.store.mcps["x"] != nil }
        guard dialogs.shown == 1 else { return XCTFail("the question was shown \(dialogs.shown) times, not once") }
        XCTAssertEqual(dialogs.shownOverAnother, [true], "the question was put on top of the other alert, not after it")
        XCTAssertEqual(dialogs.heardWhenShown, [0], "at once")
        XCTAssertEqual(answer.progress, [5, 10, 15, 20, 25, 30, 35, 40, 45, 50], "progress went out while both were open")
        XCTAssertEqual(answer.result, ControlTools.waitingResult, "and so did the early answer")
        XCTAssertEqual(answer.completions, 1)
        XCTAssertNotNil(rig.state.store.mcps["x"], "the Allow given after it still counts")
    }

    /// The app's host, with the tools' delays the main queue's own, a thousand times faster.
    private static let host = AppHost(
        marshal: { work in DispatchQueue.main.async { MainActor.assumeIsolated(work) } },
        delay: { seconds, work in
            DispatchQueue.main.asyncAfter(deadline: .now() + seconds / 1000) { MainActor.assumeIsolated(work) }
        },
        now: { Date() }, background: { DispatchQueue.global(qos: .utility).async(execute: $0) })

    /// Claude asks to add `name`, as a line from the socket arrives: inside a block on the main queue.
    private static func ask(_ tools: ControlTools, adding name: String, _ answer: ControlRig.Answer) {
        DispatchQueue.main.async {
            MainActor.assumeIsolated {
                tools.call(name: "add_connector",
                           arguments: .object(["name": .string(name), "config": .object(["command": .string("npx")])]),
                           caller: .relay(clientName: "claude-ai"), progress: { answer.progress.append($0) },
                           completion: { answer.result = $0; answer.completions += 1 })
            }
        }
    }

    /// The main run loop in its default mode, as the app runs it between events, until `done` holds or
    /// `Wait.eventually` passes.
    private static func runMainLoop(until done: () -> Bool) {
        let deadline = Date().addingTimeInterval(Wait.eventually)
        while !done(), Date() < deadline {
            _ = RunLoop.main.run(mode: .default, before: Date().addingTimeInterval(0.01))
        }
    }

    /// Ends the test's dialogs and runs what is left on the main run loop now, so a question posted here
    /// but never shown is answered Don’t Allow here, never inside a later test.
    private static func close(_ dialogs: ModalDialogs) {
        dialogs.close()
        _ = RunLoop.main.run(mode: .default, before: Date())
    }
}
