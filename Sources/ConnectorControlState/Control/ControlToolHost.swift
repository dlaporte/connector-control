import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlToolHost.cs
///
/// The tools as one connection's MCP session sees them. The transport makes one per connection
/// and says, from the connection's hello, whether it is the command-line tool; anything else is
/// the relay, and the relay's asker is whoever the client said it was in `initialize`, read at
/// each call because the session learns it only then. `tools` is the app's one `ControlTools`,
/// shared by every connection's host, never one per connection: see its note.
@MainActor
public final class ControlToolHost: McpToolHost {
    public let tools: ControlTools
    public let isCLI: Bool
    /// The session this host serves, for the client's name. Weak: the session holds the host.
    public weak var session: McpSession?

    public init(tools: ControlTools, isCLI: Bool) {
        self.tools = tools
        self.isCLI = isCLI
    }

    /// What the session's `serverInfo` says.
    public var version: String { tools.version }

    /// Who is calling. A relay whose client gave no name — or whose session is gone — is still the
    /// relay, and the gate guards it.
    public var caller: ControlCaller { isCLI ? .cli : .relay(clientName: session?.clientName ?? "") }

    public func listTools() -> [JSONValue] { tools.definitions.map(\.mcpDescriptor) }

    public func callTool(name: String, arguments: JSONValue, progressToken: JSONValue?,
                         progress: @escaping (Double) -> Void, completion: @escaping (JSONValue) -> Void) {
        tools.call(name: name, arguments: arguments, caller: caller, progress: progress,
                   completion: { completion($0.mcpResult) })
    }
}
