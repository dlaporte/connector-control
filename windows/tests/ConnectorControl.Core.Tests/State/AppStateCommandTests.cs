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
        h.Claude.LaunchTime = h.Now.AddHours(-1);
        using var state = h.Create();
        Assert.True(state.NeedsClaudeRestart);
        h.Claude.RestartResult = "Claude didn’t quit (it may be showing a dialog). Quit it manually, then click Restart Claude again.";
        await state.RestartClaudeAsync();
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

    [Fact]
    public async Task ARelaunchThatSilentlyFailedIsReportedTwentySecondsLater()
    {
        using var h = new AppStateHarness();
        h.Settings.ConfirmBeforeRestart = false;
        h.Settings.LastApplyDate = h.Now;
        h.Claude.IsRunning = true;
        h.Claude.LaunchTime = h.Now.AddHours(-1);
        using var state = h.Create();
        // RestartAsync reports success: explorer.exe accepted the AUMID. Claude never appeared.
        h.Claude.OnRestart = () =>
        {
            h.Claude.IsRunning = false;
            h.Claude.LaunchTime = null;
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
        h.Claude.LaunchTime = h.Now.AddHours(-1);
        using var state = h.Create();
        state.LastError = "old banner";
        h.Claude.OnRestart = () => h.Claude.LaunchTime = h.Now.AddSeconds(1);
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
        h.Claude.LaunchTime = h.Now.AddHours(-1);
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
        h.Claude.LaunchTime = h.Now.AddHours(-1);
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
        h.Claude.LaunchTime = h.Now.AddHours(-1);
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
    }

    [Fact]
    public void SwitchProfileAppliesImmediately()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        h.Dialogs.NextPromptAnswer = "Work";
        state.NewProfile();
        Assert.Equal("Work", state.ActiveProfile);
        state.SetEnabled("aws-mcp", false);
        Assert.Equal(["scoutbook", "service-now"], AppStateHarness.Keys(h.ClaudeServers().Keys));

        h.Settings.LastApplyDate = null;
        state.SwitchProfile("Default");
        Assert.Equal("Default", state.ActiveProfile);
        Assert.Equal(["aws-mcp", "scoutbook", "service-now"], AppStateHarness.Keys(h.ClaudeServers().Keys));
        Assert.Equal(h.Now, h.Settings.LastApplyDate);
        Assert.Equal("Default", h.StoreOnDisk().ActiveProfile);
    }

    [Fact]
    public void SwitchProfileIgnoresAnUnknownName()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.SwitchProfile("Nope");
        Assert.Equal("Default", state.ActiveProfile);
        Assert.Null(state.LastError);
        Assert.Null(h.Settings.LastApplyDate);
    }

    [Fact]
    public void NewProfilePromptTextAndCancel()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        h.Dialogs.NextPromptAnswer = null;
        state.NewProfile();
        Assert.Equal(new FakeDialogs.PromptCall("New Profile", ""), h.Dialogs.Prompts[0]);
        Assert.Equal(["Default"], state.ProfileNames);

        h.Dialogs.NextPromptAnswer = "Work";
        state.NewProfile();
        Assert.Equal(["Default", "Work"], state.ProfileNames);
        Assert.Equal(["aws-mcp", "scoutbook", "service-now"], state.SortedNames);   // a COPY of the active profile
        Assert.Equal(h.Now, h.Settings.LastApplyDate);
    }

    [Fact]
    public void NewProfileErrorsGoToLastError()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        h.Dialogs.NextPromptAnswer = "Default";
        state.NewProfile();
        Assert.Equal("A profile named “Default” already exists.", state.LastError);
        h.Dialogs.NextPromptAnswer = "   ";
        state.NewProfile();
        Assert.Equal("Name must not be empty.", state.LastError);
        Assert.Equal(["Default"], state.ProfileNames);
    }

    [Fact]
    public void RenameProfilePrefillsTheActiveName()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        h.Dialogs.NextPromptAnswer = "Main";
        state.RenameProfile();
        Assert.Equal(new FakeDialogs.PromptCall("Rename Profile", "Default"), h.Dialogs.Prompts[0]);
        Assert.Equal("Main", state.ActiveProfile);
        Assert.Equal("Main", h.StoreOnDisk().ActiveProfile);
        Assert.Equal(h.Now, h.Settings.LastApplyDate);
    }

    [Fact]
    public void DeleteProfileConfirmTextAndLastProfileError()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.DeleteProfile();
        Assert.Equal(new FakeDialogs.ConfirmCall("Delete Profile “Default”?", "Its connector list is removed; backups keep prior states.", "Delete", "Cancel", true), h.Dialogs.Confirms[0]);
        Assert.Equal("Can’t delete the last profile.", state.LastError);
        Assert.Equal(["Default"], state.ProfileNames);
    }

    [Fact]
    public void DeleteProfileSwitchesToTheAlphabeticallyFirstRemaining()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        h.Dialogs.NextPromptAnswer = "Zeta";
        state.NewProfile();
        h.Dialogs.NextPromptAnswer = "Work";
        state.NewProfile();
        Assert.Equal("Work", state.ActiveProfile);
        h.Dialogs.NextConfirm = false;
        state.DeleteProfile();
        Assert.Equal(["Default", "Work", "Zeta"], state.ProfileNames);   // cancelled
        h.Dialogs.NextConfirm = true;
        state.DeleteProfile();
        Assert.Equal(["Default", "Zeta"], state.ProfileNames);
        Assert.Equal("Default", state.ActiveProfile);
        Assert.Equal("Default", h.StoreOnDisk().ActiveProfile);
    }
}
