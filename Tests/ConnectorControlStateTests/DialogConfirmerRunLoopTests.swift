import AppKit
import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// Mac only: the Windows counterpart is TheCallersWaitGoesOnWhileTheDialogIsOpen in
/// windows/tests/ConnectorControl.App.Tests/ConfirmationDialogTests.cs.
///
/// The app's alert is modal, and on the Mac a modal's nested run loop entered from inside a block on
/// the main queue runs no other main-queue block — and every line from the socket arrives in one.
/// Here the call arrives that way, the dialog holds the main thread in a nested run loop in AppKit's
/// modal-panel mode, as `NSAlert.runModal` does, and the tools' delays are the main queue's own, a
/// thousand times faster.
@MainActor
final class DialogConfirmerRunLoopTests: XCTestCase {
    /// A modal dialog: it holds the main thread in a nested run loop until `until()` holds or five
    /// seconds pass, then answers Allow. The five seconds end it whatever the test asserts, so with the
    /// test's own wait the whole test is over within about thirty-five seconds, never hung.
    private final class ModalDialogs: Dialogs {
        let until: () -> Bool
        private(set) var shown = 0
        private(set) var answered = false

        init(until: @escaping () -> Bool) { self.until = until }

        func confirmChange(_ dialog: ConfirmationDialog) -> Bool {
            shown += 1
            let deadline = Date().addingTimeInterval(5)
            while !until(), Date() < deadline {
                _ = RunLoop.current.run(mode: DialogConfirmer.modalPanelMode, before: Date().addingTimeInterval(0.01))
            }
            answered = true
            return true
        }

        func confirm(message: String, informative: String?, primary: String, cancel: String, destructive: Bool,
                     cancelIsDefault: Bool) -> Bool { false }
        func promptForName(title: String, initial: String) -> String? { nil }
        func inform(message: String, informative: String?) {}
    }

    func testTheCallersWaitGoesOnWhileTheDialogIsOpen() {
        // AppKit puts its modal-panel mode among the common modes the main queue is served in, as it does in the app.
        _ = NSApplication.shared
        let rig = ControlRig(); defer { rig.dispose() }
        let host = AppHost(marshal: { work in DispatchQueue.main.async { MainActor.assumeIsolated(work) } },
                           delay: { seconds, work in
                               DispatchQueue.main.asyncAfter(deadline: .now() + seconds / 1000) { MainActor.assumeIsolated(work) }
                           },
                           now: { Date() }, background: { DispatchQueue.global(qos: .utility).async(execute: $0) })
        let answer = ControlRig.Answer()
        let dialogs = ModalDialogs { answer.result != nil }
        let tools = ControlTools(state: rig.state, settings: rig.settingsModel, confirmer: DialogConfirmer(dialogs: dialogs), host: host)
        // As a line from the socket arrives: inside a block on the main queue.
        DispatchQueue.main.async {
            MainActor.assumeIsolated {
                tools.call(name: "add_connector",
                           arguments: .object(["name": .string("x"), "config": .object(["command": .string("npx")])]),
                           caller: .relay(clientName: "claude-ai"), progress: { answer.progress.append($0) },
                           completion: { answer.result = $0; answer.completions += 1 })
            }
        }
        let deadline = Date().addingTimeInterval(Wait.eventually)
        while !(dialogs.answered && rig.state.store.mcps["x"] != nil), Date() < deadline {
            _ = RunLoop.main.run(mode: .default, before: Date().addingTimeInterval(0.01))
        }
        XCTAssertEqual(dialogs.shown, 1)
        XCTAssertEqual(answer.progress, [5, 10, 15, 20, 25, 30, 35, 40, 45, 50], "progress went out while the dialog was open")
        XCTAssertEqual(answer.result, ControlTools.waitingResult, "and so did the early answer")
        XCTAssertEqual(answer.completions, 1)
        XCTAssertNotNil(rig.state.store.mcps["x"], "the Allow given after it still counts")
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [ControlTools.addedNotification("Claude", "x", "Default")])
    }
}
