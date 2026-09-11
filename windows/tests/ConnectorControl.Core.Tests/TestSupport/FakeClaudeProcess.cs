using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.Tests.TestSupport;

public sealed class FakeClaudeProcess : IClaudeProcess
{
    private bool isRunning;
    private DateTime? launchDate;

    public bool IsRunning { get => Read(isRunning); set => isRunning = value; }
    public DateTime? LaunchDate { get => Read(launchDate); set => launchDate = value; }
    public string? RestartResult { get; set; }
    public int RestartCalls { get; private set; }
    /// <summary>Runs inside RestartAsync so a test can simulate the relaunch (new LaunchDate).</summary>
    public Action? OnRestart { get; set; }

    /// <summary>When set, every state read (IsRunning, LaunchDate, Snapshot) throws this instead of answering.</summary>
    public Exception? ThrowFromStateReads { get; set; }

    public ClaudeProcessSnapshot Snapshot() => new(IsRunning, LaunchDate);

    public Task<string?> RestartAsync(CancellationToken cancellationToken = default)
    {
        RestartCalls++;
        OnRestart?.Invoke();
        return Task.FromResult(RestartResult);
    }

    private T Read<T>(T value) => ThrowFromStateReads is { } ex ? throw ex : value;
}
