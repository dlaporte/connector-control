using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ConnectorControl.Core;
using ConnectorControl.Core.State;
using ConnectorControl.App.Services;

namespace ConnectorControl.App.Views;

/// <summary>Single instance, 480×820, three tabs, none of which scrolls, every row in one shape as on the Mac;
/// autostart and the command-line tool re-read whenever the window activates.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppState state;

    public SettingsWindow(AppState state, PlatformServices services, UpdateCoordinator updates, ICommandLineTool? commandLineTool = null)
    {
        InitializeComponent();
        this.state = state;
        Model = new SettingsModel(state, services.Settings, services.Autostart, services.ClaudeInstall, services.Updater, updates, commandLineTool);
        DataContext = Model;
        GeneralTabItem.Header = TabHeader(GeneralGlyph, null, SettingsModel.GeneralTab);
        StorageTabItem.Header = TabHeader(StorageGlyph, null, SettingsModel.StorageTab);
        ClaudeTabItem.Header = TabHeader(ClaudeFallbackGlyph, null, SettingsModel.ClaudeTab);
        Activated += (_, _) => Model.Refresh();
        Closed += (_, _) => Model.Dispose();
        Model.RefreshTools();   // probe all four when the window opens
        // The Claude tab's icon needs a package walk plus an icon extraction (ClaudeIconLoader),
        // slow enough that doing it inline used to make opening Settings visibly stall; deferred to
        // Background priority (below layout/render) and run off the UI thread so the window shows
        // and its text is usable immediately, with the icon popping in a moment later.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            try
            {
                var icon = await Task.Run(() => ClaudeIconLoader.Load(services.ClaudeInstall.Detect()));
                ClaudeTabItem.Header = TabHeader(ClaudeFallbackGlyph, icon, SettingsModel.ClaudeTab);
                // The Claude App group's first row shows the same icon, as the Mac's shows the app's.
                if (icon is not null)
                {
                    ClaudeAppIcon.Source = icon;
                    ClaudeAppIcon.Visibility = Visibility.Visible;
                }
            }
            catch (Exception)
            {
                // async void: an unhandled exception here would crash the app. Leave the generic
                // glyph header already in place rather than rethrow for a popped-in icon that failed.
            }
        }));
    }

    public SettingsModel Model { get; }

    // The Mac tabs' gearshape and externaldrive, and for Claude a generic app where its own icon cannot be read. Code
    // points of IconFont (Segoe Fluent Icons, falling back to Segoe MDL2 Assets), which both fonts carry: Setting,
    // HardDrive and AppIconDefault.
    internal const string GeneralGlyph = "\uE713";
    internal const string StorageGlyph = "\uEDA2";
    internal const string ClaudeFallbackGlyph = "\uECAA";

    /// <summary>Glyph (or Claude's desaturated icon) + text, the Mac tab label.</summary>
    private StackPanel TabHeader(string glyph, ImageSource? image, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (image is not null)
        {
            var icon = new Image { Source = image, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) };
            // The icon is read at 32 pixels so it stays sharp up to 200%; drawn smaller, it is scaled down smoothly.
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            panel.Children.Add(icon);
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
        if (Pickers.Folder(this) is { } folder)
        {
            Model.ChooseStoreDir(folder);
        }
    }

    private void OnUseDefaultStore(object sender, RoutedEventArgs e) => Model.UseDefaultStoreDir();

    private void OnDecrementKeepCount(object sender, RoutedEventArgs e) => Model.DecrementKeepCount();

    private void OnIncrementKeepCount(object sender, RoutedEventArgs e) => Model.IncrementKeepCount();

    /// <summary>The Mac's "Reveal in Finder": select the backups folder in its parent.</summary>
    private void OnShowInExplorer(object sender, RoutedEventArgs e) => ExplorerReveal.Show(Model.BackupsDir);

    // RestoreDialog.Show presents through WpfDialogs.Present, which activates the dialog; that is fine
    // here because this window is the owner and the dialog is shown modally (ShowDialog).
    private void OnRestore(object sender, RoutedEventArgs e) => RestoreDialog.Show(this, state);

    private void OnChooseClaudeConfig(object sender, RoutedEventArgs e)
    {
        if (Pickers.ClaudeConfig(this) is { } path)
        {
            Model.ChooseClaudeConfig(path);
        }
    }

    private void OnUseDefaultClaudeConfig(object sender, RoutedEventArgs e) => Model.UseDefaultClaudeConfig();

    private async void OnChooseLaunchTarget(object sender, RoutedEventArgs e)
    {
        if (Pickers.Program(this) is not { } path || !Model.BeginCheckingLaunchTarget())
        {
            return;
        }
        // The path is trusted at every Restart Claude from now on, so a file that is not
        // Claude signed by Anthropic is refused here, with the reason, rather than at the
        // next restart click. WinVerifyTrust reads the whole file and can stall on a slow or
        // network path, so it runs off the UI thread rather than freezing the window, and the
        // row says "Checking…" while Choose… waits.
        string? problem;
        try
        {
            problem = await Task.Run(() => AuthenticodeVerifier.VerifyClaude(path));
        }
        finally
        {
            Model.EndCheckingLaunchTarget();
        }
        if (problem is not null)
        {
            ConfirmDialog.Show(this, SettingsModel.LaunchTargetRejectedTitle, problem, Dialogs.OkTitle, null, destructive: false);
            return;
        }
        Model.ChooseLaunchTarget(path);
    }

    private void OnUseDefaultLaunchTarget(object sender, RoutedEventArgs e) => Model.UseDefaultLaunchTarget();

    private void OnCommandLineTool(object sender, RoutedEventArgs e) => Model.CommandLineToolAction();
}
