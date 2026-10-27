using System.ComponentModel;
using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.State;

/// <summary>SettingsView state: three tabs, every toggle, path, and button.</summary>
public sealed class SettingsModel : ObservableObject, IDisposable
{
    public const string GeneralTab = "General";
    public const string StorageTab = "Storage";
    public const string ClaudeTab = "Claude";
    public const string LaunchAtStartupTitle = "Launch at startup";
    public const string ConfirmRestartTitle = "Confirm before restarting Claude";
    public const string ConfirmQuitTitle = "Confirm before quitting";
    public const string NotifyTitle = "Notify on external changes";
    public const string UpdatesHeader = "Updates";
    public const string AutoUpdateTitle = "Automatically download and install updates";
    public const string CheckForUpdatesTitle = "Check for Updates";
    public const string MasterListHeader = "Master List Location";
    public const string ChooseTitle = "Choose";
    public const string UseDefaultTitle = "Use Default";
    public const string BackupsHeader = "Backups";
    public const string BackupsCaption = "Claude’s config, the master list and collections.json are backed up automatically before every change.";
    public const string ShowInExplorerTitle = "Show in Explorer";
    public const string ClaudeAppHeader = "Claude App";
    // "Claude integration" is the Windows sentence case; the Mac's title case says "Claude Integration".
    public const string ClaudeIntegrationHeader = "Claude integration";
    public const string ClaudeIntegrationTitle = "Let Claude manage Connector Control";
    public const string ClaudeIntegrationCaption = "Adds a connector named connector-control to Claude's config, so Claude can list, switch and edit your connectors. Takes effect when Claude restarts.";
    public const string ConfirmClaudeChangesTitle = "Confirm changes Claude makes to connectors";
    public const string ConfirmClaudeChangesOnCaption = "Adding, editing or deleting a connector, importing, subscribing and restoring a backup ask here first.";
    public const string ConfirmClaudeChangesOffCaption = "Claude can then change what it runs without asking here. Text Claude reads, such as a web page, could ask it to.";
    public const string ConfigPathLabel = "Config file";
    public const string LaunchTargetLabel = "Launch target";
    public const string LaunchTargetRejectedTitle = "That program can’t be used as Claude Desktop";
    public const int MinKeepCount = 5;
    public const int MaxKeepCount = 100;

    private readonly AppState state;
    private readonly ISettings settings;
    private readonly IAutostart autostart;
    private readonly IClaudeInstall install;
    private readonly IUpdater updater;
    private readonly UpdateCoordinator updates;
    private bool launchAtStartup;
    private string? loginItemNote;
    private ClaudeInstallInfo installInfo;

    public SettingsModel(AppState state, ISettings settings, IAutostart autostart, IClaudeInstall install, IUpdater updater, UpdateCoordinator updates)
    {
        this.state = state;
        this.settings = settings;
        this.autostart = autostart;
        this.install = install;
        this.updater = updater;
        this.updates = updates;
        launchAtStartup = autostart.IsEnabled;
        installInfo = install.Detect();
        state.PropertyChanged += OnStateChanged;
    }

    /// <summary>Called whenever the window opens: autostart and the install are read fresh (the user may have changed either since).</summary>
    public void Refresh()
    {
        launchAtStartup = autostart.IsEnabled;
        installInfo = install.Detect();
        RaiseAll();
    }

    // MARK: General

    /// <summary>No-op when the OS already agrees; on failure revert the toggle and show the note.</summary>
    public bool LaunchAtStartup
    {
        get => launchAtStartup;
        set
        {
            if (!Set(ref launchAtStartup, value))
            {
                return;
            }
            var actual = autostart.IsEnabled;
            if (value == actual)
            {
                return;
            }
            try
            {
                autostart.SetEnabled(value);
                LoginItemNote = null;
            }
            catch (InvalidOperationException ex)
            {
                launchAtStartup = actual;
                Raise(nameof(LaunchAtStartup));
                LoginItemNote = StartupEntryFailureNote(ex.Message);
            }
        }
    }

    /// <summary>Windows has no "login item" concept; this names the Settings ▸ Apps ▸ Startup entry instead.</summary>
    public static string StartupEntryFailureNote(string message) => $"Couldn't update startup entry: {message}";

    public string? LoginItemNote
    {
        get => loginItemNote;
        private set
        {
            if (Set(ref loginItemNote, value))
            {
                Raise(nameof(HasLoginItemNote));
            }
        }
    }

    public bool HasLoginItemNote => loginItemNote is not null;

    public bool ConfirmBeforeRestart
    {
        get => settings.ConfirmBeforeRestart;
        set { settings.ConfirmBeforeRestart = value; Raise(); }
    }

    public bool ConfirmBeforeQuit
    {
        get => settings.ConfirmBeforeQuit;
        set { settings.ConfirmBeforeQuit = value; Raise(); }
    }

    public bool NotifyExternalChanges
    {
        get => settings.NotifyExternalChanges;
        set { settings.NotifyExternalChanges = value; Raise(); }
    }

    public bool AutoUpdate
    {
        get => settings.AutoUpdate;
        set { settings.AutoUpdate = value; Raise(); }
    }

    // MARK: Claude integration

    /// <summary>The switch writes through AppState, which applies at once and answers the offer.</summary>
    public bool ClaudeIntegrationEnabled
    {
        get => settings.ClaudeIntegrationEnabled;
        set { state.SetClaudeIntegrationEnabled(value); RaiseAll(); }
    }

    /// <summary>Dimmed while a connector holds the name — unless the switch is on, so it can still go off.</summary>
    public bool CanChangeClaudeIntegration => settings.ClaudeIntegrationEnabled || !state.ClaudeIntegrationClash;

    /// <summary>The clash, named under the switch; null while there is none.</summary>
    public string? ClaudeIntegrationNote => state.ClaudeIntegrationClash ? ClaudeIntegration.ClashNote : null;

    public bool ConfirmClaudeChanges
    {
        get => settings.ConfirmClaudeChanges;
        set { state.SetConfirmClaudeChanges(value); Raise(); }
    }

    /// <summary>The checkbox keeps its value while the switch is off, dimmed.</summary>
    public bool CanChangeConfirmClaudeChanges => settings.ClaudeIntegrationEnabled;

    /// <summary>
    /// Under the checkbox: what it guards while ticked, what is at stake while not, and nothing
    /// while the switch is off.
    /// </summary>
    public string? ConfirmClaudeChangesCaption =>
        !settings.ClaudeIntegrationEnabled ? null
        : settings.ConfirmClaudeChanges ? ConfirmClaudeChangesOnCaption : ConfirmClaudeChangesOffCaption;

    /// <summary>The unticked caption is a warning (amber in the view).</summary>
    public bool ConfirmClaudeChangesCaptionIsWarning => settings.ClaudeIntegrationEnabled && !settings.ConfirmClaudeChanges;

    /// <summary>The running app's version, as the Updates section shows it.</summary>
    public string Version => updater.VersionDisplay;

    public bool UpdatesEnabled => updater.IsAvailable;

    /// <summary>The Mac's static and an instance property of the same name can coexist there; C# forbids that, so the instance property below calls this.</summary>
    public static string VersionTextFor(string version) => $"Version {version}";

    public string VersionText => VersionTextFor(updater.VersionDisplay);

    public Task CheckForUpdatesAsync() => updates.CheckAsync(interactive: true);

    // MARK: Storage

    public string StoreDirPath => state.Service.Paths.StoreDir;

    public bool CanUseDefaultStore => !string.IsNullOrEmpty(settings.MasterStoreDir);

    public void ChooseStoreDir(string dir)
    {
        state.RepointStore(dir);
        RaiseAll();
    }

    public void UseDefaultStoreDir()
    {
        state.RepointStore(null);
        RaiseAll();
    }

    public int BackupKeepCount
    {
        get => settings.BackupKeepCount;
        set
        {
            var clamped = Math.Clamp(value, MinKeepCount, MaxKeepCount);
            if (clamped == settings.BackupKeepCount)
            {
                Raise();                        // the stepper's own binding snaps back to the clamped value
                Raise(nameof(KeepCountLabel));  // same two raises on both branches, so the label can never drift
                return;
            }
            settings.BackupKeepCount = clamped;
            state.RefreshServiceSettings();
            Raise();
            Raise(nameof(KeepCountLabel));
        }
    }

    /// <summary>The Mac's static and an instance property of the same name can coexist there; C# forbids that, so the instance property below calls this.</summary>
    public static string KeepCountLabelFor(int count) => $"Keep {count} backups of each file";

    public string KeepCountLabel => KeepCountLabelFor(settings.BackupKeepCount);

    public void IncrementKeepCount() => BackupKeepCount = settings.BackupKeepCount + 1;

    public void DecrementKeepCount() => BackupKeepCount = settings.BackupKeepCount - 1;

    public string BackupsDir => state.Service.Backups.BackupsDir;

    // MARK: Claude

    public string InstallKindText => installInfo.Kind switch
    {
        ClaudeInstallKind.Msix => "MSIX package",
        ClaudeInstallKind.Legacy => "Legacy installer",
        _ => ToolNote.NotFoundText,
    };

    public string ClaudeConfigPath => state.Service.Paths.ClaudeConfigPath;

    public bool CanUseDefaultClaudeConfig => !string.IsNullOrEmpty(settings.ClaudeConfigPath);

    public void ChooseClaudeConfig(string path)
    {
        state.RepointClaudeConfig(path);
        RaiseAll();
    }

    public void UseDefaultClaudeConfig()
    {
        state.RepointClaudeConfig(null);
        RaiseAll();
    }

    public string LaunchTargetText =>
        settings.ClaudeLaunchTarget is { Length: > 0 } overridden ? overridden : installInfo.LaunchTarget ?? ToolNote.NotFoundText;

    public bool CanUseDefaultLaunchTarget => !string.IsNullOrEmpty(settings.ClaudeLaunchTarget);

    public void ChooseLaunchTarget(string exe)
    {
        settings.ClaudeLaunchTarget = exe;
        RaiseAll();
    }

    public void UseDefaultLaunchTarget()
    {
        settings.ClaudeLaunchTarget = null;
        RaiseAll();
    }

    // MARK: Tools

    public IReadOnlyList<ToolRow> ToolRows =>
        ToolInfo.All.Select(tool => ToolRow.Make(tool, state.ToolStatuses.TryGetValue(tool, out var status) ? status : null)).ToList();

    /// <summary>Windows probes all four when the window opens.</summary>
    public void RefreshTools() => _ = state.RefreshToolsAsync();

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Affects(e, nameof(AppState.ToolStatuses)))
        {
            Raise(nameof(ToolRows));
        }
    }

    public void Dispose() => state.PropertyChanged -= OnStateChanged;
}
