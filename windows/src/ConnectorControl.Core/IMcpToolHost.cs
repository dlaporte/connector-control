namespace ConnectorControl.Core;

/// <summary>
/// Mirror: Sources/ConnectorControlCore/McpSession.swift
///
/// What an MCP server needs from its tools: the list <c>tools/list</c> answers with, and how to run
/// one. Main-actor only, like the tools behind it. (C# has no actor isolation attribute: the Windows
/// control layer is UI-thread only by convention, as <c>AppState</c> is.)
/// </summary>
public interface IMcpToolHost
{
    IReadOnlyList<JsonValue> ListTools();

    /// <summary>
    /// What <c>tools/call</c> answers for a tool this host does not list, in the host's own words.
    /// </summary>
    string UnknownToolMessage(string name);

    /// <summary>
    /// Runs one tool. <paramref name="progress"/> may be called any number of times before
    /// <paramref name="completion"/>, which is called once, with the <c>tools/call</c> result object.
    /// </summary>
    void CallTool(string name, JsonValue arguments, JsonValue? progressToken,
                  Action<double> progress, Action<JsonValue> completion);
}
