using System.Windows;
using System.Windows.Controls;
using ConnectorControl.App.Tests.TestSupport;
using ConnectorControl.App.Views;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;
using AppServices = ConnectorControl.App.Services.Services;

namespace ConnectorControl.App.Tests;

public class SettingsWindowTests
{
    private static AppServices Services(AppStateHarness h) =>
        new(h.Settings, new FakeClaudeInstall(), h.Claude, h.Notifier, new FakeAutostart(), new FakeUpdater());

    [Fact]
    public void SettingsWindowLoadsAllThreeTabs()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var services = Services(h);
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var window = new SettingsWindow(state, services, updates);
            window.Measure(new Size(480, 500));
            window.Arrange(new Rect(0, 0, 480, 500));
            window.UpdateLayout();
            Assert.Equal(3, window.Tabs.Items.Count);
            var headers = window.Tabs.Items.Cast<TabItem>().Select(t => ((TextBlock)((StackPanel)t.Header).Children[1]).Text).ToArray();
            Assert.Equal(["General", "Storage", "Claude"], headers);
            Assert.Equal("Version 1.2.2", window.Model.VersionText);
            Assert.Equal(h.StoreDir, window.Model.StoreDirPath);
            Assert.Equal("MSIX package", window.Model.InstallKindText);
        });
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
}
