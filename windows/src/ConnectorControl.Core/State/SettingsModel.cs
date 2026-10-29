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
    public const string ConfirmClaudeChangesOnCaption = "Adding, editing or deleting a connector, deleting a collection, importing, subscribing, applying an update and restoring a backup ask here first.";
    public const string ConfirmClaudeChangesOffCaption = "Claude can then change what it runs without asking here. Text Claude reads, such as a web page, could ask it to.";
    // "Command-line tool" is Windows' sentence case; the Mac mirror says "Command-Line Tool".
    public const string CommandLineToolHeader = "Command-line tool";
    /// <summary>How far a row's title fades while its switch cannot change, as the mockup draws it; the note that
    /// says why keeps full opacity.</summary>
    public const double DimmedOpacity = 0.45;
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
    private readonly ICommandLineTool? commandLineTool;
    private bool launchAtStartup;
    private string? loginItemNote;
    /// <summary>
    /// Windows only: the Mac has no install to detect. Null until the install is first read, and again after
    /// each <see cref="Refresh"/>: detecting walks every package registered for the user, too slow for the UI
    /// thread at each launch, and the control layer builds a model at launch that never reads it.
    /// </summary>
    private ClaudeInstallInfo? installInfo;

    public SettingsModel(AppState state, ISettings settings, IAutostart autostart, IClaudeInstall install, IUpdater updater, UpdateCoordinator updates,
        ICommandLineTool? commandLineTool = null)
    {
        this.state = state;
        this.settings = settings;
        this.autostart = autostart;
        this.install = install;
        this.updater = updater;
        this.updates = updates;
        this.commandLineTool = commandLineTool;
        launchAtStartup = autostart.IsEnabled;
        state.PropertyChanged += OnStateChanged;
        // The command-line tool announces each Install or Remove as it starts and as it ends, so a window
        // opened meanwhile hears the end too, not only the one that started it.
        if (commandLineTool is not null)
        {
            commandLineTool.Changed += RaiseCommandLineTool;
        }
    }

    /// <summary>Called whenever the window opens: autostart is read fresh, and the install the next time it is
    /// shown (the user may have changed either since), and whether the command-line tool is on PATH, as the
    /// Mac's view reads it each time it draws.</summary>
    public void Refresh()
    {
        launchAtStartup = autostart.IsEnabled;
        installInfo = null;
        RaiseAll();
        RaiseCommandLineTool();
    }

    private ClaudeInstallInfo InstallInfo => installInfo ??= install.Detect();

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

    /// <summary>Dimmed while a connector holds the name, or the app runs from where it will not stay — unless
    /// the switch is on, so it can still go off.</summary>
    public bool CanChangeClaudeIntegration =>
        settings.ClaudeIntegrationEnabled || (!state.ClaudeIntegrationClash && !state.ControlToolIsTransient);

    /// <summary>Why the switch is dimmed, under it: the app runs from where it will not stay (while off: moving
    /// it is the one thing that helps), else the clash; null while there is neither.</summary>
    public string? ClaudeIntegrationNote =>
        !settings.ClaudeIntegrationEnabled && state.ControlToolIsTransient ? ClaudeIntegration.MoveToApplicationsError
        : state.ClaudeIntegrationClash ? ClaudeIntegration.ClashNote : null;

    /// <summary>Under the switch: the note while there is one, else what the switch does.</summary>
    public string ClaudeIntegrationCaptionText => ClaudeIntegrationNote ?? ClaudeIntegrationCaption;

    /// <summary>A note under the switch is a warning (amber in the view).</summary>
    public bool ClaudeIntegrationCaptionIsWarning => ClaudeIntegrationNote is not null;

    /// <summary>The switch's title, dimmed with the switch; the note under it is not.</summary>
    public double ClaudeIntegrationTitleOpacity => CanChangeClaudeIntegration ? 1 : DimmedOpacity;

    /// <summary>Why Claude and the command-line tool cannot reach this app, while they cannot; shown in the
    /// Claude integration group whether the switch is on or off.</summary>
    public string? ControlEndpointProblem => state.ControlEndpointProblem;

    // Platform idiom: XAML cannot bind a visibility to an optional, so Windows adds a Has… companion for each
    // optional its views show or hide; SwiftUI shows an optional directly, so the Mac has none.
    public bool HasControlEndpointProblem => ControlEndpointProblem is not null;

    public bool HasConfirmClaudeChangesCaption => ConfirmClaudeChangesCaption is not null;

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

    public string InstallKindText => InstallInfo.Kind switch
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
        settings.ClaudeLaunchTarget is { Length: > 0 } overridden ? overridden : InstallInfo.LaunchTarget ?? ToolNote.NotFoundText;

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

    // MARK: Command-line tool

    /// <summary>Whether the app has a tool to put on PATH; the group is left out when it has none.</summary>
    public bool HasCommandLineTool => commandLineTool is not null;

    /// <summary>Where the tool is, or that it is not installed; the link set apart.</summary>
    public IReadOnlyList<TextRun> CommandLineToolStatus
    {
        get
        {
            if (commandLineTool is not { } tool)
            {
                return [];
            }
            if (!tool.IsInstalled)
            {
                return [new TextRun(CommandLineToolText.NotInstalledText)];
            }
            if (tool.InstalledLocation is not { } location)
            {
                return [new TextRun(CommandLineToolText.InPathText)];
            }
            return TextRun.Runs(CommandLineToolText.InstalledAtText(location), [location]);
        }
    }

    /// <summary>What to do with it: run <c>help</c> once installed; else the full path scripts can always use — left
    /// out while Install is refused, since that path is about to be gone.</summary>
    public IReadOnlyList<TextRun> CommandLineToolCaption
    {
        get
        {
            if (commandLineTool is not { } tool || InstallRefused)
            {
                return [];
            }
            if (tool.IsInstalled)
            {
                return TextRun.Runs(CommandLineToolText.RunHelpCaption, [CommandLineToolText.HelpCommand]);
            }
            return TextRun.Runs(CommandLineToolText.ScriptsCaption(tool.ToolPath), [tool.ToolPath]);
        }
    }

    public string CommandLineToolButton =>
        commandLineTool?.IsInstalled == true ? CommandLineToolText.RemoveButton : CommandLineToolText.InstallButton;

    /// <summary>Install or Remove is running: an administrator prompt, or the PATH's broadcast. The seam's state,
    /// so a window opened meanwhile sees it too.</summary>
    public bool CommandLineToolBusy => commandLineTool?.IsBusy == true;

    /// <summary>Under the row: why the last try failed — the seam's, so a window opened meanwhile shows it — else
    /// why Install cannot work here; null otherwise.</summary>
    public string? CommandLineToolMessage =>
        commandLineTool?.LastProblem ?? (InstallRefused ? ClaudeIntegration.MoveToApplicationsError : null);

    public bool HasCommandLineToolMessage => CommandLineToolMessage is not null;

    public bool CanUseCommandLineToolButton => commandLineTool is not null && !CommandLineToolBusy && !InstallRefused;

    /// <summary>Install while the app runs from where it will not stay: a link would point at a path about to be gone.</summary>
    private bool InstallRefused => commandLineTool?.IsInstalled == false && state.ControlToolIsTransient;

    /// <summary>The row's button: Install or Remove, one at a time. A failure stays under the row until the next try.
    /// The seam keeps the busy state and the failure and announces both, so nothing waits on the completion.</summary>
    public void CommandLineToolAction()
    {
        if (commandLineTool is not { } tool || !CanUseCommandLineToolButton)
        {
            return;
        }
        if (tool.IsInstalled)
        {
            tool.Remove(_ => { });
        }
        else
        {
            tool.Install(_ => { });
        }
    }

    // Platform idiom: the Mac's model relays the seam's announcements as objectWillChange, which repaints the
    // whole view; WPF bindings listen by name, so this raises every member the row binds.
    /// <summary>Every member the row binds, after its state changed.</summary>
    private void RaiseCommandLineTool()
    {
        foreach (var name in CommandLineToolProperties)
        {
            Raise(name);
        }
    }

    private static readonly string[] CommandLineToolProperties =
    [
        nameof(CommandLineToolStatus), nameof(CommandLineToolCaption), nameof(CommandLineToolButton), nameof(CommandLineToolBusy),
        nameof(CommandLineToolMessage), nameof(HasCommandLineToolMessage), nameof(CanUseCommandLineToolButton),
    ];

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
        // The Claude group follows a change made anywhere — the command-line tool or MDM turning the
        // integration on, a clash arriving with the store, the endpoint failing — as the Mac relays every
        // change AppState announces. Platform idiom: WPF bindings listen by name, so this raises the members
        // each change affects, where SwiftUI repaints from the Mac's one relay.
        if (ClaudeGroupSources.Any(name => Affects(e, name)))
        {
            foreach (var name in ClaudeGroupProperties)
            {
                Raise(name);
            }
            RaiseCommandLineTool();
        }
    }

    private static readonly string[] ClaudeGroupSources = [nameof(AppState.Store), nameof(AppState.ControlEndpointProblem)];

    private static readonly string[] ClaudeGroupProperties =
    [
        nameof(ClaudeIntegrationEnabled), nameof(CanChangeClaudeIntegration), nameof(ClaudeIntegrationNote),
        nameof(ClaudeIntegrationCaptionText), nameof(ClaudeIntegrationCaptionIsWarning), nameof(ClaudeIntegrationTitleOpacity),
        nameof(ConfirmClaudeChanges), nameof(CanChangeConfirmClaudeChanges), nameof(ConfirmClaudeChangesCaption),
        nameof(HasConfirmClaudeChangesCaption), nameof(ConfirmClaudeChangesCaptionIsWarning), nameof(ControlEndpointProblem),
        nameof(HasControlEndpointProblem),
    ];

    public void Dispose()
    {
        state.PropertyChanged -= OnStateChanged;
        if (commandLineTool is not null)
        {
            commandLineTool.Changed -= RaiseCommandLineTool;
        }
    }
}
