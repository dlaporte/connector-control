import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlRecordingTests.cs
///
/// The shared recordings, reproduced exactly. Regenerate them, after a deliberate change to what
/// the tools say, with `scripts/regenerate-goldens.sh`, then review the diff: a change to a
/// recording nobody meant to touch is a bug, not a golden to bless.
@MainActor
final class ControlRecordingTests: XCTestCase {
    @discardableResult
    private func check(_ name: String, file: StaticString = #filePath, line: UInt = #line) throws -> ControlTranscript.Replay {
        let replay = try ControlTranscript.replay(name)
        XCTAssertFalse(replay.expected.isEmpty, "\(name) has no recorded answers", file: file, line: line)
        XCTAssertEqual(replay.actual, replay.expected, name, file: file, line: line)
        // A recording holds nothing of the machine that made it: the harness's temp folder is
        // named uniquely each run, so a line that carried it would never match again.
        let temp = replay.rig.h.dir.url.lastPathComponent
        XCTAssertFalse(replay.actual.contains { $0.contains(temp) }, "\(name) carries the temp folder", file: file, line: line)
        return replay
    }

    func testHandshake() throws { try check("handshake").rig.dispose() }

    func testReads() throws {
        let rig = try check("reads").rig; defer { rig.dispose() }
        XCTAssertNil(rig.state.store.mcps[ClaudeIntegration.entryName], "the seeded entry was never ingested")
        XCTAssertEqual(try rig.state.service.backups.backups(series: RestoreModel.series).count, 1,
                       "the first load had nothing to regenerate")
    }

    func testMasking() throws {
        let rig = try check("masking").rig; defer { rig.dispose() }
        let recording = Fixtures.text("control/masking.transcript")
        for secret in ["ghp_0123456789abcdefghijklmnopqrstuvwxyzAB", "s3cr3t-t0ken", "tenant=acme"] {
            XCTAssertFalse(recording.contains(secret), secret)
        }
    }

    func testDirectWrite() throws {
        let rig = try check("direct-write").rig; defer { rig.dispose() }
        XCTAssertEqual(rig.state.store.mcps["github"]?.enabled, false)
        XCTAssertTrue(rig.h.settings.claudeIntegrationEnabled, "Claude cannot turn the integration off")
        XCTAssertTrue(rig.h.settings.confirmClaudeChanges, "Claude cannot turn confirmations off")
    }

    func testGatedAllowed() throws {
        let rig = try check("gated-allowed").rig; defer { rig.dispose() }
        XCTAssertEqual(rig.confirmer.requests.map(\.title), [ControlTools.addTitle(ControlCaller.claudeName, "notion")])
        XCTAssertNotNil(rig.state.store.mcps["notion"])
        XCTAssertTrue(rig.h.notifier.sent.isEmpty)
    }

    func testGatedDeclined() throws {
        let rig = try check("gated-declined").rig; defer { rig.dispose() }
        XCTAssertEqual(rig.confirmer.requests.count, 1)
        XCTAssertNil(rig.state.store.mcps["notion"])
    }

    /// Claude Desktop's agent mode introduces itself as `local-agent-mode-<key>`, and is Claude too.
    func testGateOff() throws {
        let rig = try check("gate-off").rig; defer { rig.dispose() }
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [ControlTools.addedNotification(ControlCaller.claudeName, "notion", "Default")])
    }

    func testIntegrationOff() throws { try check("integration-off").rig.dispose() }

    func testReservedName() throws {
        let rig = try check("reserved-name").rig; defer { rig.dispose() }
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
        XCTAssertNil(rig.state.store.mcps[ClaudeIntegration.entryName])
    }

    /// The command-line tool is the user: never asked, never announced, and the only caller that may
    /// change the two Claude settings.
    func testCommandLine() throws {
        let rig = try check("cli").rig; defer { rig.dispose() }
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
        XCTAssertNotNil(rig.state.store.mcps["notion"])
        XCTAssertFalse(rig.h.settings.confirmClaudeChanges)
        XCTAssertFalse(rig.h.settings.claudeIntegrationEnabled)
        XCTAssertTrue(rig.h.notifier.sent.isEmpty)
    }

    /// A relay that gives the command-line tool's own name is still the relay.
    func testImpersonation() throws {
        let rig = try check("impersonation").rig; defer { rig.dispose() }
        XCTAssertEqual(rig.confirmer.requests.map(\.title), [ControlTools.addTitle(ControlCaller.callingItselfText("connector-control"), "notion")])
        XCTAssertNil(rig.state.store.mcps["notion"])
        XCTAssertTrue(rig.h.settings.claudeIntegrationEnabled)
    }

    func testProtocolErrors() throws {
        let replay = try check("protocol-errors"); defer { replay.rig.dispose() }
        XCTAssertEqual(replay.session.clientName, "claude-ai", "a second initialize renamed the asker")
    }

    /// A directive the replay does not know fails it, rather than leaving the rig at its default.
    func testADirectiveTheReplayDoesNotKnowFailsIt() throws {
        for line in ["#! confirm of", "#! caller CLI", "#! colour on", "#! backup off", "#! backup", "#! answer  allow",
                     "#! answer allow ", "#!caller cli", "caller cli"] {
            XCTAssertThrowsError(try ControlTranscript.directives([line]), line)
        }
        XCTAssertThrowsError(try ControlTranscript.directives(["#! confirm on", "#! confirm off"]), "given twice")
        XCTAssertEqual(try ControlTranscript.directives(["# A comment.", "#! caller cli", "#! answer deny", "> {}", "< {}"]),
                       ["caller": "cli", "answer": "deny"])
    }
}
