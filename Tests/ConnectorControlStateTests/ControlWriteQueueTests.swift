import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlWriteQueueTests.cs
///
/// The write queue, driven by writes the test controls: every write is prepared through the tools'
/// `prepareOverride`, asks a question of its own when its tool asks, and records that it ran.
@MainActor
final class ControlWriteQueueTests: XCTestCase {
    /// The writes that ran, in order, by the name or collection each was given.
    private var performed: [String] = []

    private static func question(_ label: String) -> ConfirmationRequest {
        ConfirmationRequest(title: "Allow \(label)?", message: label, code: [], footer: nil)
    }

    /// Every write prepared as one that asks about its label, and answers "Did <label>." once it runs.
    private func controlWrites(_ rig: ControlRig) {
        rig.tools.prepareOverride = { [unowned self] name, arguments, _ in
            let label = arguments.string("collection") ?? arguments.string("name") ?? name
            return .ready(ControlWriteQueueTests.question(label), perform: {
                self.performed.append(label)
                return ControlOutcome(result: ControlResult(structured: .object([:]), text: "Did \(label).", isError: false),
                                      notification: nil)
            })
        }
    }

    /// A gated write: it asks while confirmations are on.
    @discardableResult
    private func add(_ rig: ControlRig, _ name: String) -> ControlRig.Answer {
        rig.start("add_connector", ["name": .string(name), "config": .object([:])])
    }

    /// A direct write: it never asks.
    @discardableResult
    private func switchTo(_ rig: ControlRig, _ collection: String, as caller: ControlCaller = .relay(clientName: "claude-ai")) -> ControlRig.Answer {
        rig.start("switch_collection", ["collection": .string(collection)], as: caller)
    }

    /// Lets every waiting call's clock run out: each reports progress until its early answer.
    private func runOutTheClock(_ rig: ControlRig) {
        var runs = 0
        while !rig.h.delays.pending.isEmpty, runs < 100 {
            rig.h.delays.runNext()
            runs += 1
        }
        XCTAssertTrue(rig.h.delays.pending.isEmpty, "every waiting call has had its early answer")
    }

    func testAnAllowAfterTheIntegrationWasTurnedOffAppliesNothing() {
        let rig = ControlRig(); defer { rig.dispose() }
        controlWrites(rig)
        let answer = add(rig, "a")
        XCTAssertEqual(rig.confirmer.waitingCount, 1)
        XCTAssertNil(rig.state.setClaudeIntegrationEnabled(false))
        rig.confirmer.answer(true)
        XCTAssertEqual(answer.result, .refusal(ControlTools.integrationOffError))
        XCTAssertEqual(performed, [])
    }

    func testAWriteWaitingItsTurnIsDecidedAgainWhenItComes() {
        let rig = ControlRig(); defer { rig.dispose() }
        controlWrites(rig)
        let asking = add(rig, "a")
        let queued = switchTo(rig, "b")
        XCTAssertNil(queued.result, "it waits behind the open dialog")
        XCTAssertNil(rig.state.setClaudeIntegrationEnabled(false))
        rig.confirmer.answer(false)
        XCTAssertEqual(asking.result, .refusal(ControlTools.declinedError))
        XCTAssertEqual(queued.result, .refusal(ControlTools.integrationOffError))
        XCTAssertEqual(performed, [])
    }

    /// Turning confirmations off loosens the gate rather than closing it: the Allow the user gave
    /// still applies.
    func testAnAllowAfterConfirmationsWereTurnedOffStillApplies() {
        let rig = ControlRig(); defer { rig.dispose() }
        controlWrites(rig)
        let answer = add(rig, "a")
        rig.state.setConfirmClaudeChanges(false)
        rig.confirmer.answer(true)
        XCTAssertEqual(answer.result?.text, "Did a.")
        XCTAssertEqual(performed, ["a"])
    }

    func testAWriteWaitingItsTurnSaysSoAndOnlyItsOwnDialogSaysItAwaitsAnAnswer() {
        let rig = ControlRig(); defer { rig.dispose() }
        controlWrites(rig)
        let asking = add(rig, "a")
        let queued = switchTo(rig, "b")
        runOutTheClock(rig)
        let ticks = Array(stride(from: ControlTools.progressInterval, through: ControlTools.confirmationWait, by: ControlTools.progressInterval))
        XCTAssertEqual(asking.result, ControlTools.waitingResult)
        XCTAssertEqual(asking.progress, ticks)
        XCTAssertEqual(queued.result, ControlTools.queuedResult)
        XCTAssertEqual(queued.progress, ticks)
        XCTAssertNotEqual(ControlTools.queuedText, ControlTools.waitingText)
        XCTAssertEqual(performed, [])
    }

    func testAChangeClaudeAskedForThatLandsAfterItsEarlyAnswerIsAnnounced() {
        let rig = ControlRig(); defer { rig.dispose() }
        controlWrites(rig)
        add(rig, "a")
        switchTo(rig, "b")
        runOutTheClock(rig)
        switchTo(rig, "c", as: .cli)
        rig.h.notifier.clearSent()
        rig.confirmer.answer(true)
        rig.h.drain()
        XCTAssertEqual(performed, ["a", "b", "c"])
        // The confirmed change and the direct one Claude asked for are announced; the command
        // line's own change is the user's.
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), ["Did a.", "Did b."])
    }

    func testAChangeAnsweredInTimeIsNotAnnouncedAgain() {
        let rig = ControlRig(); defer { rig.dispose() }
        controlWrites(rig)
        let asking = add(rig, "a")
        let queued = switchTo(rig, "b")
        rig.h.notifier.clearSent()
        rig.confirmer.answer(true)
        rig.h.drain()
        XCTAssertEqual(asking.result?.text, "Did a.")
        XCTAssertEqual(queued.result?.text, "Did b.")
        XCTAssertTrue(rig.h.notifier.sent.isEmpty, "the caller heard the outcome itself")
    }

    func testAToolThatBuildsNoQuestionIsRefusedRatherThanRunUnasked() {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.tools.prepareOverride = { [unowned self] _, _, _ in
            .ready(nil, perform: {
                self.performed.append("unasked")
                return ControlOutcome(result: ControlResult(structured: .object([:]), text: "Did it.", isError: false), notification: nil)
            })
        }
        XCTAssertEqual(add(rig, "a").result, .refusal(ControlTools.noQuestionError))
        XCTAssertEqual(performed, [])
        XCTAssertEqual(rig.confirmer.requests, [])
    }

    /// Starts a write whose connection the test closes by clearing `open`.
    private func start(_ rig: ControlRig, _ name: String, _ arguments: [String: JSONValue], as caller: ControlCaller,
                       open: @escaping () -> Bool) -> ControlRig.Answer {
        let answer = ControlRig.Answer()
        rig.tools.call(name: name, arguments: .object(arguments), caller: caller,
                       progress: { answer.progress.append($0) },
                       completion: {
                           answer.result = $0
                           answer.completions += 1
                       },
                       isAbandoned: { !open() })
        rig.h.drain()
        return answer
    }

    /// The command line is not bound by Claude's time limit: queued behind a dialog, it keeps hearing
    /// progress, is never answered early, and is answered once its own change has run.
    func testACommandLineWriteQueuedBehindADialogWaitsForItsTurn() {
        let rig = ControlRig(); defer { rig.dispose() }
        controlWrites(rig)
        let asking = add(rig, "a")
        let fromCLI = switchTo(rig, "c", as: .cli)
        for _ in 0..<40 { rig.h.delays.runNext() }
        XCTAssertEqual(asking.result, ControlTools.waitingResult)
        XCTAssertNil(fromCLI.result, "no early answer, however long it waits")
        XCTAssertGreaterThan(fromCLI.progress.last ?? 0, ControlTools.confirmationWait, "it hears progress past the 50 s")
        rig.confirmer.answer(false)
        XCTAssertEqual(fromCLI.result?.text, "Did c.")
        XCTAssertEqual(fromCLI.completions, 1)
        XCTAssertEqual(performed, ["c"])
    }

    /// A write whose connection closed before its turn — a command line stopped with Control-C — is
    /// dropped unrun: nobody is left to hear of it, and the queue moves on.
    func testAWriteWhoseConnectionClosedBeforeItsTurnIsDroppedUnrun() {
        let rig = ControlRig(); defer { rig.dispose() }
        controlWrites(rig)
        var open = true
        _ = add(rig, "a")
        let gone = start(rig, "switch_collection", ["collection": .string("c")], as: .cli, open: { open })
        open = false
        rig.confirmer.answer(false)
        XCTAssertNil(gone.result)
        XCTAssertEqual(performed, [])
        XCTAssertEqual(switchTo(rig, "d").result?.text, "Did d.", "the next write runs")
    }

    /// A relay write already told it is queued still runs when its turn comes, as it was told, even
    /// if its connection has closed since.
    func testARelayWriteToldItIsQueuedRunsAfterItsConnectionCloses() {
        let rig = ControlRig(); defer { rig.dispose() }
        controlWrites(rig)
        var open = true
        _ = add(rig, "a")
        let told = start(rig, "switch_collection", ["collection": .string("b")], as: .relay(clientName: "claude-ai"),
                         open: { open })
        runOutTheClock(rig)
        XCTAssertEqual(told.result, ControlTools.queuedResult)
        open = false
        rig.confirmer.answer(false)
        XCTAssertEqual(performed, ["b"])
    }

    /// A relay write waiting its turn whose connection closes before its early answer hears nothing
    /// more — no progress, no "queued" — and is dropped when its turn comes: Claude was never told it
    /// was waiting, and a retry after "restarted — try again" must not land it twice.
    func testARelayWriteWhoseConnectionClosesBeforeItsEarlyAnswerIsDropped() {
        let rig = ControlRig(); defer { rig.dispose() }
        controlWrites(rig)
        var open = true
        _ = add(rig, "a")
        let queued = start(rig, "switch_collection", ["collection": .string("b")], as: .relay(clientName: "claude-ai"),
                           open: { open })
        // The two calls tick in turn: eighteen runs take each to 45 s.
        for _ in 0..<18 { rig.h.delays.runNext() }
        XCTAssertEqual(queued.progress.count, 9)
        open = false
        runOutTheClock(rig)
        XCTAssertNil(queued.result, "no early answer once the connection has gone")
        XCTAssertEqual(queued.progress.count, 9, "and no more progress")
        rig.confirmer.answer(false)
        XCTAssertNil(queued.result)
        XCTAssertEqual(performed, [])
    }

    /// A change Claude asked for whose connection has gone is still the user's to allow; allowed, it
    /// lands and is announced once, since its caller will never hear the answer.
    func testAnAllowedChangeWhoseConnectionClosedIsAnnouncedOnce() {
        let rig = ControlRig(); defer { rig.dispose() }
        controlWrites(rig)
        var open = true
        _ = start(rig, "add_connector", ["name": .string("a"), "config": .object([:])], as: .relay(clientName: "claude-ai"),
                  open: { open })
        open = false
        rig.h.notifier.clearSent()
        rig.confirmer.answer(true)
        rig.h.drain()
        XCTAssertEqual(performed, ["a"])
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), ["Did a."])
    }
}
