using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Threading;
using ConnectorControl.App.Services;
using ConnectorControl.App.Tests.TestSupport;
using ConnectorControl.App.Views;
using ConnectorControl.Core;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.App.Tests;

public class SettingsWindowTests
{
    [Fact]
    public void SettingsWindowLoadsAllThreeTabs()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var window = new SettingsWindow(state, services, updates);
            try
            {
                WindowTestSupport.Layout(window, new Size(480, 500));
                Assert.Equal(3, window.Tabs.Items.Count);
                var headers = window.Tabs.Items.Cast<TabItem>().Select(t => ((TextBlock)((StackPanel)t.Header).Children[1]).Text).ToArray();
                Assert.Equal(["General", "Storage", "Claude"], headers);
                // Each tab draws its glyph before its name, as the Mac's draw gearshape, externaldrive and Claude's icon; the
                // Claude tab's generic glyph stays until its own icon is read, and wherever it cannot be.
                var glyphs = window.Tabs.Items.Cast<TabItem>().Select(t => ((TextBlock)((StackPanel)t.Header).Children[0]).Text).ToArray();
                Assert.Equal(["\uE713", "\uEDA2", "\uECAA"], glyphs);
                Assert.Equal("Version 1.2.2", window.Model.VersionText);
                Assert.Equal(Visibility.Collapsed, window.UpdatesCaption.Visibility);   // a release says nothing about previews
                Assert.Equal(h.StoreDir, window.Model.StoreDirPath);
                Assert.Equal("MSIX package", window.Model.InstallKindText);
            }
            finally
            {
                // WPF lists a window from the moment it is built, shown or not; closed, it leaves the list, so a
                // later test that looks for the Settings window it opened finds only that one.
                window.Close();
            }
        });
    }

    private static string Text(TextBlock block) => string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text));

    // A caption's own colour, and the amber a warning turns it.
    private const string Secondary = "TextFillColorSecondaryBrush";
    private const string Caution = "SystemFillColorCautionBrush";

    /// <summary><paramref name="block"/> draws in the theme brush <paramref name="key"/>, looked up from the block itself,
    /// as its DynamicResource is.</summary>
    private static void AssertBrush(string key, TextBlock block)
    {
        var brush = block.TryFindResource(key);
        Assert.NotNull(brush);
        Assert.Same(brush, block.Foreground);
    }

    /// <summary>Lays <paramref name="window"/> out at its own size, runs <paramref name="body"/>, and closes the window
    /// however the body ends: WPF lists a window from the moment it is built, shown or not, until it is closed.</summary>
    private static void Laid(SettingsWindow window, Action body)
    {
        try
        {
            WindowTestSupport.Layout(window, new Size(480, 560));
            body();
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void TheClaudeTabHasTheIntegrationAndCommandLineToolGroups()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var services = h.Services();
        var tool = new FakeCommandLineTool();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var window = new SettingsWindow(state, services, updates, tool);
            Laid(window, () =>
            {
                Assert.Equal(SettingsModel.ClaudeIntegrationCaption, window.IntegrationCaption.Text);
                AssertBrush(Secondary, window.IntegrationCaption);
                Assert.Equal(SettingsModel.ClaudeIntegrationCaption, AutomationProperties.GetHelpText(window.IntegrationSwitch));
                Assert.True(window.IntegrationSwitch.IsEnabled);
                Assert.Equal(1.0, window.IntegrationTitle.Opacity);
                Assert.False(window.ConfirmChangesSwitch.IsEnabled);
                Assert.Equal(SettingsModel.DimmedOpacity, window.ConfirmChangesTitle.Opacity);
                Assert.Equal(Visibility.Collapsed, window.ConfirmChangesCaption.Visibility);
                Assert.Equal(Visibility.Collapsed, window.EndpointProblem.Visibility);
                Assert.Equal(Visibility.Visible, window.CommandLineToolGroup.Visibility);
                Assert.Equal(CommandLineToolText.NotInstalledText, Text(window.CommandLineToolStatus));
                Assert.Equal("Scripts can always use " + new TextRun(tool.ToolPath, IsSetApart: true).ShownText, Text(window.CommandLineToolCaption));
                Assert.Equal(Visibility.Visible, window.CommandLineToolCaption.Visibility);
                Assert.Equal(CommandLineToolText.InstallButton, window.CommandLineToolButton.Content);

                window.IntegrationSwitch.IsChecked = true;
                Assert.True(h.Settings.ClaudeIntegrationEnabled);
                Assert.True(window.ConfirmChangesSwitch.IsEnabled);
                Assert.Equal(1.0, window.ConfirmChangesTitle.Opacity);
                Assert.Equal(SettingsModel.ConfirmClaudeChangesOnCaption, window.ConfirmChangesCaption.Text);
                AssertBrush(Secondary, window.ConfirmChangesCaption);
                Assert.Equal(SettingsModel.ConfirmClaudeChangesOnCaption, AutomationProperties.GetHelpText(window.ConfirmChangesSwitch));
                window.ConfirmChangesSwitch.IsChecked = false;
                Assert.Equal(SettingsModel.ConfirmClaudeChangesOffCaption, window.ConfirmChangesCaption.Text);
                AssertBrush(Caution, window.ConfirmChangesCaption);
                Assert.Equal(SettingsModel.ConfirmClaudeChangesOffCaption, AutomationProperties.GetHelpText(window.ConfirmChangesSwitch));

                window.CommandLineToolButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, tool.Installs);
                Assert.False(window.CommandLineToolButton.IsEnabled);
                tool.Finish(installed: false, problem: CommandLineToolText.InstallFailedError("Access is denied."));
                Assert.Equal(CommandLineToolText.InstallFailedError("Access is denied."), window.CommandLineToolMessage.Text);
                Assert.Equal(Visibility.Visible, window.CommandLineToolMessage.Visibility);
                Assert.True(window.CommandLineToolButton.IsEnabled);

                // Installed: in the PATH, with the command to try, and Remove.
                window.CommandLineToolButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                tool.Finish(installed: true);
                Assert.Equal(CommandLineToolText.InPathText, Text(window.CommandLineToolStatus));
                Assert.Equal(string.Concat(TextRun.Runs(CommandLineToolText.RunHelpCaption, [CommandLineToolText.HelpCommand]).Select(run => run.ShownText)),
                    Text(window.CommandLineToolCaption));
                Assert.Equal(CommandLineToolText.RemoveButton, window.CommandLineToolButton.Content);
                Assert.Equal(Visibility.Collapsed, window.CommandLineToolMessage.Visibility);

                state.ReportControlEndpointUnavailable();
                Assert.Equal(Visibility.Visible, window.EndpointProblem.Visibility);
                Assert.Equal(ClaudeIntegration.EndpointUnavailableText, window.EndpointProblem.Text);
            });
        });
    }

    /// <summary>A clash dims the switch's row as the mockup does: the switch and its title fade; the note that
    /// says why keeps full opacity, in amber. Renamed away, the row comes back.</summary>
    [Fact]
    public void AClashDimsTheSwitchRowButNotItsNote()
    {
        using var h = new AppStateHarness();
        MasterStoreIO.Save(new MasterStore([new(ClaudeIntegration.EntryName, new McpEntry(true, JsonValue.Object(("command", JsonValue.String("x")))))]),
            h.MasterStorePath);
        using var state = h.Create();
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var window = new SettingsWindow(state, services, updates);
            Laid(window, () =>
            {
                Assert.False(window.IntegrationSwitch.IsEnabled);
                Assert.Equal(SettingsModel.DimmedOpacity, window.IntegrationTitle.Opacity);
                Assert.Equal(ClaudeIntegration.ClashNote, window.IntegrationCaption.Text);
                AssertBrush(Caution, window.IntegrationCaption);
                Assert.Equal(1.0, window.IntegrationCaption.Opacity);
                Assert.Equal(ClaudeIntegration.ClashNote, AutomationProperties.GetHelpText(window.IntegrationSwitch));

                Assert.Null(state.Upsert("mine", state.Store.Mcps[ClaudeIntegration.EntryName], ClaudeIntegration.EntryName));
                Assert.True(window.IntegrationSwitch.IsEnabled);
                Assert.Equal(1.0, window.IntegrationTitle.Opacity);
                Assert.Equal(SettingsModel.ClaudeIntegrationCaption, window.IntegrationCaption.Text);
                AssertBrush(Secondary, window.IntegrationCaption);
            });
        });
    }

    /// <summary>An app run from where it will not stay: the switch's row dims, with the note that says why, and the
    /// command-line row leaves out the path for scripts, which is about to be gone, and says why Install cannot
    /// work. A link made earlier still shows, with its caption, and can go.</summary>
    [Fact]
    public void AnAppRunFromWhereItWillNotStayLeavesTheCaptionOut()
    {
        using var h = new AppStateHarness();
        h.ControlToolPath = "/Volumes/Connector Control/Connector Control.app/Contents/MacOS/connector-control";
        h.ControlToolIsTransient = true;
        using var state = h.Create();
        var services = h.Services();
        var tool = new FakeCommandLineTool();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var window = new SettingsWindow(state, services, updates, tool);
            Laid(window, () =>
            {
                Assert.False(window.IntegrationSwitch.IsEnabled);
                Assert.Equal(SettingsModel.DimmedOpacity, window.IntegrationTitle.Opacity);
                Assert.Equal(ClaudeIntegration.MoveToApplicationsError, window.IntegrationCaption.Text);
                AssertBrush(Caution, window.IntegrationCaption);
                Assert.Equal(1.0, window.IntegrationCaption.Opacity);
                Assert.Equal(CommandLineToolText.NotInstalledText, Text(window.CommandLineToolStatus));
                Assert.Equal(Visibility.Collapsed, window.CommandLineToolCaption.Visibility);
                Assert.Equal(ClaudeIntegration.MoveToApplicationsError, window.CommandLineToolMessage.Text);
                Assert.Equal(Visibility.Visible, window.CommandLineToolMessage.Visibility);
                Assert.False(window.CommandLineToolButton.IsEnabled);

                tool.IsInstalled = true;
                window.Model.Refresh();   // as the window does each time it activates
                Assert.Equal(Visibility.Visible, window.CommandLineToolCaption.Visibility);
                Assert.Equal(Visibility.Collapsed, window.CommandLineToolMessage.Visibility);
                Assert.True(window.CommandLineToolButton.IsEnabled);
                Assert.Equal(CommandLineToolText.RemoveButton, window.CommandLineToolButton.Content);
            });
        });
    }

    [Fact]
    public void WithNoToolTheCommandLineToolGroupIsLeftOut()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var window = new SettingsWindow(state, services, updates);
            Laid(window, () => Assert.Equal(Visibility.Collapsed, window.CommandLineToolGroup.Visibility));
        });
    }

    /// <summary>The registry asks for the tool when it opens Settings, so a tool the app made after the registry
    /// still reaches the window.</summary>
    [Fact]
    public void SettingsOpenedByTheRegistryGetsTheToolMadeAfterIt()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            ICommandLineTool? made = null;
            var registry = new WindowRegistry(state, services, updates, h.Dialogs, () => made);
            made = new FakeCommandLineTool();
            registry.OpenSettings();
            // The one the registry showed, whatever a test that failed before closing its own left behind.
            var window = Application.Current.Windows.OfType<SettingsWindow>().Single(shown => shown.IsVisible);
            try
            {
                WindowTestSupport.Layout(window, new Size(480, 560));
                Assert.Equal(Visibility.Visible, window.CommandLineToolGroup.Visibility);
            }
            finally
            {
                // Closed however the body ends: a window left on screen would be the owner later dialogs find.
                window.Close();
            }
        });
    }

    /// <summary>The Claude tab's icon (a package walk plus an icon extraction) must not run
    /// inside the constructor, or opening Settings would stall on it every time.</summary>
    [Fact]
    public void TheClaudeTabIconIsLoadedOffTheUiThreadAfterConstruction()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var install = new FakeClaudeInstall();
        var services = new PlatformServices(h.Settings, install, h.Claude, h.Notifier, new FakeAutostart(), new FakeUpdater());
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var window = new SettingsWindow(state, services, updates);
            try
            {
                var afterConstruction = install.DetectCalls;
                // The deferred load's Task.Run hop means one Background-priority pump only starts it;
                // pump repeatedly (a nested message loop, not a blocking wait, so its own continuation
                // can still reach the dispatcher) until the async work actually completes. The load is
                // view code on the real pool, not AppHost's Background, so this is a real wait, and
                // Wait.Eventually's ceiling covers a runner slow to hand it a thread.
                Assert.True(PumpUntil(window.Dispatcher, () => install.DetectCalls > afterConstruction, Wait.Eventually),
                    "the icon's Detect()+Load() must be deferred, not run inside the constructor");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static bool PumpUntil(Dispatcher dispatcher, Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        return true;
    }

    [Fact]
    public void RestoreDialogListsBackupsWithTheOriginalLast()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.SetEnabled("aws-mcp", false);
        WpfApp.Invoke(() =>
        {
            var dialog = new RestoreDialog(state);
            dialog.Measure(new Size(460, 400));
            dialog.Arrange(new Rect(0, 0, 460, 400));
            dialog.UpdateLayout();
            Assert.Equal(2, dialog.BackupList.Items.Count);
            Assert.Equal("claude_desktop_config.original.json", dialog.BackupList.Items[1]);
            Assert.False(dialog.Model.CanRestore);
            dialog.BackupList.SelectedIndex = 0;
            Assert.True(dialog.Model.CanRestore);
            Assert.Equal(dialog.Model.Backups[0], dialog.Model.Selection);
        });
    }

    /// <summary>The Tools group lists the four launchers, one line each with its status; how to install a missing one
    /// is the footer's, one line a family, above the line that says what they are for.</summary>
    [Fact]
    public void ClaudeTabListsTheFourToolsWithTheirStatus()
    {
        using var h = new AppStateHarness();
        h.Tools.Statuses[Tool.Npx] = new ToolStatus(@"C:\Program Files\nodejs\npx.cmd", "10.9.2");
        h.Tools.Statuses[Tool.Node] = new ToolStatus(@"C:\Program Files\nodejs\node.exe", null);
        h.Tools.Statuses[Tool.Uvx] = ToolStatus.NotFound;
        h.Tools.Statuses[Tool.Uv] = ToolStatus.NotFound;
        using var state = h.Create();
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var window = new SettingsWindow(state, services, updates);   // opening the window starts the probe
            try
            {
                h.Drain();
                Assert.Equal(4, state.ToolStatuses.Count);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                window.Measure(new Size(480, 500));
                window.Arrange(new Rect(0, 0, 480, 500));
                window.UpdateLayout();
                var rows = window.ToolList.Items.Cast<ToolRow>().ToArray();
                Assert.Equal(["npx", "node", "uvx", "uv"], rows.Select(r => r.Name).ToArray());
                Assert.Equal(["10.9.2", "Found", "Not found", "Not found"], rows.Select(r => r.StatusText).ToArray());
                Assert.Null(rows[0].Note);
                Assert.NotNull(rows[2].Note);
                // `!` is required: Roslyn tracks no null state for an array element, so the
                // Assert.NotNull above does not narrow it and TreatWarningsAsErrors turns the
                // dereference warning into an error.
                Assert.Equal("Install uv", rows[2].Note!.LinkTitle);
                Assert.Equal("winget install astral-sh.uv", rows[3].Note!.InstallCommand);
                Assert.Equal(Visibility.Visible, window.ToolFooter.Visibility);
                Assert.Equal(["uv and uvx: Install uv or run winget install astral-sh.uv."],
                    window.ToolFooter.Items.Cast<ToolFooterLine>().Select(line => line.Text).ToArray());
                Assert.Equal(ToolNote.SettingsCaption, window.ToolsFooter.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The General tab: a preview says where its updates come from under its version; the notify switch reads its
    /// note as its help text; a login item that could not be changed shows why under its switch, in amber, and the
    /// switch reads that too.</summary>
    [Fact]
    public void TheGeneralTabShowsEachRowsNote()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var autostart = new FakeAutostart();
        var updater = new FakeUpdater { VersionDisplay = "1.5.0-preview.10+1a2b3c4" };
        var services = new PlatformServices(h.Settings, new FakeClaudeInstall(), h.Claude, h.Notifier, autostart, updater);
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var window = new SettingsWindow(state, services, updates);
            Laid(window, () =>
            {
                Assert.Equal(Visibility.Visible, window.UpdatesCaption.Visibility);
                Assert.Equal(SettingsModel.PreviewUpdatesCaption, window.UpdatesCaption.Text);
                Assert.Equal(SettingsModel.NotifyCaption, AutomationProperties.GetHelpText(window.NotifySwitch));
                Assert.Equal(Visibility.Collapsed, window.LoginItemNote.Visibility);

                autostart.FailWith = "Access is denied.";
                window.LaunchAtStartupSwitch.IsChecked = true;
                Assert.Equal(Visibility.Visible, window.LoginItemNote.Visibility);
                Assert.Equal(SettingsModel.StartupEntryFailureNote("Access is denied."), window.LoginItemNote.Text);
                AssertBrush(Caution, window.LoginItemNote);
                Assert.Equal(SettingsModel.StartupEntryFailureNote("Access is denied."), AutomationProperties.GetHelpText(window.LaunchAtStartupSwitch));
            });
        });
    }

    /// <summary>The Storage tab's path rows show the folder's name, and the folder it is in as the note; the count
    /// beside the stepper follows it.</summary>
    [Fact]
    public void TheStorageTabShowsEachFolderByNameAndPlace()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var window = new SettingsWindow(state, services, updates);
            Laid(window, () =>
            {
                Assert.Equal(SettingsModel.ShownName(h.StoreDir), window.StoreDirName.Text);
                Assert.Equal(SettingsModel.ShownParent(h.StoreDir), window.StoreDirParent.Text);
                Assert.Equal(SettingsModel.ShownName(h.BackupsDir), window.BackupsDirName.Text);
                Assert.Equal(SettingsModel.ShownParent(h.BackupsDir), window.BackupsDirParent.Text);
                var count = window.Model.BackupKeepCount;
                Assert.Equal(count.ToString(System.Globalization.CultureInfo.CurrentCulture), window.KeepCount.Text);
                window.Model.IncrementKeepCount();
                Assert.Equal((count + 1).ToString(System.Globalization.CultureInfo.CurrentCulture), window.KeepCount.Text);
            });
        });
    }

    /// <summary>The −/+ buttons say what they do and the count, and dim at the bounds; each Use Default/Choose… pair says
    /// its row. Windows only: the Mac Stepper and rows do both themselves.</summary>
    [Fact]
    public void TheStorageAndClaudeRowButtonsAreNamedForNarrator()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var window = new SettingsWindow(state, services, updates);
            Laid(window, () =>
            {
                Assert.Equal(SettingsModel.FewerBackupsLabel, AutomationProperties.GetName(window.FewerBackupsButton));
                Assert.Equal(SettingsModel.MoreBackupsLabel, AutomationProperties.GetName(window.MoreBackupsButton));
                Assert.Equal(SettingsModel.KeepCountHelpText(20), AutomationProperties.GetHelpText(window.MoreBackupsButton));
                Assert.True(window.FewerBackupsButton.IsEnabled);
                window.Model.BackupKeepCount = SettingsModel.MinKeepCount;
                WindowTestSupport.Layout(window, new Size(480, 560));
                Assert.False(window.FewerBackupsButton.IsEnabled);
                Assert.True(window.MoreBackupsButton.IsEnabled);
                Assert.Equal(SettingsModel.KeepCountHelpText(SettingsModel.MinKeepCount), AutomationProperties.GetHelpText(window.FewerBackupsButton));
                window.Model.BackupKeepCount = SettingsModel.MaxKeepCount;
                WindowTestSupport.Layout(window, new Size(480, 560));
                Assert.True(window.FewerBackupsButton.IsEnabled);
                Assert.False(window.MoreBackupsButton.IsEnabled);

                Assert.Equal(SettingsModel.MasterListHeader, AutomationProperties.GetHelpText(window.UseDefaultStoreButton));
                Assert.Equal(SettingsModel.MasterListHeader, AutomationProperties.GetHelpText(window.ChooseStoreButton));
                Assert.Equal(SettingsModel.ConfigPathLabel, AutomationProperties.GetHelpText(window.UseDefaultConfigButton));
                Assert.Equal(SettingsModel.ConfigPathLabel, AutomationProperties.GetHelpText(window.ChooseConfigButton));
                Assert.Equal(SettingsModel.LaunchTargetLabel, AutomationProperties.GetHelpText(window.UseDefaultLaunchTargetButton));
                Assert.Equal(SettingsModel.LaunchTargetLabel, AutomationProperties.GetHelpText(window.ChooseLaunchTargetButton));

                // While a chosen program is verified, Choose… waits.
                Assert.True(window.ChooseLaunchTargetButton.IsEnabled);
                Assert.True(window.Model.BeginCheckingLaunchTarget());
                WindowTestSupport.Layout(window, new Size(480, 560));
                Assert.False(window.ChooseLaunchTargetButton.IsEnabled);
                window.Model.EndCheckingLaunchTarget();
                WindowTestSupport.Layout(window, new Size(480, 560));
                Assert.True(window.ChooseLaunchTargetButton.IsEnabled);
            });
        });
    }

    /// <summary>Check for Updates reads Checking… and cannot be clicked while a check runs. Windows only: Sparkle shows the Mac's.</summary>
    [Fact]
    public void CheckForUpdatesReadsCheckingWhileACheckRuns()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var updater = new FakeUpdater { CheckGate = new TaskCompletionSource<bool>() };
        var services = new PlatformServices(h.Settings, new FakeClaudeInstall(), h.Claude, h.Notifier, new FakeAutostart(), updater);
        using var updates = new UpdateCoordinator(updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var window = new SettingsWindow(state, services, updates);
            Laid(window, () =>
            {
                Assert.Equal(SettingsModel.CheckForUpdatesTitle, window.CheckForUpdatesButton.Content);
                Assert.True(window.CheckForUpdatesButton.IsEnabled);
                _ = window.Model.CheckForUpdatesAsync();
                WindowTestSupport.Layout(window, new Size(480, 560));
                Assert.Equal(SettingsModel.CheckingTitle, window.CheckForUpdatesButton.Content);
                Assert.False(window.CheckForUpdatesButton.IsEnabled);
            });
        });
        updater.CheckGate.SetResult(true);
    }

    /// <summary>A footer line is one sentence: the family's tools, the link's title as a hyperlink, "or run", and the
    /// command in its own run, set apart as every command is.</summary>
    [Fact]
    public void AToolFooterLineDrawsItsLinkAndItsCommand()
    {
        WpfApp.Invoke(() =>
        {
            var line = ToolFooterLine.Make(new Dictionary<Tool, ToolStatus> { [Tool.Uv] = ToolStatus.NotFound })[0];
            var block = new TextBlock();
            ToolFooterText.SetLine(block, line);
            var inlines = block.Inlines.ToList();
            Assert.Equal(5, inlines.Count);
            var link = Assert.IsType<Hyperlink>(inlines[1]);
            Assert.Equal("Install uv", Assert.IsType<Run>(link.Inlines.FirstInline).Text);
            Assert.Equal(new TextRun("winget install astral-sh.uv", IsSetApart: true).ShownText, Assert.IsType<Run>(inlines[3]).Text);
            Assert.Equal("uv and uvx: ", Assert.IsType<Run>(inlines[0]).Text);
            Assert.Equal(" or run ", Assert.IsType<Run>(inlines[2]).Text);
            Assert.Equal(".", Assert.IsType<Run>(inlines[4]).Text);
            ToolFooterText.SetLine(block, null);
            Assert.Empty(block.Inlines);
        });
    }

    /// <summary>The window asks for the height at which no tab scrolls, in the tallest state each shows: the Tools footer with a
    /// missing family, an installed command-line tool, and the integration on with the name-clash warning under its
    /// switch and the second switch's note under it. Shown, so the title bar and the tab strip take their real room.</summary>
    [Fact]
    public void NoTabScrollsAtTheWindowsOwnSize()
    {
        using var h = new AppStateHarness();
        MasterStoreIO.Save(new MasterStore([new(ClaudeIntegration.EntryName, new McpEntry(true, JsonValue.Object(("command", JsonValue.String("x")))))]),
            h.MasterStorePath);
        h.ControlToolPath = @"C:\Users\me\AppData\Local\Connector Control\bin\connector-control.exe";
        h.Settings.ClaudeIntegrationEnabled = true;
        h.Tools.Statuses[Tool.Npx] = new ToolStatus(@"C:\Program Files\nodejs\npx.cmd", "11.6.2");
        h.Tools.Statuses[Tool.Node] = new ToolStatus(@"C:\Program Files\nodejs\node.exe", "v24.11.0");
        h.Tools.Statuses[Tool.Uvx] = ToolStatus.NotFound;
        h.Tools.Statuses[Tool.Uv] = ToolStatus.NotFound;
        using var state = h.Create();
        var services = h.Services();
        var tool = new FakeCommandLineTool { IsInstalled = true };
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var window = new SettingsWindow(state, services, updates, tool) { ShowActivated = false };
            try
            {
                h.Drain();
                // What the window asks for; once shown, Height reports what a shorter screen cut it to.
                var asked = window.Height;
                window.Show();
                // Loaded is below DataBind, so this runs every binding's first update and the layout after it.
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                Assert.Equal(ClaudeIntegration.ClashNote, window.IntegrationCaption.Text);
                Assert.Equal(Visibility.Visible, window.ConfirmChangesCaption.Visibility);
                Assert.Single(window.ToolFooter.Items.Cast<ToolFooterLine>());
                foreach (var item in window.Tabs.Items.Cast<TabItem>())
                {
                    window.Tabs.SelectedItem = item;
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                    window.UpdateLayout();
                    var scroller = Assert.IsType<ScrollViewer>(item.Content);
                    var name = ((TextBlock)((StackPanel)item.Header).Children[1]).Text;
                    var measured = $"{name}: extent {scroller.ExtentHeight}, viewport {scroller.ViewportHeight}, window {window.ActualHeight}";
                    TestContext.Current.TestOutputHelper?.WriteLine(measured);
                    Assert.True(scroller.ViewportHeight > 0, measured);
                    // The height the tab needs, from what the window spends around its viewport; asked of the window's own
                    // Height rather than its viewport, since a screen shorter than the window (a 768-pixel laptop, the CI
                    // runner) cuts the window to the screen, and then the tab scrolls, as it must.
                    var needed = scroller.ExtentHeight + (window.ActualHeight - scroller.ViewportHeight);
                    Assert.True(needed <= asked, $"the window is too short for the tab: needs {needed}, asks for {asked}; {measured}");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
