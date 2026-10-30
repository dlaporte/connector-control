import Foundation

/// Mirror: windows/src/ConnectorControl.Core/IMcpToolHost.cs
///
/// What an MCP server needs from its tools: the list `tools/list` answers with, and how to run
/// one. Main-actor only, like the tools behind it.
@MainActor
public protocol McpToolHost: AnyObject {
    func listTools() -> [JSONValue]
    /// What `tools/call` answers for a tool this host does not list: the session's own sentence, or
    /// one that says why the tool is missing.
    func unknownToolMessage(_ name: String) -> String
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
    // The constants and the pure helpers below are nonisolated: the command-line tool frames and
    // writes lines on its own reading threads, away from the main actor. Platform idiom: C# has no
    // actor isolation, so its mirror of these is plain statics.
    public nonisolated static let serverName = "connector-control"
    /// Newest first; the first is what a client asking for anything else is offered.
    public nonisolated static let supportedProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"]
    /// The largest message read; anything longer is refused without being parsed.
    public nonisolated static let maxMessageBytes = 1_048_576
    /// How deeply a message may nest arrays and objects; the Windows parser's own limit, set on both
    /// sides so neither accepts what the other refuses.
    public nonisolated static let maxNestingDepth = 64
    public nonisolated static let parseErrorCode = -32700
    public nonisolated static let invalidRequestCode = -32600
    public nonisolated static let methodNotFoundCode = -32601
    public nonisolated static let invalidParamsCode = -32602
    public nonisolated static let notInitializedCode = -32002
    public nonisolated static let parseErrorMessage = "Parse error"
    public nonisolated static let invalidRequestMessage = "Invalid Request"
    public nonisolated static let methodNotFoundMessage = "Method not found"
    public nonisolated static let invalidParamsMessage = "Invalid params"
    public nonisolated static let notInitializedMessage = "Server not initialized"
    public nonisolated static let tooLargeMessage = "Message too large"
    public nonisolated static let internalErrorCode = -32603
    public nonisolated static let internalErrorMessage = "Internal error"

    public nonisolated static func unknownToolMessage(_ name: String) -> String { "Unknown tool: \(name)" }

    /// The client's own name from `initialize`, which the gate names as the asker.
    public private(set) var clientName: String?
    public private(set) var isInitialized = false

    private let host: McpToolHost
    private let version: String
    private let send: (String) -> Void
    /// Requests still running, by id.
    private var running: Set<JSONValue> = []
    /// Running requests the client has cancelled: their progress and their answer are dropped. The
    /// tool keeps running, only its answer is dropped, so an id stays here until the tool completes.
    private var cancelled: Set<JSONValue> = []

    public init(host: McpToolHost, version: String, send: @escaping (String) -> Void) {
        self.host = host
        self.version = version
        self.send = send
    }

    public func receive(_ line: String) {
        guard line.utf8.count <= McpSession.maxMessageBytes else {
            receiveTooLarge()
            return
        }
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        guard McpSession.nests(atMost: McpSession.maxNestingDepth, trimmed),
              let message = try? JSONValue.parse(Data(trimmed.utf8)) else {
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
        guard !running.contains(id) else {
            // Answering with the id would be taken for the running request's answer.
            reply(error: McpSession.invalidRequestCode, McpSession.invalidRequestMessage, id: .null)
            return
        }
        request(method, params, id: id)
    }

    /// One message as the transport read it. Bytes that are not UTF-8 are refused as a parse error
    /// before anything decodes them: each platform would otherwise decode them its own way, and a
    /// name echoed back in an answer would differ between the two.
    public func receive(_ bytes: Data) {
        guard bytes.count <= McpSession.maxMessageBytes else {
            receiveTooLarge()
            return
        }
        guard let line = McpSession.strictUTF8(bytes) else {
            reply(error: McpSession.parseErrorCode, McpSession.parseErrorMessage, id: .null)
            return
        }
        receive(line)
    }

    /// A line the transport stopped reading at `maxMessageBytes`: refused as `receive` refuses one.
    public func receiveTooLarge() {
        reply(error: McpSession.invalidRequestCode, McpSession.tooLargeMessage, id: .null)
    }

    /// `bytes` as text, or nil when they are not UTF-8; the hello is read the same way. Platform
    /// idiom: the standard library's decoder, which refuses malformed, overlong and surrogate
    /// sequences, as the Windows mirror's throwing UTF8Encoding does.
    public nonisolated static func strictUTF8(_ bytes: Data) -> String? {
        var decoder = UTF8()
        var iterator = bytes.makeIterator()
        var scalars = String.UnicodeScalarView()
        while true {
            switch decoder.decode(&iterator) {
            case .scalarValue(let scalar): scalars.append(scalar)
            case .emptyInput: return String(scalars)
            case .error: return nil
            }
        }
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
        guard !isInitialized else {
            // The name the gate shows as the asker must not change mid-session.
            reply(error: McpSession.invalidRequestCode, McpSession.invalidRequestMessage, id: id)
            return
        }
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
            reply(error: McpSession.invalidParamsCode, host.unknownToolMessage(name), id: id)
            return
        }
        var token: JSONValue?
        if case .object(let meta)? = fields["_meta"], let given = meta["progressToken"], McpSession.validID(given) != nil {
            token = given
        }
        running.insert(id)
        host.callTool(name: name, arguments: arguments, progressToken: token, progress: { [weak self] value in
            guard let self, let token, let number = McpSession.number(value),
                  self.running.contains(id), !self.cancelled.contains(id) else { return }
            self.write(.object([
                "jsonrpc": .string("2.0"),
                "method": .string("notifications/progress"),
                "params": .object(["progressToken": token, "progress": number]),
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
        let line = JSONValue.object(["jsonrpc": .string("2.0"), "id": id, "result": result]).compactLine()
        // An answer no client can read is refused, as the client must still hear about its request or it
        // waits on that id for good: one nested deeper than maxNestingDepth, which every reader of ours
        // refuses, and one JSON cannot hold (NaN, infinity), for which compactLine answers "null".
        guard line != "null", McpSession.nests(atMost: McpSession.maxNestingDepth, line) else {
            reply(error: McpSession.internalErrorCode, McpSession.internalErrorMessage, id: id)
            return
        }
        send(line)
    }

    private func reply(error code: Int, _ message: String, id: JSONValue) {
        send(McpSession.errorLine(code: code, message: message, id: id))
    }

    /// A JSON-RPC error answer, as this session writes one: the relay answers in the same words when
    /// the app cannot.
    public nonisolated static func errorLine(code: Int, message: String, id: JSONValue) -> String {
        JSONValue.object(["jsonrpc": .string("2.0"), "id": id,
                          "error": .object(["code": .int(code), "message": .string(message)])]).compactLine()
    }

    private func write(_ message: JSONValue) { send(message.compactLine()) }

    /// A JSON-RPC id this server accepts: a string or an integer.
    public nonisolated static func validID(_ value: JSONValue) -> JSONValue? {
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

    nonisolated static func toolName(of descriptor: JSONValue) -> String? {
        guard case .object(let object) = descriptor, case .string(let name)? = object["name"] else { return nil }
        return name
    }

    /// Whole numbers go out as integers, which both platforms write the same way. NaN and infinity
    /// have no JSON form, so they are no progress at all.
    nonisolated static func number(_ value: Double) -> JSONValue? {
        guard value.isFinite else { return nil }
        return value == value.rounded() && abs(value) < 1e15 ? .int(Int(value)) : .double(value)
    }

    /// Whether `text` nests arrays and objects no deeper than `limit`, brackets inside strings not counted.
    public nonisolated static func nests(atMost limit: Int, _ text: String) -> Bool {
        var depth = 0
        var inString = false
        var escaped = false
        for byte in text.utf8 {
            if inString {
                if escaped { escaped = false } else if byte == UInt8(ascii: "\\") { escaped = true } else if byte == UInt8(ascii: "\"") { inString = false }
            } else if byte == UInt8(ascii: "\"") {
                inString = true
            } else if byte == UInt8(ascii: "[") || byte == UInt8(ascii: "{") {
                depth += 1
                if depth > limit { return false }
            } else if byte == UInt8(ascii: "]") || byte == UInt8(ascii: "}") {
                depth -= 1
            }
        }
        return true
    }

    /// Whether `text` has a comma, outside a string, followed by nothing but JSON whitespace and then a
    /// closing bracket or brace. Platform idiom: `JSONDecoder` and `JSONSerialization` read one trailing
    /// comma, where the Windows reader - and Claude Desktop - refuse it; refused here alike, so a line or a
    /// file reads the same on both.
    public nonisolated static func hasTrailingComma(_ text: String) -> Bool {
        var inString = false
        var escaped = false
        var comma = false
        for byte in text.utf8 {
            if inString {
                if escaped { escaped = false } else if byte == UInt8(ascii: "\\") { escaped = true } else if byte == UInt8(ascii: "\"") { inString = false }
            } else if byte == UInt8(ascii: ",") {
                comma = true
            } else if byte == UInt8(ascii: "]") || byte == UInt8(ascii: "}") {
                if comma { return true }
            } else if byte != UInt8(ascii: " "), byte != UInt8(ascii: "\t"), byte != UInt8(ascii: "\n"), byte != UInt8(ascii: "\r") {
                comma = false
                if byte == UInt8(ascii: "\"") { inString = true }
            }
        }
        return false
    }
}
