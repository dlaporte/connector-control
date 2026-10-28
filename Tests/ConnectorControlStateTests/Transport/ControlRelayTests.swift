import XCTest
import ConnectorControlCore
@testable import ConnectorControlState
@testable import ConnectorControlTransport

/// Mirror: windows/tests/ConnectorControl.Core.Tests/Transport/ControlRelayTests.cs
final class ControlRelayTests: XCTestCase {
    private let initialize = #"{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"claude-ai","version":"0.1.0"}}}"#
    private let initialized = #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#
    private let initializeAnswer = #"{"id":0,"jsonrpc":"2.0","result":{"capabilities":{"tools":{}},"protocolVersion":"2025-11-25","serverInfo":{"name":"connector-control","version":"1.5.0"}}}"#

    private func call(_ id: Int) -> String {
        #"{"jsonrpc":"2.0","id":\#(id),"method":"tools/call","params":{"name":"get_status","arguments":{}}}"#
    }

    private func answer(_ id: Int) -> String {
        #"{"id":\#(id),"jsonrpc":"2.0","result":{"content":[],"isError":false,"structuredContent":{}}}"#
    }

    private func refused(_ id: Int, _ reason: String) -> ControlRelay.Action {
        .toClaude(JSONValue.object(["jsonrpc": .string("2.0"), "id": .int(id),
                                    "result": ControlResult.refusal(reason).mcpResult]).compactLine())
    }

    /// A relay through its first handshake.
    private func started() -> ControlRelay {
        let relay = ControlRelay()
        XCTAssertEqual(relay.fromClaude(initialize), [.connect], "nothing connects before Claude says something")
        XCTAssertEqual(relay.connectSucceeded(), [.toApp(initialize)])
        XCTAssertEqual(relay.fromApp(initializeAnswer), [.toClaude(initializeAnswer)])
        XCTAssertEqual(relay.fromClaude(initialized), [.toApp(initialized)])
        return relay
    }

    func testEveryLinePassesAsItCame() {
        let relay = started()
        XCTAssertEqual(relay.fromClaude(call(1)), [.toApp(call(1))])
        let progress = #"{"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":5,"progressToken":"t"}}"#
        XCTAssertEqual(relay.fromApp(progress), [.toClaude(progress)])
        XCTAssertEqual(relay.fromApp(answer(1)), [.toClaude(answer(1))])
        XCTAssertEqual(relay.fromClaude("not json"), [.toApp("not json")], "the app answers what it cannot read")
    }

    func testAfterTheAppGoesTheNextRequestReconnectsAndReplaysTheHandshake() throws {
        let relay = started()
        XCTAssertEqual(relay.connectionLost(), [], "nothing was waiting, and nothing happens until Claude asks")
        XCTAssertFalse(relay.isConnected)
        XCTAssertEqual(relay.fromClaude(call(2)), [.connect])
        let replay = relay.connectSucceeded()
        XCTAssertEqual(replay.count, 3)
        guard case .toApp(let replayed) = replay[0] else { return XCTFail("\(replay)") }
        let fields = try XCTUnwrap(ControlRelay.message(replayed))
        XCTAssertEqual(fields["id"], .string(ControlRelay.replayIDPrefix + "1"), "never an id Claude uses")
        XCTAssertEqual(fields["params"], ControlRelay.message(initialize)?["params"], "Claude's own initialize, its name included")
        XCTAssertEqual(Array(replay.dropFirst()), [.toApp(initialized), .toApp(call(2))])
        XCTAssertEqual(relay.fromApp(#"{"id":"connector-control-replay-1","jsonrpc":"2.0","result":{}}"#), [],
                       "the replayed answer is dropped")
        XCTAssertEqual(relay.fromApp(answer(2)), [.toClaude(answer(2))])
    }

    func testARequestInFlightWhenTheAppGoesIsToldToTryAgain() {
        let relay = started()
        _ = relay.fromClaude(call(3))
        _ = relay.fromClaude(#"{"jsonrpc":"2.0","id":4,"method":"tools/list"}"#)
        XCTAssertEqual(relay.connectionLost(), [
            refused(3, ControlRelay.restartedError),
            .toClaude(McpSession.errorLine(code: McpSession.internalErrorCode, message: ControlRelay.restartedError, id: .int(4))),
        ])
        XCTAssertEqual(relay.fromClaude(#"{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3}}"#), [],
                       "held for the next connection, and no reconnect for it")
    }

    /// A cancelled request is never answered — the session drops its answer — so the relay stops
    /// waiting for it: Claude closing its side ends the relay, and a lost connection answers nothing
    /// for it.
    func testACancelledRequestIsNoLongerWaitedFor() {
        let relay = started()
        _ = relay.fromClaude(call(8))
        let cancel = #"{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":8}}"#
        XCTAssertEqual(relay.fromClaude(cancel), [.toApp(cancel)])
        XCTAssertEqual(relay.claudeClosed(), [.exit(ControlOutput.doneExit)])
        let other = started()
        _ = other.fromClaude(call(9))
        _ = other.fromClaude(#"{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":9}}"#)
        XCTAssertEqual(other.connectionLost(), [], "no stray answer for a cancelled request")
    }

    /// A request the session answers with id null — an id that is neither a string nor an integer, or
    /// one already running — is not waited for under that id.
    func testARequestTheSessionCannotAnswerByItsIDIsNotWaitedFor() {
        let relay = started()
        _ = relay.fromClaude(#"{"jsonrpc":"2.0","id":1.5,"method":"ping"}"#)
        _ = relay.fromClaude(call(10))
        _ = relay.fromClaude(call(10))
        XCTAssertEqual(relay.connectionLost(), [refused(10, ControlRelay.restartedError)], "one answer for 10, none for 1.5")
    }

    /// After the first handshake only a request reconnects, and may start the app: a notification, or a
    /// line that is no message, waits, held, and reaches the app after the replayed handshake.
    func testOnlyARequestReconnects() {
        let relay = started()
        _ = relay.connectionLost()
        let cancel = #"{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3}}"#
        XCTAssertEqual(relay.fromClaude(cancel), [])
        XCTAssertEqual(relay.fromClaude(""), [])
        XCTAssertEqual(relay.fromClaude(call(11)), [.connect])
        XCTAssertEqual(Array(relay.connectSucceeded().dropFirst()), [.toApp(initialized), .toApp(cancel), .toApp(""), .toApp(call(11))])
    }

    func testAFailedReconnectAnswersTheRequestAndTheNextTriesAgain() {
        let relay = started()
        _ = relay.connectionLost()
        XCTAssertEqual(relay.fromClaude(call(5)), [.connect])
        XCTAssertEqual(relay.connectFailed(ControlClient.unreachableError), [refused(5, ControlClient.unreachableError)])
        XCTAssertEqual(relay.fromClaude(call(6)), [.connect])
    }

    func testAFailedFirstHandshakeEndsTheRelay() {
        let refused = ControlRelay()
        _ = refused.fromClaude(initialize)
        XCTAssertEqual(refused.connectFailed(ControlClient.notRunningError), [
            .toClaude(McpSession.errorLine(code: McpSession.internalErrorCode, message: ControlClient.notRunningError, id: .int(0))),
            .exit(ControlOutput.unreachableExit),
        ])
        let dropped = ControlRelay()
        _ = dropped.fromClaude(initialize)
        _ = dropped.connectSucceeded()
        XCTAssertEqual(dropped.connectionLost(), [
            .toClaude(McpSession.errorLine(code: McpSession.internalErrorCode, message: ControlRelay.restartedError, id: .int(0))),
            .exit(ControlOutput.unreachableExit),
        ], "the app went before it answered initialize")
    }

    func testWhenClaudeClosesTheRelayEndsOnceItsRequestsAreAnswered() {
        XCTAssertEqual(started().claudeClosed(), [.exit(ControlOutput.doneExit)], "nothing waiting: at once")
        let relay = started()
        _ = relay.fromClaude(call(7))
        XCTAssertEqual(relay.claudeClosed(), [])
        XCTAssertEqual(relay.fromApp(answer(7)), [.toClaude(answer(7)), .exit(ControlOutput.doneExit)])
    }
}
