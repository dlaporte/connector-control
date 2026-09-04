namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>Captures AppHost.Delay calls so tests decide when "later" happens.</summary>
public sealed class DelayQueue
{
    public List<(TimeSpan Delay, Action Action)> Pending { get; } = [];

    public void Add(TimeSpan delay, Action action) => Pending.Add((delay, action));

    /// <summary>Runs the oldest pending action (a periodic action may re-add itself).</summary>
    public void RunNext()
    {
        var (_, action) = Pending[0];
        Pending.RemoveAt(0);
        action();
    }
}
