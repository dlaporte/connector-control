import Foundation
import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/Transport/ControlCommandRunner.cs
///
/// One command, run as an MCP client of the app: the hello, the handshake and the call go out
/// together, and the answer to the call is what the command prints. The call asks for progress, so a
/// change waiting behind a dialog can say once that it is waiting.
public enum ControlCommandRunner {
    /// The call's id; the handshake's is 1.
    public static let callID: JSONValue = .int(2)
    public static let progressToken = "cli"

    /// What one command sends, line by line, without newlines.
    public static func openingLines(tool: String, arguments: JSONValue, version: String) -> [String] {
        [
            ControlHello.line(client: ControlHello.cliClient, version: version),
            JSONValue.object([
                "jsonrpc": .string("2.0"), "id": .int(1), "method": .string("initialize"),
                "params": .object([
                    "protocolVersion": .string(McpSession.supportedProtocolVersions[0]),
                    "capabilities": .object([:]),
                    "clientInfo": .object(["name": .string(McpSession.serverName), "version": .string(version)]),
                ]),
            ]).compactLine(),
            JSONValue.object(["jsonrpc": .string("2.0"), "method": .string("notifications/initialized")]).compactLine(),
            JSONValue.object([
                "jsonrpc": .string("2.0"), "id": callID, "method": .string("tools/call"),
                "params": .object(["name": .string(tool), "arguments": arguments,
                                   "_meta": .object(["progressToken": .string(progressToken)])]),
            ]).compactLine(),
        ]
    }

    /// Connects through `client`, runs the call over that connection and closes it: what the command line
    /// prints. A connection that cannot be had ends the command as unreachable, with the reason the client
    /// gave — the app is not running, could not be started, or the tool runs elevated.
    public static func run(tool: String, arguments: JSONValue, json: Bool, version: String,
                           client: ControlClient, notice: () -> Void) -> ControlOutput {
        let stream: ControlStream
        do {
            stream = try client.connect()
        } catch {
            // `connect` throws only its Failure; anything else would leave the app unreachable too.
            return .unreachable((error as? ControlClient.Failure)?.message ?? ControlClient.unreachableError)
        }
        defer { stream.close() }
        return run(tool: tool, arguments: arguments, json: json, version: version, over: stream, notice: notice)
    }

    /// Runs the call over `stream` and says what to print. `notice` runs once, at the first progress
    /// report: the change is waiting its turn. An error answered with no id — a message too large —
    /// can only be the call's, the one request still open. A line past `limit` can never be read
    /// whole, so the command cannot say what happened: it ends as if the app had gone.
    public static func run(tool: String, arguments: JSONValue, json: Bool, version: String,
                           over stream: ControlStream, notice: () -> Void,
                           limit: Int = ControlClient.maxAnswerBytes) -> ControlOutput {
        let opening = openingLines(tool: tool, arguments: arguments, version: version).map { $0 + "\n" }.joined()
        guard stream.write(Data(opening.utf8)) else { return .unreachable(ControlClient.droppedError) }
        var framer = McpLineFramer(limit: limit)
        var noticed = false
        while true {
            let chunk = stream.read(maximum: 65_536)
            if chunk.isEmpty { return .unreachable(ControlClient.droppedError) }
            for frame in framer.append(chunk) {
                guard case .line(let bytes) = frame else { return .unreachable(ControlClient.droppedError) }
                guard case .object(let fields)? = try? JSONValue.parse(bytes) else { continue }
                if fields["method"] == .string("notifications/progress") {
                    if !noticed {
                        noticed = true
                        notice()
                    }
                } else if fields["method"] == nil,
                          fields["id"] == callID || (fields["id"] == JSONValue.null && fields["error"] != nil) {
                    return ControlOutput.answer(.object(fields), json: json)
                }
            }
        }
    }
}
