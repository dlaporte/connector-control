using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ControlToolHostTests.swift</summary>
public class ControlToolHostTests
{
    private static (McpSession Session, List<string> Sent) Connect(ControlRig rig, bool isCli, string client)
    {
        var sent = new List<string>();
        var host = new ControlToolHost(rig.Tools, isCli);
        var session = new McpSession(host, host.Version, sent.Add);
        host.Session = session;
        session.Receive("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","clientInfo":{"name":""" + "\"" + client + "\"}}}");
        return (session, sent);
    }

    [Fact]
    public void ToolsListIsTheCatalog()
    {
        using var rig = new ControlRig();
        var host = new ControlToolHost(rig.Tools, isCli: false);
        Assert.Equal(ControlTools.Catalog.Select(definition => definition.McpDescriptor), host.ListTools());
        Assert.Equal("1.2.2", host.Version);
    }

    [Fact]
    public void TheRelayIsNamedByTheClientItIntroducedItselfAs()
    {
        using var rig = new ControlRig();
        rig.Confirmer.Scripted = true;
        var (session, sent) = Connect(rig, isCli: false, client: "Cursor");
        session.Receive("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"add_connector","arguments":{"name":"x","config":{"command":"node"}}}}""");
        rig.H.Drain();
        Assert.Equal(ControlTools.AddTitle("Cursor", "x"), rig.Confirmer.Requests.LastOrDefault()?.Title);
        Assert.Contains("\"isError\":false", sent.LastOrDefault() ?? "");
    }

    [Fact]
    public void ClaudeDesktopsAgentModeIsClaude()
    {
        using var rig = new ControlRig();
        rig.Confirmer.Scripted = true;
        var (session, _) = Connect(rig, isCli: false, client: "local-agent-mode-connector-control");
        session.Receive("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"add_connector","arguments":{"name":"x","config":{"command":"node"}}}}""");
        rig.H.Drain();
        Assert.Equal(ControlTools.AddTitle(ControlCaller.ClaudeName, "x"), rig.Confirmer.Requests.LastOrDefault()?.Title);
    }

    [Fact]
    public void AClientThatGaveNoNameIsTheRelayAndIsAsked()
    {
        using var rig = new ControlRig();
        rig.Confirmer.Scripted = false;
        var sent = new List<string>();
        var host = new ControlToolHost(rig.Tools, isCli: false);
        var session = new McpSession(host, host.Version, sent.Add);
        host.Session = session;
        session.Receive("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""");
        Assert.Equal(new ControlCaller.Relay(""), host.Caller);
        session.Receive("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"add_connector","arguments":{"name":"x","config":{"command":"node"}}}}""");
        rig.H.Drain();
        Assert.Equal(ControlTools.AddTitle(ControlCaller.UnknownClientName, "x"), rig.Confirmer.Requests.LastOrDefault()?.Title);
        Assert.False(rig.State.Store.Mcps.ContainsKey("x"));
        Assert.Contains("\"isError\":true", sent.LastOrDefault() ?? "");
    }

    /// <summary>Only the transport's word makes a connection the command-line tool: a relay client that gives
    /// the tool's own name is still the relay, and still asked.</summary>
    [Fact]
    public void ARelayCallingItselfTheCommandLineToolIsStillAsked()
    {
        using var rig = new ControlRig();
        rig.Confirmer.Scripted = false;
        var (session, sent) = Connect(rig, isCli: false, client: MasterStore.ReservedConnectorName);
        Assert.Equal("connector-control", session.ClientName);
        session.Receive("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"add_connector","arguments":{"name":"x","config":{"command":"node"}}}}""");
        rig.H.Drain();
        Assert.Equal([ControlTools.AddTitle("connector-control", "x")], rig.Confirmer.Requests.Select(request => request.Title));
        Assert.False(rig.State.Store.Mcps.ContainsKey("x"));
        Assert.Contains("\"isError\":true", sent.LastOrDefault() ?? "");
    }

    [Fact]
    public void ACommandLineConnectionIsNeverAsked()
    {
        using var rig = new ControlRig();
        var (session, _) = Connect(rig, isCli: true, client: "connector-control");
        session.Receive("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"add_connector","arguments":{"name":"x","config":{"command":"node"}}}}""");
        rig.H.Drain();
        Assert.Empty(rig.Confirmer.Requests);
        Assert.True(rig.State.Store.Mcps.ContainsKey("x"));
    }

    [Fact]
    public void AResultReachesTheWireAsTheMcpResult()
    {
        using var rig = new ControlRig();
        var (session, sent) = Connect(rig, isCli: false, client: "claude-ai");
        session.Receive("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_collections"}}""");
        rig.H.Drain();
        var expected = rig.Call("list_collections")!.McpResult;
        Assert.Equal(JsonValue.Object(("jsonrpc", JsonValue.String("2.0")), ("id", JsonValue.Int(1)), ("result", expected)).CompactLine(),
            sent.LastOrDefault());
    }

    /// <summary><c>tools/call</c> checks the list on every call: it is built once, from the static catalog, and
    /// every connection's host lists the same descriptors.</summary>
    [Fact]
    public void EveryHostListsTheOneCachedCatalog()
    {
        using var rig = new ControlRig();
        var relay = new ControlToolHost(rig.Tools, isCli: false);
        var cli = new ControlToolHost(rig.Tools, isCli: true);
        Assert.Equal(ControlTools.Catalog.Select(definition => definition.McpDescriptor), ControlTools.McpDescriptors);
        Assert.Equal(ControlTools.McpDescriptors, relay.ListTools());
        Assert.Equal(ControlTools.McpDescriptors, cli.ListTools());
        // C# can show it is one list.
        Assert.Same(ControlTools.McpDescriptors, relay.ListTools());
        Assert.Same(ControlTools.McpDescriptors, cli.ListTools());
    }
}
