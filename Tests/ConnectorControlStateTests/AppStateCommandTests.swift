import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/AppStateCommandTests.cs. The
/// restart completion is posted to the marshal queue, so every restart test
/// pumps once where the C# awaited and pumped.
@MainActor
final class AppStateCommandTests: XCTestCase {
    private let fixture = ["aws-mcp", "scoutbook", "service-now"]

    func testQuitAsksForConfirmationByDefault() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        var quit = 0
        state.quitRequested = { quit += 1 }
        h.dialogs.nextConfirm = false
        state.quitApp()
        XCTAssertEqual(quit, 0)
        XCTAssertEqual(h.dialogs.confirms[0], FakeDialogs.ConfirmCall(
            message: "Quit Connector Control?", informative: nil, primary: "Quit", cancel: "Cancel", destructive: false))
        h.dialogs.nextConfirm = true
        state.quitApp()
        XCTAssertEqual(quit, 1)
    }

    func testQuitSkipsTheConfirmationWhenDisabled() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.settings.confirmBeforeQuit = false
        let state = h.create()
        var quit = 0
        state.quitRequested = { quit += 1 }
        state.quitApp()
        XCTAssertEqual(quit, 1)
        XCTAssertTrue(h.dialogs.confirms.isEmpty)
    }

    func testRestartClaudeConfirmsThenRestarts() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.restartClaude()
        XCTAssertEqual(h.dialogs.confirms[0], FakeDialogs.ConfirmCall(
            message: "Restart Claude Desktop now?",
            informative: "Any in-progress Claude conversation will be interrupted.",
            primary: "Restart", cancel: "Cancel", destructive: false))
        XCTAssertEqual(h.claude.restartCalls, 1)
    }

    func testRestartClaudeCancelledDoesNothing() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        h.dialogs.nextConfirm = false
        state.restartClaude()
        XCTAssertEqual(h.claude.restartCalls, 0)
    }

    func testRestartClaudeSkipsTheConfirmationWhenDisabled() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.settings.confirmBeforeRestart = false
        let state = h.create()
        state.restartClaude()
        XCTAssertTrue(h.dialogs.confirms.isEmpty)
        XCTAssertEqual(h.claude.restartCalls, 1)
    }

    func testRestartErrorLandsInLastErrorAndARecheckIsScheduled() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.settings.confirmBeforeRestart = false
        h.settings.lastApplyDate = h.now
        h.claude.isRunning = true
        h.claude.launchDate = h.now.addingTimeInterval(-3600)
        let state = h.create()
        XCTAssertTrue(state.needsClaudeRestart)
        h.claude.restartResult = "Claude didn’t quit (it may be showing a dialog). Quit it manually, then click Restart Claude again."
        state.restartClaude()
        XCTAssertEqual(h.ui.pending, 1, "the completion is posted through the host, not run inline")
        h.ui.pump()
        XCTAssertEqual(state.lastError, h.claude.restartResult)
        XCTAssertTrue(state.needsClaudeRestart)
        XCTAssertEqual(h.delays.pending.map(\.delay), [3])
        h.claude.isRunning = false   // the user quit it by hand in the meantime
        h.delays.runNext()
        XCTAssertFalse(state.needsClaudeRestart)
    }

    func testRestartSuccessClearsTheErrorAndTheRestartState() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.settings.confirmBeforeRestart = false
        h.settings.lastApplyDate = h.now
        h.claude.isRunning = true
        h.claude.launchDate = h.now.addingTimeInterval(-3600)
        let state = h.create()
        state.lastError = "old banner"
        h.claude.onRestart = { h.claude.launchDate = h.now.addingTimeInterval(1) }
        state.restartClaude()
        h.ui.pump()
        XCTAssertNil(state.lastError)
        XCTAssertFalse(state.needsClaudeRestart)
    }

    func testNotificationRestartActionIsGuardedByAPendingRestartAndSkipsTheConfirmation() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        h.notifier.activateRestart()   // stale click: nothing pending
        XCTAssertEqual(h.claude.restartCalls, 0)

        h.claude.isRunning = true
        h.claude.launchDate = h.now.addingTimeInterval(-3600)
        state.setEnabled("aws-mcp", false)
        XCTAssertTrue(state.needsClaudeRestart)
        h.notifier.activateRestart()
        XCTAssertEqual(h.claude.restartCalls, 1)
        XCTAssertTrue(h.dialogs.confirms.isEmpty)   // the explicit action click IS the confirmation
    }

    func testDisposeUnsubscribesTheNotificationRestartAction() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        h.claude.isRunning = true
        h.claude.launchDate = h.now.addingTimeInterval(-3600)
        state.setEnabled("aws-mcp", false)
        XCTAssertTrue(state.needsClaudeRestart)   // a pending restart, so activateRestart would act if still wired up
        state.dispose()
        h.notifier.activateRestart()
        XCTAssertEqual(h.claude.restartCalls, 0)
    }

    func testPendingRestartDelaysAreNoOpsAfterDispose() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.settings.confirmBeforeRestart = false
        h.settings.lastApplyDate = h.now
        h.claude.isRunning = true
        h.claude.launchDate = h.now.addingTimeInterval(-3600)
        let state = h.create()
        state.restartClaude()
        h.ui.pump()
        XCTAssertEqual(h.delays.pending.count, 1)
        let errorBefore = state.lastError
        let needsRestartBefore = state.needsClaudeRestart
        h.claude.isRunning = false   // without the guard the recheck would now clear needsClaudeRestart
        state.dispose()
        h.delays.runNext()   // 3 s recheck: must be a no-op post-dispose, not a crash
        XCTAssertEqual(state.lastError, errorBefore)
        XCTAssertEqual(state.needsClaudeRestart, needsRestartBefore)
    }

    func testSwitchProfileAppliesImmediately() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        h.dialogs.nextPromptAnswer = "Work"
        state.newProfile()
        XCTAssertEqual(state.activeProfile, "Work")
        state.setEnabled("aws-mcp", false)
        XCTAssertEqual(try h.claudeServers().keys.sorted(), ["scoutbook", "service-now"])

        h.settings.lastApplyDate = nil
        state.switchProfile(to: "Default")
        XCTAssertEqual(state.activeProfile, "Default")
        XCTAssertEqual(try h.claudeServers().keys.sorted(), fixture)
        XCTAssertEqual(h.settings.lastApplyDate, h.now)
        XCTAssertEqual(try h.storeOnDisk().activeProfile, "Default")
    }

    func testSwitchProfileIgnoresAnUnknownName() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.switchProfile(to: "Nope")
        XCTAssertEqual(state.activeProfile, "Default")
        XCTAssertNil(state.lastError)
        XCTAssertNil(h.settings.lastApplyDate)
    }

    func testNewProfilePromptTextAndCancel() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        h.dialogs.nextPromptAnswer = nil
        state.newProfile()
        XCTAssertEqual(h.dialogs.prompts[0], FakeDialogs.PromptCall(title: "New Profile", initial: ""))
        XCTAssertEqual(state.profileNames, ["Default"])

        h.dialogs.nextPromptAnswer = "Work"
        state.newProfile()
        XCTAssertEqual(state.profileNames, ["Default", "Work"])
        XCTAssertEqual(state.sortedNames, fixture)   // a COPY of the active profile
        XCTAssertEqual(h.settings.lastApplyDate, h.now)
    }

    func testNewProfileErrorsGoToLastError() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        h.dialogs.nextPromptAnswer = "Default"
        state.newProfile()
        XCTAssertEqual(state.lastError, "A profile named “Default” already exists.")
        h.dialogs.nextPromptAnswer = "   "
        state.newProfile()
        XCTAssertEqual(state.lastError, "Name must not be empty.")
        XCTAssertEqual(state.profileNames, ["Default"])
    }

    func testRenameProfilePrefillsTheActiveName() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        h.dialogs.nextPromptAnswer = "Main"
        state.renameProfile()
        XCTAssertEqual(h.dialogs.prompts[0], FakeDialogs.PromptCall(title: "Rename Profile", initial: "Default"))
        XCTAssertEqual(state.activeProfile, "Main")
        XCTAssertEqual(try h.storeOnDisk().activeProfile, "Main")
        XCTAssertEqual(h.settings.lastApplyDate, h.now)
    }

    func testDeleteProfileConfirmTextAndLastProfileError() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        state.deleteProfile()
        XCTAssertEqual(h.dialogs.confirms[0], FakeDialogs.ConfirmCall(
            message: "Delete Profile “Default”?",
            informative: "Its connector list is removed; backups keep prior states.",
            primary: "Delete", cancel: "Cancel", destructive: true))
        XCTAssertEqual(state.lastError, "Can’t delete the last profile.")
        XCTAssertEqual(state.profileNames, ["Default"])
    }

    func testDeleteProfileSwitchesToTheAlphabeticallyFirstRemaining() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        h.dialogs.nextPromptAnswer = "Zeta"
        state.newProfile()
        h.dialogs.nextPromptAnswer = "Work"
        state.newProfile()
        XCTAssertEqual(state.activeProfile, "Work")
        h.dialogs.nextConfirm = false
        state.deleteProfile()
        XCTAssertEqual(state.profileNames, ["Default", "Work", "Zeta"])   // cancelled
        h.dialogs.nextConfirm = true
        state.deleteProfile()
        XCTAssertEqual(state.profileNames, ["Default", "Zeta"])
        XCTAssertEqual(state.activeProfile, "Default")
        XCTAssertEqual(try h.storeOnDisk().activeProfile, "Default")
    }
}
