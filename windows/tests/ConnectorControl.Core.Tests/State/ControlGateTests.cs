using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ControlGateTests.swift</summary>
public class ControlGateTests
{
    private static readonly ControlGateClass[] Classes = [ControlGateClass.Read, ControlGateClass.Direct, ControlGateClass.Gated, ControlGateClass.AlwaysConfirm];

    [Fact]
    public void TheCommandLineIsNeverGated()
    {
        foreach (var gate in Classes)
        {
            foreach (var integration in new[] { true, false })
            {
                foreach (var confirm in new[] { true, false })
                {
                    Assert.Equal(new ControlGate.Decision.Run(),
                        ControlGate.Decide(new ControlCaller.Cli(), gate, integration, confirm));
                }
            }
        }
    }

    [Fact]
    public void TheRelaysMatrix()
    {
        ControlGate.Decision off = new ControlGate.Decision.Refuse(ControlTools.IntegrationOffError);
        ControlGate.Decision run = new ControlGate.Decision.Run();
        ControlGate.Decision confirm = new ControlGate.Decision.Confirm();
        ControlGate.Decision runAndNotify = new ControlGate.Decision.RunAndNotify();
        // gate class, integration on, confirmations on, what happens
        var rows = new (ControlGateClass Gate, bool Integration, bool Confirm, ControlGate.Decision Expected)[]
        {
            (ControlGateClass.Read, true, true, run), (ControlGateClass.Read, true, false, run), (ControlGateClass.Read, false, true, off), (ControlGateClass.Read, false, false, off),
            (ControlGateClass.Direct, true, true, run), (ControlGateClass.Direct, true, false, run), (ControlGateClass.Direct, false, true, off), (ControlGateClass.Direct, false, false, off),
            (ControlGateClass.Gated, true, true, confirm), (ControlGateClass.Gated, true, false, runAndNotify), (ControlGateClass.Gated, false, true, off), (ControlGateClass.Gated, false, false, off),
            (ControlGateClass.AlwaysConfirm, true, true, confirm), (ControlGateClass.AlwaysConfirm, true, false, confirm),
            (ControlGateClass.AlwaysConfirm, false, true, off), (ControlGateClass.AlwaysConfirm, false, false, off),
        };
        foreach (var (gate, integration, confirmChanges, expected) in rows)
        {
            Assert.True(expected == ControlGate.Decide(new ControlCaller.Relay("claude-ai"), gate, integration, confirmChanges),
                $"{gate} integration:{integration} confirm:{confirmChanges}");
        }
    }

    [Fact]
    public void TheAskerIsClaudeForClaudeDesktopAndTheClientsOwnNameOtherwise()
    {
        Assert.Equal("Claude", new ControlCaller.Relay("claude-ai").AskerName);
        Assert.Equal("Claude", new ControlCaller.Relay("Claude Desktop").AskerName);
        Assert.Equal("Claude", new ControlCaller.Relay("local-agent-mode-connector-control").AskerName);
        Assert.Equal("Cursor", new ControlCaller.Relay("Cursor").AskerName);
        Assert.Equal(ControlCaller.UnknownClientName, new ControlCaller.Relay("").AskerName);
    }

    [Fact]
    public void ArgumentsAreCheckedAgainstTheSchemaBeforeAnythingRuns()
    {
        using var rig = new ControlRig();
        Assert.Equal(ControlResult.Refusal(ControlTools.MissingArgumentError("name")), rig.Call("get_connector"));
        Assert.Equal(ControlResult.Refusal(ControlTools.WrongTypeError("name", ControlTools.TextKind)),
            rig.Call("get_connector", JsonValue.Object(("name", JsonValue.Int(3)))));
        Assert.Equal(ControlResult.Refusal(ControlTools.NotOneOfError("key", string.Join(", ", ControlSettingKey.All))),
            rig.Call("set_setting", JsonValue.Object(("key", JsonValue.String("theme")), ("value", JsonValue.String("on")))));
        Assert.Equal(ControlResult.Refusal(ControlTools.UnknownToolError("no_such_tool")), rig.Call("no_such_tool"));
        Assert.False(rig.Call("get_status")?.IsError);
    }

    [Fact]
    public void EveryRelayCallIsRefusedWhileTheIntegrationIsOff()
    {
        using var rig = new ControlRig(integration: false);
        Assert.Equal(ControlResult.Refusal(ControlTools.IntegrationOffError), rig.Call("get_status"));
        Assert.Equal(ControlResult.Refusal(ControlTools.IntegrationOffError), rig.Call("list_connectors"));
        // The command line is the user.
        Assert.False(rig.Call("get_status", caller: new ControlCaller.Cli())?.IsError);
    }

    [Fact]
    public void AResultCarriesItsTextAndStructureTheWayMcpWantsThem()
    {
        var result = new ControlResult(JsonValue.Object(("a", JsonValue.Int(1))), "done", false);
        Assert.Equal(JsonValue.Object(
            ("content", JsonValue.Array([JsonValue.Object(("type", JsonValue.String("text")), ("text", JsonValue.String("done")))])),
            ("structuredContent", JsonValue.Object(("a", JsonValue.Int(1)))),
            ("isError", JsonValue.Bool(false))), result.McpResult);
        Assert.Equal(new ControlResult(JsonValue.Object(("error", JsonValue.String("no"))), "no", true), ControlResult.Refusal("no"));
    }

    /// <summary>The tool name a refusal echoes is Claude's own text, so it shows as a name does.</summary>
    [Fact]
    public void AnUnknownToolShowsItsNameOneWayOnly()
    {
        using var rig = new ControlRig();
        Assert.Equal(ControlResult.Refusal(ControlTools.UnknownToolError("""bad\n\\name""")), rig.Call("bad\n\\name"));
    }
}
