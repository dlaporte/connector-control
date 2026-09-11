namespace ConnectorControl.Core.Services;

/// <summary>
/// Whether Claude is running and, when it is, the earliest start time across its processes —
/// always UTC (<see cref="DateTimeKind.Utc"/>), null when <see cref="IsRunning"/> is false. It
/// is compared with <see cref="ISettings.LastApplyDate"/>, which is UTC too. Both come from one
/// enumeration of Claude's processes rather than two —
/// <see cref="ConnectorControl.Core.State.AppState.RefreshRestartState"/> needs both at once and
/// used to pay for each separately.
/// </summary>
public readonly record struct ClaudeProcessSnapshot(bool IsRunning, DateTime? LaunchDate);

/// <summary>Replaces the Mac's NSRunningApplication + ClaudeRestarter.</summary>
public interface IClaudeProcess
{
    /// <summary>Whether Claude is running and, when it is, its earliest start time — see <see cref="ClaudeProcessSnapshot"/>.</summary>
    ClaudeProcessSnapshot Snapshot();

    /// <summary>
    /// Gracefully quit Claude (never force-kill), wait up to 15 s, relaunch.
    /// Completes with null on success or the user-facing error message.
    /// Cancelling <paramref name="cancellationToken"/> while the 15 s wait is
    /// in progress throws <see cref="OperationCanceledException"/> (or the
    /// <see cref="TaskCanceledException"/> subclass) rather than returning a
    /// message; callers that pass a token must be ready to catch it.
    /// </summary>
    Task<string?> RestartAsync(CancellationToken cancellationToken = default);
}
