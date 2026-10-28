namespace ConnectorControl.Core.State;

/// <summary>
/// The tools as one connection's MCP session sees them. The transport makes one per connection
/// and says, from the connection's hello, whether it is the command-line tool; anything else is
/// the relay, and the relay's asker is whoever the client said it was in <c>initialize</c>, read at
/// each call because the session learns it only then.
///
/// Mirror: Sources/ConnectorControlState/Control/ControlToolHost.swift
/// </summary>
public sealed class ControlToolHost(ControlTools tools, bool isCli) : IMcpToolHost
{
    public ControlTools Tools { get; } = tools;
    public bool IsCli { get; } = isCli;
    /// <summary>The session this host serves, for the client's name. A plain reference: the GC has
    /// no cycle to break, where the Mac's is weak.</summary>
    public McpSession? Session { get; set; }

    /// <summary>What the session's <c>serverInfo</c> says.</summary>
    public string Version => Tools.Version;

    /// <summary>Who is calling. A relay whose client gave no name — or whose session is gone — is
    /// still the relay, and the gate guards it.</summary>
    public ControlCaller Caller => IsCli ? new ControlCaller.Cli() : new ControlCaller.Relay(Session?.ClientName ?? "");

    public IReadOnlyList<JsonValue> ListTools() => Tools.Definitions.Select(definition => definition.McpDescriptor).ToList();

    public void CallTool(string name, JsonValue arguments, JsonValue? progressToken,
                         Action<double> progress, Action<JsonValue> completion) =>
        Tools.Call(name, arguments, Caller, progress, result => completion(result.McpResult));
}
