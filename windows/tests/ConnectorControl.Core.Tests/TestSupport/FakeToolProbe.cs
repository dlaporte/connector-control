namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// A machine with everything installed (<c>/fake/bin/&lt;tool&gt;</c>, version 1.0.0) unless a
/// test sets <see cref="Statuses"/>. Records what was probed; AppState calls it from the host's
/// Background work, which the harness runs on whichever thread drains its queue (a WPF test's
/// dispatcher thread, say), so <see cref="Probed"/> and <see cref="Batches"/> lock both the
/// write inside <see cref="Probe"/> and every read a test does.
/// </summary>
public sealed class FakeToolProbe : IToolProbe
{
    private readonly object gate = new();
    private readonly List<Tool> probed = [];
    private int batches;

    public Dictionary<Tool, ToolStatus> Statuses { get; } = [];

    public IReadOnlyList<Tool> Probed
    {
        get { lock (gate) { return probed.ToArray(); } }
    }

    public int Batches
    {
        get { lock (gate) { return batches; } }
    }

    public IReadOnlyDictionary<Tool, ToolStatus> Probe(IReadOnlyList<Tool> tools)
    {
        lock (gate)
        {
            batches++;
            probed.AddRange(tools);
            return tools.ToDictionary(
                t => t,
                t => Statuses.TryGetValue(t, out var status) ? status : new ToolStatus($"/fake/bin/{ToolInfo.Name(t)}", "1.0.0"));
        }
    }
}
