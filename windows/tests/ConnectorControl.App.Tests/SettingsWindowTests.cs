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
                window.Measure(new Size(480, 500));
                window.Arrange(new Rect(0, 0, 480, 500));
                window.UpdateLayout();
                Assert.Equal(3, window.Tabs.Items.Count);
                var headers = window.Tabs.Items.Cast<TabItem>().Select(t => ((TextBlock)((StackPanel)t.Header).Children[1]).Text).ToArray();
                Assert.Equal(["General", "Storage", "Claude"], headers);
                Assert.Equal("Version 1.2.2", window.Model.VersionText);
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
                Assert.False(window.ConfirmChangesBox.IsEnabled);
                Assert.Equal(Visibility.Collapsed, window.ConfirmChangesCaption.Visibility);
                Assert.Equal(Visibility.Collapsed, window.EndpointProblem.Visibility);
                Assert.Equal(Visibility.Visible, window.CommandLineToolGroup.Visibility);
                Assert.Equal(CommandLineToolText.NotInstalledText, Text(window.CommandLineToolStatus));
                Assert.Equal("Scripts can always use " + new TextRun(tool.ToolPath, IsSetApart: true).ShownText, Text(window.CommandLineToolCaption));
                Assert.Equal(Visibility.Visible, window.CommandLineToolCaption.Visibility);
                Assert.Equal(CommandLineToolText.InstallButton, window.CommandLineToolButton.Content);

                window.IntegrationSwitch.IsChecked = true;
                Assert.True(h.Settings.ClaudeIntegrationEnabled);
                Assert.True(window.ConfirmChangesBox.IsEnabled);
                Assert.Equal(SettingsModel.ConfirmClaudeChangesOnCaption, window.ConfirmChangesCaption.Text);
                AssertBrush(Secondary, window.ConfirmChangesCaption);
                Assert.Equal(SettingsModel.ConfirmClaudeChangesOnCaption, AutomationProperties.GetHelpText(window.ConfirmChangesBox));
                window.ConfirmChangesBox.IsChecked = false;
                Assert.Equal(SettingsModel.ConfirmClaudeChangesOffCaption, window.ConfirmChangesCaption.Text);
                AssertBrush(Caution, window.ConfirmChangesCaption);
                Assert.Equal(SettingsModel.ConfirmClaudeChangesOffCaption, AutomationProperties.GetHelpText(window.ConfirmChangesBox));

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

    /// <summary>Spec 2026-09-05-tool-probe §3.5: the Tools card lists the four launchers with their status and notes.</summary>
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
            }
            finally
            {
                window.Close();
            }
        });
    }
}
