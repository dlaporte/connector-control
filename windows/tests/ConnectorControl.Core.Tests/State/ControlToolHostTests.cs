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
        Assert.Equal(ControlTools.AddTitle(ControlCaller.CallingItselfText("Cursor"), "x"), rig.Confirmer.Requests.LastOrDefault()?.Title);
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
        Assert.Equal([ControlTools.AddTitle(ControlCaller.CallingItselfText("connector-control"), "x")], rig.Confirmer.Requests.Select(request => request.Title));
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

    /// <summary>A tool or relay newer than the app it reached - an update replaced the app on disk while the
    /// old one still runs - is told to restart the app, not that the tool does not exist.</summary>
    [Fact]
    public void ANewerClientAskingForAToolThisAppLacksIsToldToRestartTheApp()
    {
        using var rig = new ControlRig();
        var host = new ControlToolHost(rig.Tools, isCli: true);
        var unknown = ControlTools.UnknownToolError("future_tool");
        // No version was said.
        Assert.Equal(unknown, host.UnknownToolMessage("future_tool"));
        foreach (var older in new[] { "1.2.2", "1.2.1", "1.2.2-preview.1", "development build" })
        {
            host.ClientVersion = older;
            Assert.Equal(unknown, host.UnknownToolMessage("future_tool"));
        }
        host.ClientVersion = "99.0.0";
        Assert.Equal(ControlTools.NeedsNewerAppError("99.0.0"), host.UnknownToolMessage("future_tool"));
        host.ClientVersion = "1.3.0-preview.2";
        Assert.Equal(ControlTools.NeedsNewerAppError("1.3.0-preview.2"), host.UnknownToolMessage("future_tool"));
    }

    /// <summary>A tool nothing lists gets the app's own sentence, with the name escaped as every other name
    /// the layer echoes is.</summary>
    [Fact]
    public void AnUnknownToolIsRefusedInTheAppsWordsWithItsNameEscaped()
    {
        using var rig = new ControlRig();
        var host = new ControlToolHost(rig.Tools, isCli: true);
        Assert.Equal(ControlTools.UnknownToolError("no_such_tool"), host.UnknownToolMessage("no_such_tool"));
        Assert.Equal(ControlTools.UnknownToolError("""bad\n\\name"""), host.UnknownToolMessage("bad\n\\name"));
    }

    /// <summary>The version the session introduces itself with, and the one a hello is compared with, is the
    /// version alone: the Mac shows its build number beside it, Windows does not.</summary>
    [Fact]
    public void TheToolsVersionIsTheSemanticVersionTheAppShows()
    {
        using var rig = new ControlRig();
        rig.Updater.VersionDisplay = "1.5.0 (10500)";
        Assert.Equal("1.5.0", rig.Tools.Version);
        // A development build says so. Windows never shows a build number, but the rule is the same.
        rig.Updater.VersionDisplay = "development build";
        Assert.Equal("development build", rig.Tools.Version);
    }

    /// <summary>The host says when its connection has closed, so a write still waiting for its turn is dropped.</summary>
    [Fact]
    public void AClosedHostsWaitingWriteIsDropped()
    {
        using var rig = new ControlRig();
        rig.Confirmer.Scripted = null;
        rig.Start("delete_connectors", JsonValue.Object(("names", JsonValue.Array([JsonValue.String("aws-mcp")]))));
        // A dialog holds the line.
        Assert.Equal(1, rig.Confirmer.WaitingCount);
        var host = new ControlToolHost(rig.Tools, isCli: true);
        var answers = new List<JsonValue>();
        host.CallTool("switch_collection", JsonValue.Object(("collection", JsonValue.String("Default"))), null,
            _ => { }, answers.Add);
        host.IsOpen = false;
        rig.Confirmer.Answer(false);
        rig.H.Drain();
        // Nobody is left to answer.
        Assert.Empty(answers);
    }
}
