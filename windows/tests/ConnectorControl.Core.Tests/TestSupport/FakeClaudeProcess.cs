using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.Tests.TestSupport;

public sealed class FakeClaudeProcess : IClaudeProcess
{
    public bool IsRunning { get; set; }
    public DateTime? LaunchTime { get; set; }
    public string? RestartResult { get; set; }
    public int RestartCalls { get; private set; }
    /// <summary>Runs inside RestartAsync so a test can simulate the relaunch (new LaunchTime).</summary>
    public Action? OnRestart { get; set; }

    public Task<string?> RestartAsync(CancellationToken cancellationToken = default)
    {
        RestartCalls++;
        OnRestart?.Invoke();
        return Task.FromResult(RestartResult);
    }
}
