using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.State;

/// <summary>Catalog §4 SettingsView state: three tabs, every toggle, path, and button.</summary>
public sealed class SettingsModel : ObservableObject
{
    public const string GeneralTab = "General";
    public const string StorageTab = "Storage";
    public const string ClaudeTab = "Claude";
    public const string LaunchAtStartupTitle = "Launch at startup";
    public const string ConfirmRestartTitle = "Confirm before restarting Claude";
    public const string ConfirmQuitTitle = "Confirm before quitting";
    public const string NotifyTitle = "Notify about changes made outside Connector Control";
    public const string NotifyCaption = "Covers edits to Claude's config and synced connector-list changes, including when a remote change needs a Claude restart.";
    public const string UpdatesHeader = "Updates";
    public const string AutoUpdateTitle = "Automatically download and install updates";
    public const string CheckForUpdatesTitle = "Check for Updates…";
    public const string MasterListHeader = "Master List Location";
    public const string ChooseTitle = "Choose…";
    public const string UseDefaultTitle = "Use Default";
    public const string BackupsHeader = "Backups";
    public const string BackupsCaption = "Both config files are backed up automatically before every change.";
    public const string ShowInExplorerTitle = "Show in Explorer";
    public const string RestoreTitle = "Restore…";
    public const string ClaudeAppHeader = "Claude App";
    public const string ConfigPathLabel = "Config file";
    public const string LaunchTargetLabel = "Launch target";
    public const string NotFoundText = "Not found";
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

    public SettingsModel(AppState state, ISettings settings, IAutostart autostart, IClaudeInstall install, IUpdater updater, UpdateCoordinator updates)
    {
        this.state = state;
        this.settings = settings;
        this.autostart = autostart;
        this.install = install;
        this.updater = updater;
        this.updates = updates;
        launchAtStartup = autostart.IsEnabled;
    }

    /// <summary>Called whenever the window opens: autostart is read fresh (the user may have changed it in Windows Settings).</summary>
    public void Refresh()
    {
        launchAtStartup = autostart.IsEnabled;
        RaiseAll();
    }

    // MARK: General (catalog §4.2)

    /// <summary>Catalog §4.2: no-op when the OS already agrees; on failure revert the toggle and show the note.</summary>
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
                LoginItemNote = $"Couldn't update login item: {ex.Message}";
            }
        }
    }

    public string? LoginItemNote { get => loginItemNote; private set => Set(ref loginItemNote, value); }

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

    public bool UpdatesEnabled => updater.IsAvailable;

    public string VersionText => $"Version {updater.VersionDisplay}";

    public Task CheckForUpdatesAsync() => updates.CheckAsync(interactive: true);

    // MARK: Storage (catalog §4.3)

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

    public string KeepCountLabel => $"Keep {settings.BackupKeepCount} backups of each file";

    public void IncrementKeepCount() => BackupKeepCount = settings.BackupKeepCount + 1;

    public void DecrementKeepCount() => BackupKeepCount = settings.BackupKeepCount - 1;

    public string BackupsDir => state.Service.Backups.BackupsDir;

    // MARK: Claude (catalog §4.4, spec §7.3)

    public string InstallKindText => install.Detect().Kind switch
    {
        ClaudeInstallKind.Msix => "MSIX package",
        ClaudeInstallKind.Legacy => "Legacy installer",
        _ => NotFoundText,
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
        settings.ClaudeLaunchTarget is { Length: > 0 } overridden ? overridden : install.Detect().LaunchTarget ?? NotFoundText;

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
}
