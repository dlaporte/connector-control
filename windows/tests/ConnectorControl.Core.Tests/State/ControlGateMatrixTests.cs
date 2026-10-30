using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// The gate on real tools, one of each class: every caller, both confirmation settings, the
/// integration on and off - whether the call was refused, asked about, run, and announced.
///
/// Mirror: Tests/ConnectorControlStateTests/ControlGateMatrixTests.swift
/// </summary>
public class ControlGateMatrixTests
{
    /// <summary>One real tool of each class, and how to tell it ran.</summary>
    private static readonly (ControlGateClass Gate, string Name, JsonValue Arguments, Func<ControlRig, ControlResult?, bool> Ran)[] Samples =
    [
        (ControlGateClass.Read, "get_status", JsonValue.Object(),
            (_, result) => result?.Structured.ValueAt(new JsonPointer(["activeCollection"])) is not null),
        (ControlGateClass.Direct, "switch_collection", JsonValue.Object(("collection", JsonValue.String("Work"))),
            (rig, _) => rig.State.ActiveCollection == "Work"),
        (ControlGateClass.Gated, "add_connector",
            JsonValue.Object(("name", JsonValue.String("x")), ("config", JsonValue.Object(("command", JsonValue.String("node"))))),
            (rig, _) => rig.State.Store.Mcps.ContainsKey("x")),
        (ControlGateClass.AlwaysConfirm, "restart_claude", JsonValue.Object(), (rig, _) => rig.H.Claude.RestartCalls == 1),
    ];

    [Fact]
    public void TheMatrix()
    {
        foreach (var (gate, name, arguments, ran) in Samples)
        {
            Assert.Equal(gate, ControlTools.Catalog.First(definition => definition.Name == name).Gate);
            foreach (var caller in new ControlCaller[] { new ControlCaller.Cli(), new ControlCaller.Relay("claude-ai") })
            {
                foreach (var integration in new[] { true, false })
                {
                    foreach (var confirm in new[] { true, false })
                    {
                        using var rig = new ControlRig(integration, confirm);
                        rig.Confirmer.Scripted = true;
                        Assert.Null(rig.State.AddEmptyCollection("Work"));
                        var label = $"{name} {caller} integration:{integration} confirm:{confirm}";
                        Assert.True(rig.H.Notifier.Sent.Count == 0, label);
                        var result = rig.Call(name, arguments, caller);
                        // Whatever the call left for later - the restart, after its answer - happens.
                        foreach (var _ in rig.H.Delays.Pending.ToList())
                        {
                            rig.H.Delays.RunNext();
                        }
                        rig.H.Drain();
                        var relay = caller is not ControlCaller.Cli;
                        var refused = relay && !integration;
                        var asked = relay && integration && (gate == ControlGateClass.AlwaysConfirm || (gate == ControlGateClass.Gated && confirm));
                        // A direct change is announced when it moves what Claude runs, as switching
                        // from Default to the empty Work does.
                        var announced = relay && integration && (gate == ControlGateClass.Direct || (gate == ControlGateClass.Gated && !confirm));
                        Assert.True(refused == (result == ControlResult.Refusal(ControlTools.IntegrationOffError)), label);
                        Assert.True(asked == (rig.Confirmer.Requests.Count > 0), label);
                        Assert.True(announced == (rig.H.Notifier.Sent.Count > 0), label);
                        Assert.True(!refused == ran(rig, result), label);
                        if (!refused)
                        {
                            Assert.True(result?.IsError == false, label);
                        }
                    }
                }
            }
        }
    }
}
