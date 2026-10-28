import Foundation

/// Mirror: windows/src/ConnectorControl.Core/IMcpToolHost.cs
///
/// What an MCP server needs from its tools: the list `tools/list` answers with, and how to run
/// one. Main-actor only, like the tools behind it.
@MainActor
public protocol McpToolHost: AnyObject {
    func listTools() -> [JSONValue]
    /// Runs one tool. `progress` may be called any number of times before `completion`, which is
    /// called once, with the `tools/call` result object.
    func callTool(name: String, arguments: JSONValue, progressToken: JSONValue?,
                  progress: @escaping (Double) -> Void, completion: @escaping (JSONValue) -> Void)
}

/// Mirror: windows/src/ConnectorControl.Core/McpSession.cs
///
/// The part of the Model Context Protocol Connector Control serves, as newline-delimited JSON-RPC
/// 2.0: the handshake with protocol-version negotiation, `ping`, `tools/list`, `tools/call` with
/// progress, and cancellation. Lines in through `receive`, lines out through `send`, no I/O of its
/// own, one session per connection. Main-actor only: the transport hands each line over on the
/// main thread, where the tools run.
@MainActor
public final class McpSession {
    public static let serverName = "connector-control"
    /// Newest first; the first is what a client asking for anything else is offered.
    public static let supportedProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"]
    /// The largest message read; anything longer is refused without being parsed.
    public static let maxMessageBytes = 1_048_576
    public static let parseErrorCode = -32700
    public static let invalidRequestCode = -32600
    public static let methodNotFoundCode = -32601
    public static let invalidParamsCode = -32602
    public static let notInitializedCode = -32002
    public static let parseErrorMessage = "Parse error"
    public static let invalidRequestMessage = "Invalid Request"
    public static let methodNotFoundMessage = "Method not found"
    public static let invalidParamsMessage = "Invalid params"
    public static let notInitializedMessage = "Server not initialized"
    public static let tooLargeMessage = "Message too large"

    public static func unknownToolMessage(_ name: String) -> String { "Unknown tool: \(name)" }

    /// The client's own name from `initialize`, which the gate names as the asker.
    public private(set) var clientName: String?
    public private(set) var isInitialized = false

    private let host: McpToolHost
    private let version: String
    private let send: (String) -> Void
    /// Requests still running, by id.
    private var running: Set<JSONValue> = []
    /// Running requests the client has cancelled: their progress and their answer are dropped.
    private var cancelled: Set<JSONValue> = []

    public init(host: McpToolHost, version: String, send: @escaping (String) -> Void) {
        self.host = host
        self.version = version
        self.send = send
    }

    public func receive(_ line: String) {
        guard line.utf8.count <= McpSession.maxMessageBytes else {
            reply(error: McpSession.invalidRequestCode, McpSession.tooLargeMessage, id: .null)
            return
        }
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        guard let message = try? JSONValue.parse(Data(trimmed.utf8)) else {
            reply(error: McpSession.parseErrorCode, McpSession.parseErrorMessage, id: .null)
            return
        }
        guard case .object(let object) = message, object["jsonrpc"] == .string("2.0") else {
            reply(error: McpSession.invalidRequestCode, McpSession.invalidRequestMessage, id: requestID(message) ?? .null)
            return
        }
        guard case .string(let method)? = object["method"] else {
            // An answer to something this server never asks is not worth an error of its own.
            if object["id"] != nil, object["result"] != nil || object["error"] != nil { return }
            reply(error: McpSession.invalidRequestCode, McpSession.invalidRequestMessage, id: requestID(message) ?? .null)
            return
        }
        let params = object["params"] ?? .object([:])
        guard let rawID = object["id"] else {
            notification(method, params)
            return
        }
        guard let id = McpSession.validID(rawID) else {
            reply(error: McpSession.invalidRequestCode, McpSession.invalidRequestMessage, id: .null)
            return
        }
        request(method, params, id: id)
    }

    // MARK: Requests

    private func request(_ method: String, _ params: JSONValue, id: JSONValue) {
        switch method {
        case "initialize":
            initialize(params, id: id)
        case "ping":
            respond(id, .object([:]))
        case _ where !isInitialized:
            reply(error: McpSession.notInitializedCode, McpSession.notInitializedMessage, id: id)
        case "tools/list":
            respond(id, .object(["tools": .array(host.listTools())]))
        case "tools/call":
            callTool(params, id: id)
        default:
            reply(error: McpSession.methodNotFoundCode, McpSession.methodNotFoundMessage, id: id)
        }
    }

    private func initialize(_ params: JSONValue, id: JSONValue) {
        guard case .object(let fields) = params else {
            reply(error: McpSession.invalidParamsCode, McpSession.invalidParamsMessage, id: id)
            return
        }
        var negotiated = McpSession.supportedProtocolVersions[0]
        if case .string(let asked)? = fields["protocolVersion"], McpSession.supportedProtocolVersions.contains(asked) {
            negotiated = asked
        }
        if case .object(let info)? = fields["clientInfo"], case .string(let name)? = info["name"] {
            clientName = name
        }
        isInitialized = true
        respond(id, .object([
            "protocolVersion": .string(negotiated),
            "capabilities": .object(["tools": .object([:])]),
            "serverInfo": .object(["name": .string(McpSession.serverName), "version": .string(version)]),
        ]))
    }

    private func callTool(_ params: JSONValue, id: JSONValue) {
        guard case .object(let fields) = params, case .string(let name)? = fields["name"] else {
            reply(error: McpSession.invalidParamsCode, McpSession.invalidParamsMessage, id: id)
            return
        }
        let arguments = fields["arguments"] ?? .object([:])
        guard case .object = arguments else {
            reply(error: McpSession.invalidParamsCode, McpSession.invalidParamsMessage, id: id)
            return
        }
        guard host.listTools().contains(where: { McpSession.toolName(of: $0) == name }) else {
            reply(error: McpSession.invalidParamsCode, McpSession.unknownToolMessage(name), id: id)
            return
        }
        var token: JSONValue?
        if case .object(let meta)? = fields["_meta"], let given = meta["progressToken"], McpSession.validID(given) != nil {
            token = given
        }
        running.insert(id)
        host.callTool(name: name, arguments: arguments, progressToken: token, progress: { [weak self] value in
            guard let self, let token, self.running.contains(id), !self.cancelled.contains(id) else { return }
            self.write(.object([
                "jsonrpc": .string("2.0"),
                "method": .string("notifications/progress"),
                "params": .object(["progressToken": token, "progress": McpSession.number(value)]),
            ]))
        }, completion: { [weak self] result in
            guard let self, self.running.remove(id) != nil else { return }
            if self.cancelled.remove(id) != nil { return }
            self.respond(id, result)
        })
    }

    // MARK: Notifications

    /// `notifications/initialized` needs nothing; a cancellation marks a running request so its
    /// answer is dropped. Everything else is ignored, as a notification is never answered.
    private func notification(_ method: String, _ params: JSONValue) {
        guard method == "notifications/cancelled", case .object(let fields) = params,
              let given = fields["requestId"], let id = McpSession.validID(given), running.contains(id) else { return }
        cancelled.insert(id)
    }

    // MARK: Writing

    private func respond(_ id: JSONValue, _ result: JSONValue) {
        write(.object(["jsonrpc": .string("2.0"), "id": id, "result": result]))
    }

    private func reply(error code: Int, _ message: String, id: JSONValue) {
        write(.object(["jsonrpc": .string("2.0"), "id": id,
                       "error": .object(["code": .int(code), "message": .string(message)])]))
    }

    private func write(_ message: JSONValue) { send(message.compactLine()) }

    /// A JSON-RPC id this server accepts: a string or an integer.
    static func validID(_ value: JSONValue) -> JSONValue? {
        switch value {
        case .string, .int: return value
        default: return nil
        }
    }

    /// The id of a message too broken to answer properly, when it has a usable one.
    private func requestID(_ message: JSONValue) -> JSONValue? {
        guard case .object(let object) = message, let id = object["id"] else { return nil }
        return McpSession.validID(id)
    }

    static func toolName(of descriptor: JSONValue) -> String? {
        guard case .object(let object) = descriptor, case .string(let name)? = object["name"] else { return nil }
        return name
    }

    /// Whole numbers go out as integers, which both platforms write the same way.
    static func number(_ value: Double) -> JSONValue {
        value == value.rounded() && abs(value) < 1e15 ? .int(Int(value)) : .double(value)
    }
}
