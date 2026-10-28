using System.Text;
using ConnectorControl.Core.Tests.TestSupport;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.Transport;

/// <summary>
/// Mirror: Tests/ConnectorControlStateTests/Transport/ControlServerTests.swift
///
/// The server apart from any socket: connections the test opens, feeds and closes.
/// </summary>
public class ControlServerTests
{
    private sealed class FakeConnection : IControlConnection
    {
        public List<string> Sent { get; } = [];
        public bool Closed { get; private set; }
        public void Send(string line) => Sent.Add(line);
        public void Close() => Closed = true;
    }

    private const string InitializeMessage = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"claude-ai","version":"1"}}}""";
    private const string InitializedMessage = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    private static McpFrame Line(string text) => new McpFrame.Line(Encoding.UTF8.GetBytes(text));

    private static void Say(ControlServer server, FakeConnection connection, params string[] lines)
    {
        foreach (var text in lines)
        {
            server.Receive(Line(text), connection);
        }
    }

    [Fact]
    public void EveryConnectionGetsItsOwnHostAndSessionOverTheOneControlTools()
    {
        using var rig = new ControlRig();
        var server = new ControlServer(rig.Tools);
        var cli = new FakeConnection();
        var relay = new FakeConnection();
        Assert.True(server.Open(cli));
        Assert.True(server.Open(relay));
        Say(server, cli, ControlHello.Line(ControlHello.CliClient, "1.2.2"));
        Say(server, relay, ControlHello.Line(ControlHello.RelayClient, "1.2.2"));
        Assert.Equal(2, server.Hosts.Count);
        Assert.All(server.Hosts, host => Assert.Same(rig.Tools, host.Tools));   // one ControlTools, shared by every connection
        Assert.Equal([true, false], server.Hosts.Select(host => host.IsCli));
        Assert.Equal(["1.2.2", "1.2.2"], server.Hosts.Select(host => host.ClientVersion));
        Assert.NotSame(server.Hosts[0].Session, server.Hosts[1].Session);
    }

    [Fact]
    public void TheHelloIsConsumedAndNeverReachesTheSession()
    {
        using var rig = new ControlRig();
        var server = new ControlServer(rig.Tools);
        var connection = new FakeConnection();
        server.Open(connection);
        Say(server, connection, InitializeMessage);
        Assert.Empty(connection.Sent);   // a first line is the hello, and a hello is never answered
        Assert.Equal([false], server.Hosts.Select(host => host.IsCli));   // an MCP message is no command-line hello
        Say(server, connection, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        Assert.Equal(["""{"error":{"code":-32002,"message":"Server not initialized"},"id":2,"jsonrpc":"2.0"}"""], connection.Sent);
    }

    [Fact]
    public void AnOversizedHelloIsTheRelayAndTheConnectionCarriesOn()
    {
        using var rig = new ControlRig();
        var server = new ControlServer(rig.Tools);
        var connection = new FakeConnection();
        server.Open(connection);
        server.Receive(new McpFrame.TooLarge(), connection);
        Assert.Equal([false], server.Hosts.Select(host => host.IsCli));
        Assert.Null(server.Hosts.FirstOrDefault()?.ClientVersion);
        Say(server, connection, InitializeMessage);
        Assert.Single(connection.Sent);
        Assert.Contains("\"serverInfo\"", connection.Sent[0]);
    }

    [Fact]
    public void AHelloThatIsNotUtf8IsTheRelay()
    {
        using var rig = new ControlRig();
        var server = new ControlServer(rig.Tools);
        var connection = new FakeConnection();
        server.Open(connection);
        byte[] bytes =
        [
            .. Encoding.UTF8.GetBytes("{\"client\":\"cli\",\"version\":\"1.2.2\",\"x\":\""),
            0xFF,
            .. Encoding.UTF8.GetBytes("\"}"),
        ];
        server.Receive(new McpFrame.Line(bytes), connection);
        Assert.Equal([false], server.Hosts.Select(host => host.IsCli));
        Assert.Null(server.Hosts.FirstOrDefault()?.ClientVersion);
        Assert.Empty(connection.Sent);
    }

    [Fact]
    public void FramesAfterTheHelloReachTheSessionAsBytes()
    {
        using var rig = new ControlRig();
        var server = new ControlServer(rig.Tools);
        var connection = new FakeConnection();
        server.Open(connection);
        Say(server, connection, ControlHello.Line(ControlHello.CliClient, "1.2.2"));
        server.Receive(new McpFrame.TooLarge(), connection);
        server.Receive(new McpFrame.Line([0x7B, 0xFF, 0x7D]), connection);
        Assert.Equal(
        [
            """{"error":{"code":-32600,"message":"Message too large"},"id":null,"jsonrpc":"2.0"}""",
            """{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}""",
        ], connection.Sent);
    }

    [Fact]
    public void TheServerTakesAHandfulOfConnectionsAndClosesTheRest()
    {
        using var rig = new ControlRig();
        var server = new ControlServer(rig.Tools);
        var connections = Enumerable.Range(0, ControlServer.MaxConnections).Select(_ => new FakeConnection()).ToList();
        Assert.All(connections, connection => Assert.True(server.Open(connection)));
        var extra = new FakeConnection();
        Assert.False(server.Open(extra));
        Assert.True(extra.Closed);   // closed unanswered
        server.Close(connections[0]);
        Assert.True(connections[0].Closed);
        Assert.True(server.Open(new FakeConnection()));   // a closed connection frees its place
        Assert.Equal(ControlServer.MaxConnections, server.ConnectionCount);
    }

    [Fact]
    public void AClosedConnectionsHostIsClosedAndItsFramesAreIgnored()
    {
        using var rig = new ControlRig();
        var server = new ControlServer(rig.Tools);
        var connection = new FakeConnection();
        var other = new FakeConnection();
        server.Open(connection);
        server.Open(other);
        Say(server, connection, ControlHello.Line(ControlHello.CliClient, "1.2.2"));
        Say(server, other, ControlHello.Line(ControlHello.RelayClient, "1.2.2"));
        var host = server.Hosts[0];
        server.Close(connection);
        Assert.False(host.IsOpen);
        Say(server, connection, InitializeMessage);
        Assert.Empty(connection.Sent);
        var relayHost = server.Hosts[0];
        server.CloseAll();
        Assert.False(relayHost.IsOpen);
        Assert.True(other.Closed);
        Assert.Equal(0, server.ConnectionCount);
    }

    /// <summary>An asking call whose connection closed is still answered once the user responds; that
    /// answer goes to a closed session and must reach nothing, on a connection the test still holds.</summary>
    [Fact]
    public void AnAnswerAfterTheConnectionClosedIsSentNowhere()
    {
        using var rig = new ControlRig();
        var server = new ControlServer(rig.Tools);
        var connection = new FakeConnection();
        server.Open(connection);
        Say(server, connection, ControlHello.Line(ControlHello.RelayClient, "1.2.2"), InitializeMessage, InitializedMessage,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"delete_connectors","arguments":{"names":["aws-mcp"]}}}""");
        rig.H.Drain();
        Assert.Equal(1, rig.Confirmer.WaitingCount);
        var before = connection.Sent.Count;
        server.Close(connection);
        rig.Confirmer.Answer(true);
        rig.H.Drain();
        Assert.Equal(before, connection.Sent.Count);   // nothing is written after close
        var other = new FakeConnection();
        server.Open(other);
        server.CloseAll();
        Assert.Empty(other.Sent);
    }

    /// <summary>The point of sharing one ControlTools: a change from one connection waits behind another
    /// connection's open dialog, as Claude's two relays and a command line must.</summary>
    [Fact]
    public void AWriteFromOneConnectionWaitsBehindAnotherConnectionsDialog()
    {
        using var rig = new ControlRig();
        var server = new ControlServer(rig.Tools);
        var chat = new FakeConnection();
        var agent = new FakeConnection();
        server.Open(chat);
        server.Open(agent);
        Say(server, chat, ControlHello.Line(ControlHello.RelayClient, "1.2.2"), InitializeMessage, InitializedMessage,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"delete_connectors","arguments":{"names":["aws-mcp"]}}}""");
        Say(server, agent, ControlHello.Line(ControlHello.RelayClient, "1.2.2"), InitializeMessage, InitializedMessage,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"create_collection","arguments":{"name":"Later"}}}""");
        rig.H.Drain();
        Assert.Equal(1, rig.Confirmer.WaitingCount);
        Assert.Single(agent.Sent);   // the second connection's write waits its turn
        Assert.False(rig.State.Store.Collections.ContainsKey("Later"));
        rig.Confirmer.Answer(false);
        rig.H.Drain();
        Assert.Equal(2, agent.Sent.Count);
        Assert.True(rig.State.Store.Collections.ContainsKey("Later"));
    }

    /// <summary>The gate goes by the hello: the command line runs a gated change without asking; the relay
    /// is asked, and has no answer until the user gives one.</summary>
    [Fact]
    public void TheCommandLineRunsAGatedChangeAndTheRelayIsAsked()
    {
        using var rig = new ControlRig();
        var server = new ControlServer(rig.Tools);
        var cli = new FakeConnection();
        var relay = new FakeConnection();
        server.Open(cli);
        server.Open(relay);
        const string Delete = """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"delete_connectors","arguments":{"names":["aws-mcp"]}}}""";
        Say(server, relay, ControlHello.Line(ControlHello.RelayClient, "1.2.2"), InitializeMessage, InitializedMessage, Delete);
        rig.H.Drain();
        Assert.Single(rig.Confirmer.Requests);   // the relay is asked
        Assert.Single(relay.Sent);   // and has only its initialize answer
        rig.Confirmer.Answer(false);
        Say(server, cli, ControlHello.Line(ControlHello.CliClient, "1.2.2"), InitializeMessage, InitializedMessage, Delete);
        rig.H.Drain();
        Assert.Single(rig.Confirmer.Requests);   // the command line is never asked
        Assert.Contains("\"isError\":false", cli.Sent.Last());
        Assert.False(rig.State.Store.Mcps.ContainsKey("aws-mcp"));
    }
}
