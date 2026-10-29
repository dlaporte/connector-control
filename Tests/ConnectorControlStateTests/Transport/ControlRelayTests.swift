import XCTest
import ConnectorControlCore
@testable import ConnectorControlState
@testable import ConnectorControlTransport

/// Mirror: windows/tests/ConnectorControl.Core.Tests/Transport/ControlRelayTests.cs
final class ControlRelayTests: XCTestCase {
    private let initialize = #"{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"claude-ai","version":"0.1.0"}}}"#
    private let initialized = #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#
    private let initializeAnswer = #"{"id":0,"jsonrpc":"2.0","result":{"capabilities":{"tools":{}},"protocolVersion":"2025-11-25","serverInfo":{"name":"connector-control","version":"1.5.0"}}}"#

    private func call(_ id: Int) -> String { call(rawID: String(id)) }

    /// A call whose id is written as `rawID` is, JSON and all.
    private func call(rawID: String) -> String {
        #"{"jsonrpc":"2.0","id":\#(rawID),"method":"tools/call","params":{"name":"get_status","arguments":{}}}"#
    }

    private func answer(_ id: Int) -> String {
        #"{"id":\#(id),"jsonrpc":"2.0","result":{"content":[],"isError":false,"structuredContent":{}}}"#
    }

    private func refused(_ id: Int, _ reason: String) -> ControlRelay.Action { refused(.int(id), reason) }

    private func refused(_ id: JSONValue, _ reason: String) -> ControlRelay.Action {
        .toClaude(JSONValue.object(["jsonrpc": .string("2.0"), "id": id,
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

    /// An id Claude reuses after cancelling its request may still be running in the app, which answers the
    /// new request with id null and drops the old one's answer: nothing will come under that id, so it is
    /// not waited for, and the relay still ends once Claude closes its side. A new connection is a new
    /// session, where the id is waited for again.
    func testAnIDReusedAfterItsRequestWasCancelledIsNotWaitedFor() {
        let relay = started()
        _ = relay.fromClaude(call(8))
        _ = relay.fromClaude(#"{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":8}}"#)
        XCTAssertEqual(relay.fromClaude(call(8)), [.toApp(call(8))])
        XCTAssertEqual(relay.claudeClosed(), [.exit(ControlOutput.doneExit)])

        let restarted = started()
        _ = restarted.fromClaude(call(9))
        _ = restarted.fromClaude(#"{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":9}}"#)
        XCTAssertEqual(restarted.connectionLost(), [])
        XCTAssertEqual(restarted.fromClaude(call(9)), [.connect])
        _ = restarted.connectSucceeded()
        XCTAssertEqual(restarted.claudeClosed(), [], "waits for 9 in the new session")
        XCTAssertEqual(restarted.fromApp(answer(9)), [.toClaude(answer(9)), .exit(ControlOutput.doneExit)])
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

    /// Ids are told apart as the session reads them: 1 and 1.0 are one id, the integer 1, on both
    /// platforms; "1" is another.
    func testIDsAreToldApartAsTheSessionReadsThem() {
        let relay = started()
        _ = relay.fromClaude(call(rawID: "1"))
        _ = relay.fromClaude(call(rawID: #""1""#))
        _ = relay.fromClaude(call(rawID: "1.0"))
        let answeredAsOnePointZero = #"{"id":1.0,"jsonrpc":"2.0","result":{}}"#
        XCTAssertEqual(relay.fromApp(answeredAsOnePointZero), [.toClaude(answeredAsOnePointZero)])
        XCTAssertEqual(relay.connectionLost(), [refused(.string("1"), ControlRelay.restartedError)],
                       "the answer under 1.0 settled 1, and nothing more was waited for under it")
    }

    func testAFailedReconnectAnswersTheRequestAndTheNextTriesAgain() {
        let relay = started()
        _ = relay.connectionLost()
        XCTAssertEqual(relay.fromClaude(call(5)), [.connect])
        XCTAssertEqual(relay.connectFailed(ControlClient.unreachableError), [refused(5, ControlClient.unreachableError)])
        XCTAssertEqual(relay.fromClaude(call(6)), [.connect])
    }

    /// A connection that cannot be made answers what was held for it as the session would have: once for
    /// each id, and never under an id the session would not answer by.
    func testAFailedReconnectAnswersEachHeldRequestOnceUnderItsOwnID() {
        let relay = started()
        _ = relay.connectionLost()
        _ = relay.fromClaude(call(14))
        _ = relay.fromClaude(call(14))
        _ = relay.fromClaude(#"{"jsonrpc":"2.0","id":1.5,"method":"ping"}"#)
        _ = relay.fromClaude(#"{"jsonrpc":"2.0","id":null,"method":"ping"}"#)
        XCTAssertEqual(relay.connectFailed(ControlClient.unreachableError), [refused(14, ControlClient.unreachableError)])
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

    /// An `initialize` the app refuses is neither the handshake nor kept to replay: the app going then
    /// ends the relay, as before any handshake, and the `initialize` the app accepts next is the one a
    /// new connection replays.
    func testARefusedInitializeIsNeitherTheHandshakeNorReplayed() throws {
        let refusal = #"{"error":{"code":-32602,"message":"Unsupported protocol version"},"id":0,"jsonrpc":"2.0"}"#
        let refused = ControlRelay()
        _ = refused.fromClaude(initialize)
        _ = refused.connectSucceeded()
        XCTAssertEqual(refused.fromApp(refusal), [.toClaude(refusal)])
        XCTAssertEqual(refused.connectionLost(), [.exit(ControlOutput.unreachableExit)], "no handshake was made")

        let retried = ControlRelay()
        _ = retried.fromClaude(initialize)
        _ = retried.connectSucceeded()
        _ = retried.fromApp(refusal)
        let second = initialize.replacingOccurrences(of: #""id":0"#, with: #""id":1"#)
            .replacingOccurrences(of: "2025-11-25", with: "2025-06-18")
        XCTAssertEqual(retried.fromClaude(second), [.toApp(second)])
        let accepted = initializeAnswer.replacingOccurrences(of: #""id":0"#, with: #""id":1"#)
        XCTAssertEqual(retried.fromApp(accepted), [.toClaude(accepted)])
        _ = retried.fromClaude(initialized)
        XCTAssertEqual(retried.connectionLost(), [], "the accepted initialize was the handshake")
        XCTAssertEqual(retried.fromClaude(call(2)), [.connect])
        let replay = retried.connectSucceeded()
        guard case .toApp(let replayed)? = replay.first else { return XCTFail("\(replay)") }
        XCTAssertEqual(try XCTUnwrap(ControlRelay.message(replayed))["params"], ControlRelay.message(second)?["params"],
                       "the initialize the app accepted, not the one it refused")
    }

    func testWhenClaudeClosesTheRelayEndsOnceItsRequestsAreAnswered() {
        XCTAssertEqual(started().claudeClosed(), [.exit(ControlOutput.doneExit)], "nothing waiting: at once")
        let relay = started()
        _ = relay.fromClaude(call(7))
        XCTAssertEqual(relay.claudeClosed(), [])
        XCTAssertEqual(relay.fromApp(answer(7)), [.toClaude(answer(7)), .exit(ControlOutput.doneExit)])
    }

    /// Once Claude has closed its side, whatever settles the last request ends the relay — its answer,
    /// the connection going, or a connection that could not be made — so the relay never outlives it.
    func testOnceClaudeHasClosedALostOrFailedConnectionEndsTheRelay() {
        let lost = started()
        _ = lost.fromClaude(call(12))
        XCTAssertEqual(lost.claudeClosed(), [])
        XCTAssertEqual(lost.connectionLost(), [refused(12, ControlRelay.restartedError), .exit(ControlOutput.doneExit)])
        let failed = started()
        _ = failed.connectionLost()
        XCTAssertEqual(failed.fromClaude(call(13)), [.connect])
        XCTAssertEqual(failed.claudeClosed(), [], "the connection on its way settles the request first")
        XCTAssertEqual(failed.connectFailed(ControlClient.unreachableError),
                       [refused(13, ControlClient.unreachableError), .exit(ControlOutput.doneExit)])
    }
}
