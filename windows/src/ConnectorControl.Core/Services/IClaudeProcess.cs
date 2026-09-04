namespace ConnectorControl.Core.Services;

/// <summary>Replaces the Mac's NSRunningApplication + ClaudeRestarter.</summary>
public interface IClaudeProcess
{
    bool IsRunning { get; }

    /// <summary>
    /// Earliest start time across Claude's processes, always UTC
    /// (<see cref="DateTimeKind.Utc"/>), or null when not running. It is compared
    /// with <see cref="ISettings.LastApplyDate"/>, which is UTC too.
    /// </summary>
    DateTime? LaunchTime { get; }

    /// <summary>
    /// Gracefully quit Claude (never force-kill), wait up to 15 s, relaunch.
    /// Completes with null on success or the user-facing error message.
    /// </summary>
    Task<string?> RestartAsync(CancellationToken cancellationToken = default);
}
