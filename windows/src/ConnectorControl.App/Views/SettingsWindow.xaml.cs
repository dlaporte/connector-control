using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ConnectorControl.Core.State;
using Microsoft.Win32;
using AppServices = ConnectorControl.App.Services.Services;

namespace ConnectorControl.App.Views;

/// <summary>Catalog §4 / spec §7.3: single instance, 480×500, three tabs; autostart re-read whenever the window activates.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppState state;

    public SettingsWindow(AppState state, AppServices services, UpdateCoordinator updates)
    {
        InitializeComponent();
        this.state = state;
        Model = new SettingsModel(state, services.Settings, services.Autostart, services.ClaudeInstall, services.Updater, updates);
        DataContext = Model;
        GeneralTabItem.Header = TabHeader("", null, SettingsModel.GeneralTab);
        StorageTabItem.Header = TabHeader("", null, SettingsModel.StorageTab);
        ClaudeTabItem.Header = TabHeader("", ClaudeIconLoader.Load(services.ClaudeInstall.Detect()), SettingsModel.ClaudeTab);
        Activated += (_, _) => Model.Refresh();
    }

    public SettingsModel Model { get; }

    /// <summary>Glyph (or Claude's desaturated icon) + text, the Mac tab label.</summary>
    private StackPanel TabHeader(string glyph, ImageSource? image, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (image is not null)
        {
            panel.Children.Add(new Image { Source = image, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void OnCheckForUpdates(object sender, RoutedEventArgs e) => _ = Model.CheckForUpdatesAsync();

    private void OnChooseStoreDir(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose", Multiselect = false };
        if (picker.ShowDialog(this) == true)
        {
            Model.ChooseStoreDir(picker.FolderName);
        }
    }

    private void OnUseDefaultStore(object sender, RoutedEventArgs e) => Model.UseDefaultStoreDir();

    private void OnDecrementKeepCount(object sender, RoutedEventArgs e) => Model.DecrementKeepCount();

    private void OnIncrementKeepCount(object sender, RoutedEventArgs e) => Model.IncrementKeepCount();

    /// <summary>The Mac's "Reveal in Finder": select the backups folder in its parent.</summary>
    private void OnShowInExplorer(object sender, RoutedEventArgs e)
    {
        var dir = Model.BackupsDir;
        var arguments = Directory.Exists(dir) ? $"/select,\"{dir}\"" : $"\"{Path.GetDirectoryName(dir) ?? dir}\"";
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // This window has no dialogs seam (unlike ConfirmDialog/NamePromptDialog/UpdateDialog,
            // it never surfaces failures through IDialogs); explorer.exe failing to launch isn't
            // worth inventing one for, so it's a silent no-op rather than a crash.
        }
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        var dialog = new RestoreDialog(state) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.ShowDialog();
    }

    private void OnChooseClaudeConfig(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose", Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*", CheckFileExists = false };
        if (picker.ShowDialog(this) == true)
        {
            Model.ChooseClaudeConfig(picker.FileName);
        }
    }

    private void OnUseDefaultClaudeConfig(object sender, RoutedEventArgs e) => Model.UseDefaultClaudeConfig();

    private void OnChooseLaunchTarget(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose", Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*" };
        if (picker.ShowDialog(this) == true)
        {
            Model.ChooseLaunchTarget(picker.FileName);
        }
    }

    private void OnUseDefaultLaunchTarget(object sender, RoutedEventArgs e) => Model.UseDefaultLaunchTarget();
}
