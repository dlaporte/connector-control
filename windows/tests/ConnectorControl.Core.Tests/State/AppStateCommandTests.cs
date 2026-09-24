using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class AppStateCommandTests
{
    [Fact]
    public void QuitAsksForConfirmationByDefault()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var quit = 0;
        state.QuitRequested += () => quit++;
        h.Dialogs.NextConfirm = false;
        state.QuitApp();
        Assert.Equal(0, quit);
        Assert.Equal(new FakeDialogs.ConfirmCall("Quit Connector Control?", null, "Quit", "Cancel", false), h.Dialogs.Confirms[0]);
        h.Dialogs.NextConfirm = true;
        state.QuitApp();
        Assert.Equal(1, quit);
    }

    [Fact]
    public void QuitSkipsTheConfirmationWhenDisabled()
    {
        using var h = new AppStateHarness();
        h.Settings.ConfirmBeforeQuit = false;
        using var state = h.Create();
        var quit = 0;
        state.QuitRequested += () => quit++;
        state.QuitApp();
        Assert.Equal(1, quit);
        Assert.Empty(h.Dialogs.Confirms);
    }

    [Fact]
    public async Task RestartClaudeConfirmsThenRestarts()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        await state.RestartClaudeAsync();
        Assert.Equal(new FakeDialogs.ConfirmCall("Restart Claude Desktop now?", "Any in-progress Claude conversation will be interrupted.", "Restart", "Cancel", false), h.Dialogs.Confirms[0]);
        Assert.Equal(1, h.Claude.RestartCalls);
    }

    [Fact]
    public async Task RestartClaudeCancelledDoesNothing()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        h.Dialogs.NextConfirm = false;
        await state.RestartClaudeAsync();
        Assert.Equal(0, h.Claude.RestartCalls);
    }

    [Fact]
    public async Task RestartClaudeSkipsTheConfirmationWhenDisabled()
    {
        using var h = new AppStateHarness();
        h.Settings.ConfirmBeforeRestart = false;
        using var state = h.Create();
        await state.RestartClaudeAsync();
        Assert.Empty(h.Dialogs.Confirms);
        Assert.Equal(1, h.Claude.RestartCalls);
    }

    [Fact]
    public async Task RestartErrorLandsInLastErrorAndARecheckIsScheduled()
    {
        using var h = new AppStateHarness();
        h.Settings.ConfirmBeforeRestart = false;
        h.Settings.LastApplyDate = h.Now;
        h.Claude.IsRunning = true;
        h.Claude.LaunchDate = h.Now.AddHours(-1);
        using var state = h.Create();
        Assert.True(state.NeedsClaudeRestart);
        h.Claude.RestartResult = "Claude didn’t quit (it may be showing a dialog). Quit it manually, then click Restart Claude again.";
        await state.RestartClaudeAsync();
        Assert.Equal(1, h.Ui.Pending);   // the completion is posted through the host, not run inline
        h.Ui.Pump();
        Assert.Equal(h.Claude.RestartResult, state.LastError);
        Assert.True(state.NeedsClaudeRestart);
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20)], h.Delays.Pending.Select(d => d.Delay).ToArray());
        h.Claude.IsRunning = false;   // the user quit it by hand in the meantime
        h.Delays.RunNext();
        Assert.False(state.NeedsClaudeRestart);
        h.Delays.RunNext();           // the 20 s look: LastError already says what went wrong, so leave it
        Assert.Equal(h.Claude.RestartResult, state.LastError);
    }

    /// <summary>C#-only: the Mac's restart reports through a completion handler and cannot throw,
    /// while Windows awaits a Task that can.</summary>
    [Fact]
    public async Task RestartExceptionOutsideTheLaunchGuardStillCompletesWithAMessage()
    {
        using var h = new AppStateHarness();
        h.Settings.ConfirmBeforeRestart = false;
        using var state = h.Create();
        h.Claude.OnRestart = () => throw new InvalidOperationException("boom");
        await state.RestartClaudeAsync();
        h.Ui.Pump();
        Assert.Equal(1, h.Claude.RestartCalls);
        Assert.Equal("boom", state.LastError);
        // Same schedule as the success path: the marshalled completion always runs to the end.
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20)], h.Delays.Pending.Select(d => d.Delay).ToArray());
    }

    /// <summary>C#-only: Windows relaunches through explorer.exe, which reports success before Claude
    /// appears, so it looks again 20 s later; the Mac's restarter launches Claude itself and
    /// reports a failed launch through its completion.</summary>
    [Fact]
    public async Task ARelaunchThatSilentlyFailedIsReportedTwentySecondsLater()
    {
        using var h = new AppStateHarness();
        h.Settings.ConfirmBeforeRestart = false;
        h.Settings.LastApplyDate = h.Now;
        h.Claude.IsRunning = true;
        h.Claude.LaunchDate = h.Now.AddHours(-1);
        using var state = h.Create();
        // RestartAsync reports success: explorer.exe accepted the AUMID. Claude never appeared.
        h.Claude.OnRestart = () =>
        {
            h.Claude.IsRunning = false;
            h.Claude.LaunchDate = null;
        };
        await state.RestartClaudeAsync();
        h.Ui.Pump();
        Assert.Null(state.LastError);
        Assert.Equal(AppState.RestartRecheckDelay, h.Delays.Pending[0].Delay);
        Assert.Equal(AppState.RestartRelaunchCheck, h.Delays.Pending[1].Delay);

        h.Delays.RunNext();           // 3 s: too early to conclude anything
        Assert.Null(state.LastError);
        h.Delays.RunNext();           // 20 s: the probe's relaunch bound has passed
        Assert.Equal(AppState.RelaunchFailedMessage, state.LastError);
        Assert.Equal("Claude didn’t come back after the restart. Start Claude yourself, then try again.", AppState.RelaunchFailedMessage);
    }

    [Fact]
    public async Task RestartSuccessClearsTheErrorAndTheRestartState()
    {
        using var h = new AppStateHarness();
        h.Settings.ConfirmBeforeRestart = false;
        h.Settings.LastApplyDate = h.Now;
        h.Claude.IsRunning = true;
        h.Claude.LaunchDate = h.Now.AddHours(-1);
        using var state = h.Create();
        state.LastError = "old banner";
        h.Claude.OnRestart = () => h.Claude.LaunchDate = h.Now.AddSeconds(1);
        await state.RestartClaudeAsync();
        h.Ui.Pump();
        Assert.Null(state.LastError);
        Assert.False(state.NeedsClaudeRestart);
    }

    [Fact]
    public void ToastRestartActionIsGuardedByAPendingRestartAndSkipsTheConfirmation()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        h.Notifier.ActivateRestart();   // stale click: nothing pending
        Assert.Equal(0, h.Claude.RestartCalls);

        h.Claude.IsRunning = true;
        h.Claude.LaunchDate = h.Now.AddHours(-1);
        state.SetEnabled("aws-mcp", false);
        Assert.True(state.NeedsClaudeRestart);
        h.Notifier.ActivateRestart();
        Assert.Equal(1, h.Claude.RestartCalls);
        Assert.Empty(h.Dialogs.Confirms);   // the explicit action click IS the confirmation
    }

    [Fact]
    public void DisposeUnsubscribesTheToastRestartAction()
    {
        using var h = new AppStateHarness();
        var state = h.Create();
        h.Claude.IsRunning = true;
        h.Claude.LaunchDate = h.Now.AddHours(-1);
        state.SetEnabled("aws-mcp", false);
        Assert.True(state.NeedsClaudeRestart);   // a pending restart, so ActivateRestart would act if still wired up
        state.Dispose();
        h.Notifier.ActivateRestart();
        Assert.Equal(0, h.Claude.RestartCalls);
    }

    [Fact]
    public async Task PendingRestartDelaysAreNoOpsAfterDispose()
    {
        using var h = new AppStateHarness();
        h.Settings.ConfirmBeforeRestart = false;
        h.Settings.LastApplyDate = h.Now;
        h.Claude.IsRunning = true;
        h.Claude.LaunchDate = h.Now.AddHours(-1);
        var state = h.Create();
        await state.RestartClaudeAsync();
        h.Ui.Pump();
        Assert.Equal(2, h.Delays.Pending.Count);
        var errorBefore = state.LastError;
        var needsRestartBefore = state.NeedsClaudeRestart;
        h.Claude.IsRunning = false;   // without the guard the 20 s check would now report a failed relaunch
        state.Dispose();
        h.Delays.RunNext();   // 3 s recheck: must be a no-op post-Dispose, not throw
        h.Delays.RunNext();   // 20 s relaunch check: same
        Assert.Equal(errorBefore, state.LastError);
        Assert.Equal(needsRestartBefore, state.NeedsClaudeRestart);

        // The completion block itself — not only the two delayed follow-ups — must be a no-op
        // once disposed: a restart that finishes after Dispose (the app quitting mid-restart)
        // must not resurrect state or schedule fresh delays.
        state.LastError = "should not survive";
        await state.PerformRestartClaudeAsync();
        h.Ui.Pump();
        Assert.Equal("should not survive", state.LastError);
        Assert.Empty(h.Delays.Pending);   // no new delays scheduled on a disposed state
    }

    [Fact]
    public void SwitchCollectionAppliesImmediately()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Work"));
        Assert.Equal("Work", state.ActiveCollection);
        state.SetEnabled("aws-mcp", false);
        Assert.Equal(["scoutbook", "service-now"], AppStateHarness.Keys(h.ClaudeServers().Keys));

        h.Settings.LastApplyDate = null;
        state.SwitchCollection("Default");
        Assert.Equal("Default", state.ActiveCollection);
        Assert.Equal(["aws-mcp", "scoutbook", "service-now"], AppStateHarness.Keys(h.ClaudeServers().Keys));
        Assert.Equal(h.Now, h.Settings.LastApplyDate);
        Assert.Equal("Default", h.StoreOnDisk().ActiveCollection);
    }

    [Fact]
    public void SwitchCollectionIgnoresAnUnknownName()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.SwitchCollection("Nope");
        Assert.Equal("Default", state.ActiveCollection);
        Assert.Null(state.LastError);
        Assert.Null(h.Settings.LastApplyDate);
    }
}
