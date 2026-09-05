namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// A machine with everything installed (<c>/fake/bin/&lt;tool&gt;</c>, version 1.0.0) unless a
/// test sets <see cref="Statuses"/>. Records what was probed; AppState calls it from a pool
/// thread, so the lists are locked.
/// </summary>
public sealed class FakeToolProbe : IToolProbe
{
    private readonly object gate = new();

    public Dictionary<Tool, ToolStatus> Statuses { get; } = [];

    public List<Tool> Probed { get; } = [];

    public int Batches { get; private set; }

    public IReadOnlyDictionary<Tool, ToolStatus> Probe(IReadOnlyList<Tool> tools)
    {
        lock (gate)
        {
            Batches++;
            Probed.AddRange(tools);
            return tools.ToDictionary(
                t => t,
                t => Statuses.TryGetValue(t, out var status) ? status : new ToolStatus($"/fake/bin/{ToolInfo.Name(t)}", "1.0.0"));
        }
    }
}
