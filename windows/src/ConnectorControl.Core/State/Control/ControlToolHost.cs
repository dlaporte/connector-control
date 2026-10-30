namespace ConnectorControl.Core.State;

/// <summary>
/// The tools as one connection's MCP session sees them. The transport makes one per connection
/// and says, from the connection's hello, whether it is the command-line tool; anything else is
/// the relay, and the relay's asker is whoever the client said it was in <c>initialize</c>, read at
/// each call because the session learns it only then. <c>Tools</c> is the app's one <see cref="ControlTools"/>,
/// shared by every connection's host, never one per connection: see its note.
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
    /// <summary>The version the connection's hello gave, or null when it gave none.</summary>
    public string? ClientVersion { get; set; }
    /// <summary>False once the connection has closed: a write it left waiting for its turn is dropped. The
    /// server clears it when it closes a connection, which is what keeps the two platforms the same:
    /// <c>CallTool</c> captures the host with a plain reference, as the GC has no cycle to break, where
    /// the Mac's closure is weak and counts a released host as closed.</summary>
    public bool IsOpen { get; set; } = true;

    /// <summary>What the session's <c>serverInfo</c> says.</summary>
    public string Version => Tools.Version;

    /// <summary>Who is calling. A relay whose client gave no name - or whose session is gone - is
    /// still the relay, and the gate guards it.</summary>
    public ControlCaller Caller => IsCli ? new ControlCaller.Cli() : new ControlCaller.Relay(Session?.ClientName ?? "");

    public IReadOnlyList<JsonValue> ListTools() => ControlTools.McpDescriptors;

    /// <summary>A tool this app does not list. Asked for by a newer command-line tool or relay - an update
    /// replaced the app on disk while this one still runs - it needs the newer app, which a restart
    /// starts. Otherwise it is the app's own sentence, with the name escaped as every name the
    /// layer echoes is.</summary>
    public string UnknownToolMessage(string name) =>
        ClientVersion is { } clientVersion && ControlVersion.IsNewer(clientVersion, Tools.Version)
            ? ControlTools.NeedsNewerAppError(ControlVersion.Semantic(clientVersion) ?? clientVersion)
            : ControlTools.UnknownToolError(ControlText.EscapedName(name));

    public void CallTool(string name, JsonValue arguments, JsonValue? progressToken,
                         Action<double> progress, Action<JsonValue> completion) =>
        Tools.Call(name, arguments, Caller, progress, result => completion(result.McpResult), () => !IsOpen);
}
