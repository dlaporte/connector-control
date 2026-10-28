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

    func testAWriteWaitingItsTurnSaysSoAndOnlyItsOwnDialogSaysItAwaitsAnAnswer() {
        let rig = ControlRig(); defer { rig.dispose() }
        controlWrites(rig)
        let asking = add(rig, "a")
        let queued = switchTo(rig, "b")
        let fromCLI = switchTo(rig, "c", as: .cli)
        runOutTheClock(rig)
        let ticks = Array(stride(from: ControlTools.progressInterval, through: ControlTools.confirmationWait, by: ControlTools.progressInterval))
        XCTAssertEqual(asking.result, ControlTools.waitingResult)
        XCTAssertEqual(asking.progress, ticks)
        XCTAssertEqual(queued.result, ControlTools.queuedResult)
        XCTAssertEqual(queued.progress, ticks)
        XCTAssertEqual(fromCLI.result, ControlTools.queuedResult, "the command line never hears of a dialog it did not raise")
        XCTAssertNotEqual(ControlTools.queuedText, ControlTools.waitingText)
        XCTAssertEqual(performed, [])
    }

    func testAChangeClaudeAskedForThatLandsAfterItsEarlyAnswerIsAnnounced() {
        let rig = ControlRig(); defer { rig.dispose() }
        controlWrites(rig)
        add(rig, "a")
        switchTo(rig, "b")
        switchTo(rig, "c", as: .cli)
        runOutTheClock(rig)
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
}
