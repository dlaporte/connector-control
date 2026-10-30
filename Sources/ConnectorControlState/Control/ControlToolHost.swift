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
    /// The version the connection's hello gave, or nil when it gave none.
    public var clientVersion: String?
    /// False once the connection has closed: a write it left waiting for its turn is dropped. The
    /// server clears it when it closes a connection, which is what keeps the two platforms the same:
    /// the closure `callTool` hands the tools captures the host weakly, to break the cycle tools →
    /// write queue → call → host, so a host that has been released also counts as closed.
    public var isOpen = true

    public init(tools: ControlTools, isCLI: Bool) {
        self.tools = tools
        self.isCLI = isCLI
    }

    /// What the session's `serverInfo` says.
    public var version: String { tools.version }

    /// Who is calling. A relay whose client gave no name - or whose session is gone - is still the
    /// relay, and the gate guards it.
    public var caller: ControlCaller { isCLI ? .cli : .relay(clientName: session?.clientName ?? "") }

    public func listTools() -> [JSONValue] { ControlTools.mcpDescriptors }

    /// A tool this app does not list. Asked for by a newer command-line tool or relay - an update
    /// replaced the app on disk while this one still runs - it needs the newer app, which a restart
    /// starts. Otherwise it is the app's own sentence, with the name escaped as every name the
    /// layer echoes is.
    public func unknownToolMessage(_ name: String) -> String {
        if let clientVersion, ControlVersion.isNewer(clientVersion, than: tools.version) {
            return ControlTools.needsNewerAppError(ControlVersion.semantic(clientVersion) ?? clientVersion)
        }
        return ControlTools.unknownToolError(ControlText.escapedName(name))
    }

    public func callTool(name: String, arguments: JSONValue, progressToken: JSONValue?,
                         progress: @escaping (Double) -> Void, completion: @escaping (JSONValue) -> Void) {
        tools.call(name: name, arguments: arguments, caller: caller, progress: progress,
                   completion: { completion($0.mcpResult) }, isAbandoned: { [weak self] in !(self?.isOpen ?? false) })
    }
}
