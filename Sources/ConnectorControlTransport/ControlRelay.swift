import Foundation
import ConnectorControlCore
import ConnectorControlState

/// Mirror: windows/src/ConnectorControl.Core/Transport/ControlRelay.cs
///
/// `connector-control mcp`, the relay Claude Desktop runs, as a state machine: lines from Claude, lines
/// from the app and the connection's comings and goings in; what to send where out. Every line passes
/// as it came. The relay keeps only what it needs to outlive the app restarting: Claude's
/// `initialize` and `notifications/initialized`, replayed to a new connection with the replayed
/// answer dropped, and the requests still open, which a dropped connection answers "restarted". It
/// reconnects only when Claude next sends something. No I/O: `ControlRelayRunner` does that. The
/// Windows mirror has no enum payloads, so its actions are records.
public final class ControlRelay {
    public static let restartedError = "Connector Control restarted — try again."
    /// The replayed `initialize` goes out under an id of this form, which Claude never uses, so its
    /// answer is known and dropped.
    public static let replayIDPrefix = "connector-control-replay-"

    public enum Action: Equatable, Sendable {
        case toApp(String)
        case toClaude(String)
        /// Connect — starting the app where that is allowed — and report with `connectSucceeded()` or
        /// `connectFailed(_:)`.
        case connect
        case exit(Int32)
    }

    public private(set) var isConnected = false
    /// The `initialize` the app accepted, as Claude sent it: the one a new connection replays.
    private var initialize: [String: JSONValue]?
    /// Claude's latest `initialize` while none is accepted: kept for a replay only once the app answers it
    /// with a result, so one the app refused is never replayed.
    private var offered: [String: JSONValue]?
    /// Claude's `notifications/initialized`, as it sent it.
    private var initialized: String?
    /// The app has accepted Claude's own `initialize`.
    private var handshakeDone = false
    /// Requests forwarded and not yet answered, oldest first.
    private var pending: [(id: JSONValue, method: String)] = []
    /// Lines from Claude waiting for a connection.
    private var held: [String] = []
    /// A connection has been asked for and is neither made nor failed yet.
    private var connecting = false
    private var replayID: JSONValue?
    private var replays = 0
    /// Claude has closed its side: the relay ends once nothing it forwarded is left unanswered.
    private var closing = false

    public init() {}

    public func fromClaude(_ line: String) -> [Action] {
        let message = ControlRelay.message(line)
        if let message, message["id"] != nil, message["method"] == .string("initialize"), initialize == nil {
            offered = message
        }
        if let message, message["id"] == nil, message["method"] == .string("notifications/initialized"), initialized == nil {
            initialized = line
        }
        // A cancelled request is never answered — the session drops its answer — so it is waited for
        // no longer.
        if let message, message["id"] == nil, message["method"] == .string("notifications/cancelled"),
           case .object(let params)? = message["params"], let id = params["requestId"] {
            pending.removeAll { $0.id == id }
        }
        guard isConnected else {
            held.append(line)
            // After the first handshake only a request reconnects, and may start the app: a
            // notification, or a line that is no message at all, waits for the next request.
            if handshakeDone, message?["id"] == nil { return [] }
            connecting = true
            return [.connect]
        }
        var actions = forward(line, message)
        if closing, pending.isEmpty { actions.append(.exit(ControlOutput.doneExit)) }
        return actions
    }

    public func connectSucceeded() -> [Action] {
        isConnected = true
        connecting = false
        var actions: [Action] = []
        if handshakeDone, var replay = initialize {
            replays += 1
            let id = JSONValue.string(ControlRelay.replayIDPrefix + String(replays))
            replay["id"] = id
            replayID = id
            actions.append(.toApp(JSONValue.object(replay).compactLine()))
            if let initialized { actions.append(.toApp(initialized)) }
        }
        let lines = held
        held = []
        for line in lines { actions += forward(line, ControlRelay.message(line)) }
        if closing, pending.isEmpty { actions.append(.exit(ControlOutput.doneExit)) }
        return actions
    }

    /// No connection could be made: each request held for it is answered with `reason`, as `forward`
    /// waits for one — under an id the session answers by, once. Before the first handshake there is
    /// nothing to keep running for, and the relay ends.
    public func connectFailed(_ reason: String) -> [Action] {
        connecting = false
        let lines = held
        held = []
        var answered: [JSONValue] = []
        var actions: [Action] = []
        for line in lines {
            guard let request = ControlRelay.request(ControlRelay.message(line)), !answered.contains(request.id) else { continue }
            answered.append(request.id)
            actions.append(.toClaude(ControlRelay.failure(id: request.id, method: request.method, reason)))
        }
        if !handshakeDone {
            actions.append(.exit(ControlOutput.unreachableExit))
        } else if closing {
            actions.append(.exit(ControlOutput.doneExit))
        }
        return actions
    }

    public func fromApp(_ line: String) -> [Action] {
        let message = ControlRelay.message(line)
        if let message, message["method"] == nil, let id = message["id"] {
            if id == replayID {
                replayID = nil
                return []
            }
            // Only an answer with a result is the handshake: an error leaves the relay as before it, so the
            // app going still ends it, and the refused `initialize` is never replayed.
            if let index = pending.firstIndex(where: { $0.id == id }), pending.remove(at: index).method == "initialize",
               message["result"] != nil {
                if initialize == nil, let offered, ControlRelay.request(offered)?.id == id { initialize = offered }
                handshakeDone = true
            }
        }
        var actions: [Action] = [.toClaude(line)]
        if closing, pending.isEmpty { actions.append(.exit(ControlOutput.doneExit)) }
        return actions
    }

    /// The connection to the app has gone: each request it held is answered "restarted". Before the
    /// app answered Claude's own `initialize`, the relay ends.
    public func connectionLost() -> [Action] {
        guard isConnected else { return [] }
        isConnected = false
        replayID = nil
        let waiting = pending
        pending = []
        var actions = waiting.map { Action.toClaude(ControlRelay.failure(id: $0.id, method: $0.method, ControlRelay.restartedError)) }
        if !handshakeDone {
            actions.append(.exit(ControlOutput.unreachableExit))
        } else if closing {
            actions.append(.exit(ControlOutput.doneExit))
        }
        return actions
    }

    /// Claude closed its side: the relay ends now, or once what it forwarded is answered. A connection on
    /// its way settles what it holds first, and ends the relay then.
    public func claudeClosed() -> [Action] {
        closing = true
        guard !connecting else { return [] }
        return pending.isEmpty || !isConnected ? [.exit(ControlOutput.doneExit)] : []
    }

    /// Passes a line on. A request is waited for only when the session will answer it under its own id:
    /// one whose id is neither a string nor an integer, or is already running, is answered with id null
    /// or not at all.
    private func forward(_ line: String, _ message: [String: JSONValue]?) -> [Action] {
        if let request = ControlRelay.request(message), !pending.contains(where: { $0.id == request.id }) {
            pending.append(request)
        }
        return [.toApp(line)]
    }

    /// A request's id and method, when its id is one the session answers by: a string or an integer.
    static func request(_ message: [String: JSONValue]?) -> (id: JSONValue, method: String)? {
        guard let message, case .string(let method)? = message["method"], let raw = message["id"],
              let id = McpSession.validID(raw) else { return nil }
        return (id, method)
    }

    /// The object a line holds, read only to know what the line is — never to change it.
    static func message(_ line: String) -> [String: JSONValue]? {
        guard McpSession.nests(atMost: McpSession.maxNestingDepth, line),
              case .object(let fields)? = try? JSONValue.parse(Data(line.utf8)) else { return nil }
        return fields
    }

    /// What a request gets when the app cannot answer it: a tool call a refusal, read as Claude reads
    /// any other; anything else a JSON-RPC error.
    static func failure(id: JSONValue, method: String, _ reason: String) -> String {
        guard method == "tools/call" else {
            return McpSession.errorLine(code: McpSession.internalErrorCode, message: reason, id: id)
        }
        return JSONValue.object(["jsonrpc": .string("2.0"), "id": id, "result": ControlResult.refusal(reason).mcpResult]).compactLine()
    }
}
