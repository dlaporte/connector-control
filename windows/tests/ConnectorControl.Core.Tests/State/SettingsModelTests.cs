using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/SettingsModelTests.swift</summary>
public class SettingsModelTests
{
    private sealed class Rig : IDisposable
    {
        public AppStateHarness H { get; } = new();
        public AppState State { get; }
        public FakeAutostart Autostart { get; } = new();
        public FakeClaudeInstall Install { get; } = new();
        public FakeUpdater Updater { get; } = new();
        public UpdateCoordinator Updates { get; }
        public SettingsModel Model { get; }

        public Rig(Action<AppStateHarness>? beforeCreate = null, ICommandLineTool? tool = null)
        {
            beforeCreate?.Invoke(H);
            State = H.Create();
            Updates = new UpdateCoordinator(Updater, H.Settings, H.Notifier, H.Dialogs, AppHost.Inline());
            Model = new SettingsModel(State, H.Settings, Autostart, Install, Updater, Updates, tool);
        }

        public void Dispose()
        {
            Model.Dispose();
            Updates.Dispose();
            State.Dispose();
            H.Dispose();
        }
    }

    [Fact]
    public void LaunchAtStartupTogglesAutostart()
    {
        using var rig = new Rig();
        Assert.False(rig.Model.LaunchAtStartup);
        rig.Model.LaunchAtStartup = true;
        Assert.True(rig.Autostart.Enabled);
        Assert.Null(rig.Model.LoginItemNote);
        rig.Model.LaunchAtStartup = false;
        Assert.False(rig.Autostart.Enabled);
        Assert.Equal(2, rig.Autostart.SetCalls);
    }

    [Fact]
    public void LaunchAtStartupDoesNothingWhenTheOsAlreadyAgrees()
    {
        using var rig = new Rig();
        rig.Autostart.Enabled = true;   // enabled behind our back (Windows Settings)
        rig.Model.Refresh();
        Assert.True(rig.Model.LaunchAtStartup);
        rig.Model.LaunchAtStartup = true;
        Assert.Equal(0, rig.Autostart.SetCalls);
    }

    [Fact]
    public void LaunchAtStartupFailureRevertsAndNotes()
    {
        using var rig = new Rig();
        Assert.False(rig.Model.HasLoginItemNote);
        rig.Autostart.FailWith = "Access is denied.";
        var raised = new List<string?>();
        rig.Model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        rig.Model.LaunchAtStartup = true;
        Assert.False(rig.Model.LaunchAtStartup);
        Assert.Equal("Couldn't update startup entry: Access is denied.", rig.Model.LoginItemNote);
        Assert.True(rig.Model.HasLoginItemNote);
        Assert.True(rig.Model.LoginItemNoteIsWarning);   // a failure is amber
        Assert.Contains(nameof(SettingsModel.HasLoginItemNote), raised);
        Assert.Contains(nameof(SettingsModel.LoginItemNoteIsWarning), raised);
        rig.Autostart.FailWith = null;
        rig.Model.LaunchAtStartup = true;
        Assert.Null(rig.Model.LoginItemNote);
        Assert.False(rig.Model.HasLoginItemNote);
        Assert.False(rig.Model.LoginItemNoteIsWarning);
    }

    [Fact]
    public void ConfirmAndNotifyTogglesPersistToSettings()
    {
        using var rig = new Rig();
        rig.Model.ConfirmBeforeRestart = false;
        rig.Model.ConfirmBeforeQuit = false;
        rig.Model.NotifyExternalChanges = false;
        rig.Model.AutoUpdate = false;
        Assert.False(rig.H.Settings.ConfirmBeforeRestart);
        Assert.False(rig.H.Settings.ConfirmBeforeQuit);
        Assert.False(rig.H.Settings.NotifyExternalChanges);
        Assert.False(rig.H.Settings.AutoUpdate);
    }

    [Fact]
    public void UpdatesAreDisabledForADevelopmentBuild()
    {
        using var rig = new Rig();
        rig.Updater.IsAvailable = false;
        rig.Updater.VersionDisplay = "development build";
        rig.Model.Refresh();
        Assert.False(rig.Model.UpdatesEnabled);
        Assert.Equal("Version development build", rig.Model.VersionText);
        Assert.False(rig.Model.IsPreviewBuild);
        Assert.Null(rig.Model.UpdatesCaption);
        Assert.False(rig.Model.HasUpdatesCaption);
    }

    /// <summary>Only a preview says where its updates come from; a release and a development build say nothing.</summary>
    [Fact]
    public void OnlyAPreviewSaysWhereItsUpdatesComeFrom()
    {
        using var rig = new Rig();
        rig.Updater.VersionDisplay = "1.5.0-preview.10+1a2b3c4";
        Assert.True(rig.Model.IsPreviewBuild);
        Assert.Equal(SettingsModel.PreviewUpdatesCaption, rig.Model.UpdatesCaption);
        Assert.True(rig.Model.HasUpdatesCaption);
        rig.Updater.VersionDisplay = "1.5.0+1a2b3c4";
        Assert.False(rig.Model.IsPreviewBuild);
        Assert.Null(rig.Model.UpdatesCaption);
        rig.Updater.VersionDisplay = "1.5.0";
        Assert.False(rig.Model.IsPreviewBuild);
    }

    [Fact]
    public async Task CheckForUpdatesRunsAnInteractiveCheck()
    {
        using var rig = new Rig();
        rig.Updater.VersionDisplay = "1.3.0";
        Assert.Equal("Version 1.3.0", rig.Model.VersionText);
        await rig.Model.CheckForUpdatesAsync();
        Assert.Equal(1, rig.Updater.Checks);
        Assert.Equal("You're up to date.", rig.H.Dialogs.Informs[0].Message);
    }

    /// <summary>While a check runs, Check for Updates reads Checking… and cannot be clicked, and says so as it changes.
    /// Windows only: Sparkle shows the Mac's progress.</summary>
    [Fact]
    public async Task CheckForUpdatesReadsCheckingAndIsDisabledWhileACheckRuns()
    {
        using var rig = new Rig();
        rig.Updater.CheckGate = new TaskCompletionSource<bool>();
        var raised = new List<string?>();
        rig.Model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.True(rig.Model.CanCheckForUpdates);
        Assert.Equal(SettingsModel.CheckForUpdatesTitle, rig.Model.CheckForUpdatesButtonTitle);

        var check = rig.Model.CheckForUpdatesAsync();
        Assert.True(rig.Model.IsCheckingForUpdates);
        Assert.False(rig.Model.CanCheckForUpdates);
        Assert.Equal("Checking…", rig.Model.CheckForUpdatesButtonTitle);
        Assert.Contains(nameof(SettingsModel.CanCheckForUpdates), raised);
        Assert.Contains(nameof(SettingsModel.CheckForUpdatesButtonTitle), raised);

        raised.Clear();
        rig.Updater.CheckGate.SetResult(true);
        await check.WaitAsync(Wait.Eventually, TestContext.Current.CancellationToken);
        Assert.False(rig.Model.IsCheckingForUpdates);
        Assert.True(rig.Model.CanCheckForUpdates);
        Assert.Equal(SettingsModel.CheckForUpdatesTitle, rig.Model.CheckForUpdatesButtonTitle);
        Assert.Contains(nameof(SettingsModel.CanCheckForUpdates), raised);
    }

    [Fact]
    public void CheckForUpdatesStaysDisabledWhereThereIsNoUpdater()
    {
        using var rig = new Rig();
        rig.Updater.IsAvailable = false;
        Assert.False(rig.Model.CanCheckForUpdates);
    }

    /// <summary>The −/+ buttons dim at the bounds and tell Narrator the count. Windows only: the Mac Stepper does both itself.</summary>
    [Fact]
    public void TheKeepCountButtonsDimAtTheBoundsAndSayTheCount()
    {
        using var rig = new Rig();
        var raised = new List<string?>();
        rig.Model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.True(rig.Model.CanDecrementKeepCount);
        Assert.True(rig.Model.CanIncrementKeepCount);
        Assert.Equal("Keeps 20 backups of each file.", rig.Model.BackupKeepCountHelpText);
        rig.Model.BackupKeepCount = SettingsModel.MinKeepCount;
        Assert.False(rig.Model.CanDecrementKeepCount);
        Assert.True(rig.Model.CanIncrementKeepCount);
        Assert.Contains(nameof(SettingsModel.CanDecrementKeepCount), raised);
        Assert.Contains(nameof(SettingsModel.BackupKeepCountHelpText), raised);
        raised.Clear();
        rig.Model.DecrementKeepCount();   // a click at the bound changes nothing, and still says so
        Assert.Equal(SettingsModel.MinKeepCount, rig.Model.BackupKeepCount);
        Assert.Contains(nameof(SettingsModel.CanDecrementKeepCount), raised);
        rig.Model.BackupKeepCount = SettingsModel.MaxKeepCount;
        Assert.True(rig.Model.CanDecrementKeepCount);
        Assert.False(rig.Model.CanIncrementKeepCount);
        Assert.Equal("Keeps 100 backups of each file.", rig.Model.BackupKeepCountHelpText);
    }

    [Fact]
    public void BackupKeepCountClampsAndRebuildsTheService()
    {
        using var rig = new Rig();
        Assert.Equal(20, rig.Model.BackupKeepCount);
        rig.Model.BackupKeepCount = 500;
        Assert.Equal(100, rig.Model.BackupKeepCount);
        Assert.Equal(100, rig.H.Settings.BackupKeepCount);
        Assert.Equal(100, rig.State.Service.Backups.KeepCount);
        rig.Model.BackupKeepCount = 1;
        Assert.Equal(5, rig.Model.BackupKeepCount);
        rig.Model.DecrementKeepCount();
        Assert.Equal(5, rig.Model.BackupKeepCount);
        rig.Model.IncrementKeepCount();
        Assert.Equal(6, rig.Model.BackupKeepCount);
    }

    [Fact]
    public void StoreLocationFollowsRepointing()
    {
        using var rig = new Rig();
        Assert.Equal(rig.H.StoreDir, rig.Model.StoreDirPath);
        Assert.Equal("Connector Control", rig.Model.StoreDirName);
        Assert.Equal(SettingsModel.ShownParent(rig.H.StoreDir), rig.Model.StoreDirParent);
        Assert.Equal("backups", rig.Model.BackupsDirName);
        Assert.Equal(SettingsModel.ShownParent(rig.H.BackupsDir), rig.Model.BackupsDirParent);
        Assert.False(rig.Model.CanUseDefaultStore);
        var synced = rig.H.Dir.File("synced");
        rig.Model.ChooseStoreDir(synced);
        Assert.Equal(synced, rig.Model.StoreDirPath);
        Assert.Equal("synced", rig.Model.StoreDirName);
        Assert.True(rig.Model.CanUseDefaultStore);
        Assert.Equal(rig.H.BackupsDir, rig.Model.BackupsDir);
        rig.Model.UseDefaultStoreDir();
        Assert.Equal(rig.H.StoreDir, rig.Model.StoreDirPath);
        Assert.False(rig.Model.CanUseDefaultStore);
    }

    /// <summary>A path row shows the name, and under it the folder it is in; Windows shows the folder in full.</summary>
    [Fact]
    public void APathRowShowsTheNameAndTheFolderItIsIn()
    {
        Assert.Equal("Connector Control", SettingsModel.ShownName(@"C:\Users\me\AppData\Roaming\Connector Control"));
        Assert.Equal(@"C:\Users\me\AppData\Roaming", SettingsModel.ShownParent(@"C:\Users\me\AppData\Roaming\Connector Control"));
        Assert.Equal("bin", SettingsModel.ShownName(@"C:\Users\me\bin\"));
        Assert.Equal(@"C:\", SettingsModel.ShownParent(@"C:\Users"));
        Assert.Equal("/usr/local/bin", SettingsModel.ShownParent("/usr/local/bin/connector-control"));
        Assert.Equal("/", SettingsModel.ShownParent("/Applications"));
    }

    [Theory]
    [InlineData(ClaudeInstallKind.Msix, "MSIX package")]
    [InlineData(ClaudeInstallKind.Legacy, "Legacy installer")]
    [InlineData(ClaudeInstallKind.NotFound, "Not found")]
    public void InstallKindText(ClaudeInstallKind kind, string expected)
    {
        using var rig = new Rig();
        rig.Install.Info = new ClaudeInstallInfo(kind, null, kind == ClaudeInstallKind.NotFound ? null : "target", "claude");
        rig.Model.Refresh();   // the install is cached; a window re-open is what re-reads it
        Assert.Equal(expected, rig.Model.InstallKindText);
    }

    /// <summary>Windows only: the Mac has no install to detect. Detecting walks every package registered for
    /// the user, too slow to run at each launch for a model the Claude tab may never show — the control layer
    /// builds one at launch and never reads the install — so it runs when the install is first read, and again
    /// only after a refresh.</summary>
    [Fact]
    public void TheInstallIsDetectedOnlyWhenItIsRead()
    {
        using var rig = new Rig();
        rig.Model.Refresh();
        Assert.Equal(0, rig.Install.DetectCalls);
        _ = rig.Model.InstallKindText;
        _ = rig.Model.LaunchTargetText;
        Assert.Equal(1, rig.Install.DetectCalls);
        rig.Model.Refresh();
        _ = rig.Model.LaunchTargetText;
        Assert.Equal(2, rig.Install.DetectCalls);
    }

    [Fact]
    public void LaunchTargetShowsTheOverrideOrTheDetectedTarget()
    {
        using var rig = new Rig();
        Assert.Equal("Claude_pzs8sxrjxfjjc!Claude", rig.Model.LaunchTargetText);
        Assert.False(rig.Model.CanUseDefaultLaunchTarget);
        rig.Model.ChooseLaunchTarget(@"C:\Tools\claude.exe");
        Assert.Equal(@"C:\Tools\claude.exe", rig.Model.LaunchTargetText);
        Assert.Equal(@"C:\Tools\claude.exe", rig.H.Settings.ClaudeLaunchTarget);
        Assert.True(rig.Model.CanUseDefaultLaunchTarget);
        rig.Model.UseDefaultLaunchTarget();
        Assert.Null(rig.H.Settings.ClaudeLaunchTarget);
        rig.Install.Info = ClaudeInstallInfo.NotFound;
        rig.Model.Refresh();   // the install is cached; a window re-open is what re-reads it
        Assert.Equal("Not found", rig.Model.LaunchTargetText);
    }

    [Fact]
    public void ClaudeConfigPathChooseAndUseDefaultRepointAppState()
    {
        using var rig = new Rig();
        Assert.Equal(rig.H.ClaudeConfigPath, rig.Model.ClaudeConfigPath);
        Assert.False(rig.Model.CanUseDefaultClaudeConfig);
        var other = rig.H.Dir.File(Path.Combine("other", "claude_desktop_config.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        rig.Model.ChooseClaudeConfig(other);
        Assert.Equal(other, rig.Model.ClaudeConfigPath);
        Assert.Equal(other, rig.State.Service.Paths.ClaudeConfigPath);
        Assert.True(rig.Model.CanUseDefaultClaudeConfig);
        rig.Model.UseDefaultClaudeConfig();
        Assert.Equal(rig.H.ClaudeConfigPath, rig.Model.ClaudeConfigPath);
    }

    [Fact]
    public void ToolRowsStartAsCheckingAndFillInAfterARefresh()
    {
        using var rig = new Rig();
        rig.H.Tools.Statuses[Tool.Npx] = new ToolStatus(@"C:\Program Files\nodejs\npx.cmd", "10.9.2");
        rig.H.Tools.Statuses[Tool.Node] = new ToolStatus(@"C:\Program Files\nodejs\node.exe", null);
        rig.H.Tools.Statuses[Tool.Uvx] = ToolStatus.NotFound;
        rig.H.Tools.Statuses[Tool.Uv] = ToolStatus.NotFound;
        Assert.Equal(["npx", "node", "uvx", "uv"], rig.Model.ToolRows.Select(r => r.Name).ToArray());
        Assert.All(rig.Model.ToolRows, r => Assert.Equal("Checking…", r.StatusText));
        Assert.All(rig.Model.ToolRows, r => Assert.False(r.IsProblem));
        Assert.All(rig.Model.ToolRows, r => Assert.Null(r.Note));
        rig.Model.RefreshTools();
        Assert.All(rig.Model.ToolRows, r => Assert.Equal("Checking…", r.StatusText));   // probed off the UI thread: nothing yet
        rig.H.Drain();
        var rows = rig.Model.ToolRows;
        Assert.Equal(["10.9.2", "Found", "Not found", "Not found"], rows.Select(r => r.StatusText).ToArray());
        Assert.Equal([false, false, true, true], rows.Select(r => r.IsProblem).ToArray());
        Assert.Null(rows[0].Note);
        Assert.Null(rows[1].Note);
        Assert.Equal("Install uv", rows[2].Note!.LinkTitle);
        Assert.Equal("winget install astral-sh.uv", rows[3].Note!.InstallCommand);
        Assert.Equal(["uv and uvx: Install uv or run winget install astral-sh.uv."], rig.Model.ToolFooterLines.Select(l => l.Text).ToArray());   // one line for the family, not one a tool
        Assert.True(rig.Model.HasToolFooterLines);
    }

    /// <summary>The footer has a line for each family with a problem, Node.js before uv as the rows run, the link and the
    /// command set apart; nothing while every tool is found or still being checked.</summary>
    [Fact]
    public void TheToolsFooterHasALineForEachFamilyWithAProblem()
    {
        Assert.Empty(ToolFooterLine.Make(new Dictionary<Tool, ToolStatus>()));
        Assert.Empty(ToolFooterLine.Make(new Dictionary<Tool, ToolStatus> { [Tool.Npx] = new ToolStatus(@"C:\Program Files\nodejs\npx.cmd", "10.9.2") }));
        var lines = ToolFooterLine.Make(new Dictionary<Tool, ToolStatus> { [Tool.Uv] = ToolStatus.NotFound, [Tool.Npx] = ToolStatus.NotFound, [Tool.Node] = ToolStatus.NotFound });
        Assert.Equal(["node and npx: Install Node.js or run winget install OpenJS.NodeJS.LTS.", "uv and uvx: Install uv or run winget install astral-sh.uv."],
            lines.Select(l => l.Text).ToArray());
        var uv = lines[^1];
        Assert.Equal("Install uv", uv.LinkTitle);
        Assert.Equal(ToolInfo.LinkUrl(ToolFamily.Uv), uv.LinkUrl);
        Assert.Equal("winget install astral-sh.uv", uv.InstallCommand);
        Assert.Equal([new TextRun("uv and uvx: "), new TextRun("Install uv", IsSetApart: true), new TextRun(" or run "),
            new TextRun("winget install astral-sh.uv", IsSetApart: true), new TextRun(".")], uv.Runs);
    }

    [Fact]
    public void ToolRowsRaiseWhenStatusesArrive()
    {
        using var rig = new Rig();
        var raised = new List<string?>();
        rig.Model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        rig.Model.RefreshTools();
        rig.H.Drain();
        Assert.Equal(4, rig.State.ToolStatuses.Count);
        Assert.Contains(nameof(SettingsModel.ToolRows), raised);
        Assert.Contains(nameof(SettingsModel.ToolFooterLines), raised);
        Assert.Contains(nameof(SettingsModel.HasToolFooterLines), raised);
        Assert.Equal(1, rig.H.Tools.Batches);
    }

    [Fact]
    public void DisposeStopsRelayingAppStateToolStatusChanges()
    {
        using var rig = new Rig();
        rig.H.Tools.Statuses[Tool.Npx] = ToolStatus.NotFound;
        rig.Model.RefreshTools();
        rig.H.Drain();
        Assert.Equal(4, rig.State.ToolStatuses.Count);

        rig.Model.Dispose();
        // Simulate a bound view: it only re-reads ToolRows when told to by a PropertyChanged event.
        var lastSeenRows = rig.Model.ToolRows;
        var beforeChange = lastSeenRows;
        var raised = new List<string?>();
        rig.Model.PropertyChanged += (_, e) =>
        {
            raised.Add(e.PropertyName);
            lastSeenRows = rig.Model.ToolRows;
        };

        // Publish a change that would flip npx's row on a live (not disposed) model.
        rig.H.Tools.Statuses[Tool.Npx] = new ToolStatus(@"C:\fake\npx.cmd", "1.0.0");
        rig.State.RefreshToolsAsync([Tool.Npx]);
        rig.H.Drain();
        Assert.True(rig.State.ToolStatuses[Tool.Npx].Found);

        Assert.Empty(raised);
        Assert.Equal(beforeChange, lastSeenRows);   // never re-read: Dispose stopped the AppState.PropertyChanged relay
    }

    [Fact]
    public void ToolRowsCarryTheNoteFromToolNote()
    {
        Assert.Equal(ToolRow.Make(Tool.Npx, ToolStatus.NotFound), new ToolRow("npx", "Not found", true, ToolNote.Make(Tool.Npx, ToolStatus.NotFound)));
    }

    // MARK: Claude integration

    [Fact]
    public void TheIntegrationSwitchAndTheConfirmSwitch()
    {
        using var rig = new Rig();
        Assert.False(rig.Model.ClaudeIntegrationEnabled);
        Assert.True(rig.Model.CanChangeClaudeIntegration);
        Assert.Null(rig.Model.ClaudeIntegrationNote);
        Assert.False(rig.Model.CanChangeConfirmClaudeChanges);   // the second switch works only while the first is on
        Assert.Equal(SettingsModel.DimmedOpacity, rig.Model.ConfirmClaudeChangesTitleOpacity);   // its title fades with it
        Assert.Null(rig.Model.ConfirmClaudeChangesCaption);      // its caption hides while the integration is off
        rig.Model.ClaudeIntegrationEnabled = true;
        Assert.True(rig.H.Settings.ClaudeIntegrationEnabled);
        Assert.True(rig.Model.CanChangeConfirmClaudeChanges);
        Assert.Equal(1.0, rig.Model.ConfirmClaudeChangesTitleOpacity);
        Assert.Equal(SettingsModel.ConfirmClaudeChangesOnCaption, rig.Model.ConfirmClaudeChangesCaption);
        Assert.False(rig.Model.ConfirmClaudeChangesCaptionIsWarning);
        rig.Model.ConfirmClaudeChanges = false;
        Assert.False(rig.H.Settings.ConfirmClaudeChanges);
        Assert.Equal(SettingsModel.ConfirmClaudeChangesOffCaption, rig.Model.ConfirmClaudeChangesCaption);
        Assert.True(rig.Model.ConfirmClaudeChangesCaptionIsWarning);
        rig.Model.ClaudeIntegrationEnabled = false;
        Assert.False(rig.Model.ConfirmClaudeChanges);   // the second switch keeps its value while dimmed
        Assert.Equal(rig.Updater.VersionDisplay, rig.Model.Version);
    }

    [Fact]
    public void AClashDimsTheSwitchAndNamesIt()
    {
        using var rig = new Rig(h => MasterStoreIO.Save(
            new MasterStore([new(ClaudeIntegration.EntryName, new McpEntry(true, JsonValue.Object(("command", JsonValue.String("x")))))]),
            h.MasterStorePath));
        Assert.False(rig.Model.CanChangeClaudeIntegration);
        Assert.Equal(ClaudeIntegration.ClashNote, rig.Model.ClaudeIntegrationNote);
    }

    /// <summary>The Claude group follows a change made anywhere else while the window is open: the command-line
    /// tool turning the integration on, the endpoint failing. Dispose stops it.</summary>
    [Fact]
    public void TheClaudeGroupFollowsChangesMadeElsewhere()
    {
        using var rig = new Rig();
        var names = new List<string>();
        rig.Model.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? "");
        int Raised() => names.Count(name => name is nameof(SettingsModel.ClaudeIntegrationEnabled) or nameof(SettingsModel.ControlEndpointProblem));
        Assert.Null(rig.State.SetClaudeIntegrationEnabled(true));   // as the command-line tool turns it on
        Assert.True(Raised() > 0);
        Assert.True(rig.Model.ClaudeIntegrationEnabled);
        Assert.Null(rig.Model.ControlEndpointProblem);
        Assert.False(rig.Model.HasControlEndpointProblem);
        var before = Raised();
        rig.State.ReportControlEndpointUnavailable();
        Assert.True(Raised() > before);
        Assert.Equal(ClaudeIntegration.EndpointUnavailableText, rig.Model.ControlEndpointProblem);
        Assert.True(rig.Model.HasControlEndpointProblem);
        rig.Model.Dispose();
        var disposed = names.Count;
        rig.State.SetClaudeIntegrationEnabled(false);
        Assert.Equal(disposed, names.Count);   // Dispose stops the relay
    }

    /// <summary>Switching into a collection whose connector holds the name, and out again: while on, the switch
    /// can still go off and names the clash in place of its caption; while off, it is dimmed until the
    /// clash ends.</summary>
    [Fact]
    public void SwitchingIntoAndOutOfAClashingCollection()
    {
        using var rig = new Rig(h =>
        {
            MasterStoreIO.Save(new MasterStore(MasterStore.CurrentVersion, "Default",
            [
                new("Default", new Collection()),
                new("Old", new Collection([new(ClaudeIntegration.EntryName, new McpEntry(true, JsonValue.Object(("command", JsonValue.String("x")))))])),
            ]), h.MasterStorePath);
            h.ControlToolPath = "/x/connector-control";
            h.Settings.ClaudeIntegrationEnabled = true;
        });
        rig.H.Drain();
        Assert.Equal(SettingsModel.ClaudeIntegrationCaption, rig.Model.ClaudeIntegrationCaptionText);
        Assert.False(rig.Model.ClaudeIntegrationCaptionIsWarning);
        Assert.Equal(1.0, rig.Model.ClaudeIntegrationTitleOpacity);
        Assert.Equal(ClaudeIntegration.Entry("/x/connector-control"), rig.H.ClaudeServers()[ClaudeIntegration.EntryName]);
        rig.State.SwitchCollection("Old");
        rig.H.Drain();
        Assert.True(rig.Model.CanChangeClaudeIntegration);   // on, it can still go off
        Assert.Equal(ClaudeIntegration.ClashNote, rig.Model.ClaudeIntegrationCaptionText);
        Assert.True(rig.Model.ClaudeIntegrationCaptionIsWarning);
        // The app's own entry gives way to the connector that holds the name.
        Assert.NotEqual(ClaudeIntegration.Entry("/x/connector-control"), rig.H.ClaudeServers()[ClaudeIntegration.EntryName]);
        rig.Model.ClaudeIntegrationEnabled = false;
        Assert.False(rig.Model.CanChangeClaudeIntegration);   // off, it is dimmed while the clash lasts
        Assert.Equal(SettingsModel.DimmedOpacity, rig.Model.ClaudeIntegrationTitleOpacity);   // its title fades; the note does not
        rig.State.SwitchCollection("Default");
        rig.H.Drain();
        Assert.True(rig.Model.CanChangeClaudeIntegration);
        Assert.Equal(SettingsModel.ClaudeIntegrationCaption, rig.Model.ClaudeIntegrationCaptionText);
    }

    /// <summary>An app run from where it will not stay cannot turn the integration on or install the tool: the
    /// switch and Install are dimmed and say why. Removing a link made earlier still works.</summary>
    [Fact]
    public void AnAppRunFromWhereItWillNotStayDimsTheSwitchAndInstall()
    {
        var tool = new FakeCommandLineTool();
        using var rig = new Rig(h =>
        {
            h.ControlToolPath = "/Volumes/Connector Control/Connector Control.app/Contents/MacOS/connector-control";
            h.ControlToolIsTransient = true;
        }, tool);
        Assert.False(rig.Model.CanChangeClaudeIntegration);
        Assert.Equal(ClaudeIntegration.MoveToApplicationsError, rig.Model.ClaudeIntegrationCaptionText);
        Assert.True(rig.Model.ClaudeIntegrationCaptionIsWarning);
        Assert.False(rig.Model.CanUseCommandLineToolButton);
        Assert.Equal(ClaudeIntegration.MoveToApplicationsError, rig.Model.CommandLineToolMessage);
        Assert.True(rig.Model.HasCommandLineToolMessage);
        Assert.Empty(rig.Model.CommandLineToolCaption);   // no path for scripts that is about to be gone
        Assert.False(rig.Model.HasCommandLineToolCaption);   // the row leaves the empty line out
        rig.Model.CommandLineToolAction();
        Assert.Equal(0, tool.Installs);
        tool.IsInstalled = true;
        Assert.True(rig.Model.CanUseCommandLineToolButton);   // a link made earlier can still go
        Assert.Null(rig.Model.CommandLineToolMessage);
        Assert.False(rig.Model.HasCommandLineToolMessage);
        Assert.Equal(TextRun.Runs(CommandLineToolText.RunHelpCaption, [CommandLineToolText.HelpCommand]), rig.Model.CommandLineToolCaption);
        Assert.True(rig.Model.HasCommandLineToolCaption);
    }

    // MARK: Command-line tool

    [Fact]
    public void TheCommandLineToolRowInstallsAndRemoves()
    {
        var tool = new FakeCommandLineTool();
        using var rig = new Rig(tool: tool);
        Assert.True(rig.Model.HasCommandLineTool);
        Assert.Equal([new TextRun(CommandLineToolText.NotInstalledText)], rig.Model.CommandLineToolStatus);
        Assert.Equal([new TextRun("Scripts can always use "), new TextRun(tool.ToolPath, IsSetApart: true)], rig.Model.CommandLineToolCaption);
        Assert.True(rig.Model.HasCommandLineToolCaption);
        Assert.Equal(CommandLineToolText.InstallButton, rig.Model.CommandLineToolButton);
        Assert.Null(rig.Model.CommandLineToolMessage);

        rig.Model.CommandLineToolAction();
        Assert.Equal(1, tool.Installs);
        Assert.True(rig.Model.CommandLineToolBusy);
        Assert.False(rig.Model.CanUseCommandLineToolButton);   // one at a time, while the PATH's change is announced
        rig.Model.CommandLineToolAction();
        Assert.Equal(1, tool.Installs);
        var reopened = new SettingsModel(rig.State, rig.H.Settings, rig.Autostart, rig.Install, rig.Updater, rig.Updates, tool);
        Assert.False(reopened.CanUseCommandLineToolButton);   // a Settings window opened meanwhile cannot start a second one
        tool.Finish(installed: true);
        Assert.False(rig.Model.CommandLineToolBusy);
        Assert.True(reopened.CanUseCommandLineToolButton);
        reopened.Dispose();
        Assert.Equal([new TextRun(CommandLineToolText.InPathText)], rig.Model.CommandLineToolStatus);
        Assert.Equal([new TextRun("Run "), new TextRun(CommandLineToolText.HelpCommand, IsSetApart: true), new TextRun(" in a new terminal.")],
            rig.Model.CommandLineToolCaption);
        Assert.Equal(CommandLineToolText.RemoveButton, rig.Model.CommandLineToolButton);
        tool.InstalledLocation = "/usr/local/bin/connector-control";
        // In the link's folder, as the Mac puts it there.
        Assert.Equal([new TextRun("Installed in "), new TextRun("/usr/local/bin", IsSetApart: true)], rig.Model.CommandLineToolStatus);

        rig.Model.CommandLineToolAction();
        Assert.Equal(1, tool.Removals);
        tool.Finish(installed: true, CommandLineToolText.RemoveFailedError("Access denied."));
        Assert.Equal(CommandLineToolText.RemoveFailedError("Access denied."), rig.Model.CommandLineToolMessage);
        Assert.True(rig.Model.HasCommandLineToolMessage);
        Assert.Equal(CommandLineToolText.RemoveButton, rig.Model.CommandLineToolButton);
        rig.Model.CommandLineToolAction();
        Assert.Null(rig.Model.CommandLineToolMessage);   // a new try clears the last failure
        tool.Finish(installed: false);
        Assert.Equal(CommandLineToolText.InstallButton, rig.Model.CommandLineToolButton);
    }

    /// <summary>The row repaints as an operation starts and as it ends: the seam's own announcements, relayed.
    /// WPF bindings re-read only the members raised, so these are the row's members by name.</summary>
    [Fact]
    public void TheCommandLineToolRowRaisesAsAnOperationStartsAndEnds()
    {
        var tool = new FakeCommandLineTool();
        using var rig = new Rig(tool: tool);
        var names = new List<string>();
        rig.Model.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? "");
        rig.Model.CommandLineToolAction();
        Assert.Contains(nameof(SettingsModel.CommandLineToolBusy), names);
        Assert.Contains(nameof(SettingsModel.CanUseCommandLineToolButton), names);
        names.Clear();
        tool.Finish(installed: false, CommandLineToolText.InstallFailedError("Access denied."));
        Assert.Contains(nameof(SettingsModel.CommandLineToolMessage), names);
        Assert.Contains(nameof(SettingsModel.HasCommandLineToolMessage), names);
        Assert.Contains(nameof(SettingsModel.CommandLineToolBusy), names);
        // The caption and whether it is drawn at all, raised together.
        Assert.Contains(nameof(SettingsModel.CommandLineToolCaption), names);
        Assert.Contains(nameof(SettingsModel.HasCommandLineToolCaption), names);
    }

    /// <summary>A Settings window closed and opened again while Install or Remove runs hears its end, not only the
    /// window that started it, and shows its failure. Dispose stops it hearing.</summary>
    [Fact]
    public void AWindowOpenedDuringAnOperationHearsItsEnd()
    {
        var tool = new FakeCommandLineTool();
        using var rig = new Rig(tool: tool);
        rig.Model.CommandLineToolAction();
        rig.Model.Dispose();   // the window that started it closes
        using var reopened = new SettingsModel(rig.State, rig.H.Settings, rig.Autostart, rig.Install, rig.Updater, rig.Updates, tool);
        var names = new List<string>();
        reopened.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? "");
        Assert.False(reopened.CanUseCommandLineToolButton);
        tool.Finish(installed: false, CommandLineToolText.InstallFailedError("Access denied."));
        Assert.Contains(nameof(SettingsModel.CanUseCommandLineToolButton), names);
        Assert.Contains(nameof(SettingsModel.CommandLineToolMessage), names);
        Assert.True(reopened.CanUseCommandLineToolButton);
        Assert.Equal(CommandLineToolText.InstallFailedError("Access denied."), reopened.CommandLineToolMessage);
        reopened.Dispose();
        var disposed = names.Count;
        tool.Install(_ => { });
        tool.Finish(installed: true);
        Assert.Equal(disposed, names.Count);   // Dispose stops it hearing the seam
    }

    /// <summary>A cancelled prompt says nothing, even after a failure: the next try clears the failure, and the
    /// cancel puts nothing in its place.</summary>
    [Fact]
    public void ACancelledPromptSaysNothing()
    {
        var tool = new FakeCommandLineTool();
        using var rig = new Rig(tool: tool);
        rig.Model.CommandLineToolAction();
        tool.Finish(installed: false, CommandLineToolText.InstallFailedError("Access denied."));
        Assert.Equal(CommandLineToolText.InstallFailedError("Access denied."), rig.Model.CommandLineToolMessage);
        rig.Model.CommandLineToolAction();
        tool.Finish(installed: false);
        Assert.Null(rig.Model.CommandLineToolMessage);
        Assert.False(rig.Model.HasCommandLineToolMessage);
        Assert.Equal(CommandLineToolText.InstallButton, rig.Model.CommandLineToolButton);
        Assert.True(rig.Model.CanUseCommandLineToolButton);
    }

    /// <summary>Windows only: WPF bindings listen by name, so a change that raises only the store, such as a synced
    /// edit bringing a connector under the reserved name, must still re-raise the Claude group. The Mac relays every
    /// change AppState announces.</summary>
    [Fact]
    public void ARaiseOfTheStoreAloneReRaisesTheClaudeGroup()
    {
        using var rig = new Rig();
        var names = new List<string>();
        rig.Model.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? "");
        // A reload raises everything at its end. Subscribed after the model, this listener hears each raise once the
        // model has answered it, so what the model had raised when the store's own raise arrived is the store's doing.
        List<string>? raisedByTheStore = null;
        var everythingFirst = false;
        rig.State.PropertyChanged += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.PropertyName))
            {
                everythingFirst |= raisedByTheStore is null;
            }
            else if (e.PropertyName == nameof(AppState.Store))
            {
                raisedByTheStore ??= [.. names];
            }
        };
        Assert.True(rig.Model.CanChangeClaudeIntegration);
        // Disabled, so Claude's config is left as it is.
        var store = rig.H.StoreOnDisk();
        store.Mcps[ClaudeIntegration.EntryName] = new McpEntry(false, JsonValue.Object(("command", JsonValue.String("x"))));
        MasterStoreIO.Save(store, rig.H.MasterStorePath);
        rig.State.Reload();
        Assert.NotNull(raisedByTheStore);
        Assert.False(everythingFirst);   // nothing raised everything before the store did
        Assert.Contains(nameof(SettingsModel.CanChangeClaudeIntegration), raisedByTheStore);
        Assert.Contains(nameof(SettingsModel.ClaudeIntegrationNote), raisedByTheStore);
        Assert.Contains(nameof(SettingsModel.ClaudeIntegrationCaptionText), raisedByTheStore);
        Assert.Contains(nameof(SettingsModel.ClaudeIntegrationTitleOpacity), raisedByTheStore);
        Assert.False(rig.Model.CanChangeClaudeIntegration);
        Assert.Equal(ClaudeIntegration.ClashNote, rig.Model.ClaudeIntegrationCaptionText);
    }

    [Fact]
    public void WithNoToolThereIsNoRow()
    {
        using var rig = new Rig();
        Assert.False(rig.Model.HasCommandLineTool);
        Assert.Empty(rig.Model.CommandLineToolStatus);
        Assert.False(rig.Model.CanUseCommandLineToolButton);
    }
}
