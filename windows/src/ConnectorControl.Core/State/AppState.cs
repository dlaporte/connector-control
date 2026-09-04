using System.Text.Json;
using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.State;

/// <summary>
/// The Mac AppState (catalog §1) on Windows. UI-thread-only; everything that
/// arrives from another thread comes through <see cref="AppHost.Marshal"/>.
/// Init sequence (catalog §1.2 minus legacy migration): resolve the service,
/// one-time ACL sweep, route the toast Restart action, reload, arm watchers.
/// </summary>
public sealed class AppState : ObservableObject, IDisposable
{
    public const string NoConnectorsSubtitle = "No connectors configured";
    public const string ClaudeConfigRegeneratedBody = "Claude's config was changed outside Connector Control — regenerated from your connector list. Restart Claude to pick it up.";
    public const string ConnectorListChangedRestartBody = "Connector list has changed, restart required.";
    public const string RegenerationFailedBody = "The connector configuration changed, but Claude's config could not be updated — open Connector Control to retry.";
    public const string ClaudeConfigChangedBody = "Claude's config changed outside Connector Control.";
    public const string StoreChangedBody = "The connector list changed outside Connector Control — review it before your next change is applied.";
    public const string QuitMessage = "Quit Connector Control?";
    public const string QuitButton = "Quit";
    public const string RestartMessage = "Restart Claude Desktop now?";
    public const string RestartInformative = "Any in-progress Claude conversation will be interrupted.";
    public const string RestartButton = "Restart";
    public const string NewProfileTitle = "New Profile";
    public const string RenameProfileTitle = "Rename Profile";
    public const string DeleteProfileInformative = "Its connector list is removed; backups keep prior states.";
    public const string DeleteButton = "Delete";
    /// <summary>Coined here, not taken from the Mac catalog: on macOS a relaunch cannot fail silently.</summary>
    public const string RelaunchFailedMessage = "Claude didn’t come back after the restart. Start Claude yourself, then try again.";
    /// <summary>Catalog §1.17: Claude's launch time is re-read 3 s after the restart completes.</summary>
    public static readonly TimeSpan RestartRecheckDelay = TimeSpan.FromSeconds(3);
    /// <summary>
    /// Spec §6.2 probe: a relaunched Claude is up within 20 s. RestartAsync reports null
    /// even when the AUMID launch silently did nothing (explorer.exe succeeds for any
    /// AUMID), so this second look is the only place that failure becomes visible.
    /// </summary>
    public static readonly TimeSpan RestartRelaunchCheck = TimeSpan.FromSeconds(20);

    private static readonly IReadOnlyDictionary<string, JsonValue> EmptyServers = new Dictionary<string, JsonValue>(StringComparer.Ordinal);

    private readonly ISettings settings;
    private readonly IClaudeProcess claude;
    private readonly INotifier notifier;
    private readonly PathContext paths;
    private readonly AppHost host;

    private MasterStore store = MasterStore.Empty();
    private string? lastError;
    private bool needsClaudeRestart;
    private bool applyRetryNeeded;
    private IReadOnlyDictionary<string, JsonValue> appliedServers = EmptyServers;
    private ConfigService service;
    private bool hasLoadedOnce;
    private FileWatcher? watcher;
    private FileWatcher? storeWatcher;
    private bool disposed;

    /// <summary>Test probe: both watchers are live. Spec §6.3 wants this true after every reload.</summary>
    internal bool WatchersArmed => watcher is { IsArmed: true } && storeWatcher is { IsArmed: true };

    public AppState(ISettings settings, IClaudeProcess claude, INotifier notifier, IDialogs dialogs, PathContext paths, AppHost host)
    {
        this.settings = settings;
        this.claude = claude;
        this.notifier = notifier;
        Dialogs = dialogs;
        this.paths = paths;
        this.host = host;
        service = MakeService(settings, this.paths);
        // Sweep the RESOLVED paths (a repointed store lives outside the default dir).
        AclSweep.RunOnce(settings, service.Paths);
        // The toast's Restart Claude button routes back here. Skipping the confirm-before-restart
        // dialog is deliberate: clicking the explicit action IS the confirmation. Stale-click guard:
        // an old toast must not restart a Claude that already picked up the config.
        notifier.RestartActionActivated += OnRestartActionActivated;
        Reload();
        ArmWatchers();
    }

    /// <summary>The prompts AppState itself raises (quit, restart, profiles); editor/settings windows own their own.</summary>
    internal IDialogs Dialogs { get; }

    // MARK: published state (catalog §1.1)

    public MasterStore Store { get => store; private set => Set(ref store, value); }

    /// <summary>Settable: the restore dialog reports its failure here (catalog §5).</summary>
    public string? LastError { get => lastError; set => Set(ref lastError, value); }

    public bool NeedsClaudeRestart { get => needsClaudeRestart; private set => Set(ref needsClaudeRestart, value); }

    /// <summary>True when the last apply threw; keeps a retry affordance visible even after Reload refreshes LastError.</summary>
    public bool ApplyRetryNeeded { get => applyRetryNeeded; private set => Set(ref applyRetryNeeded, value); }

    /// <summary>mcpServers as last read from / written to Claude's file, for dirty tracking.</summary>
    public IReadOnlyDictionary<string, JsonValue> AppliedServers { get => appliedServers; private set => Set(ref appliedServers, value); }

    public ConfigService Service { get => service; private set => Set(ref service, value); }

    public bool IsDirty => !DictionaryEquality.Equal(Store.EnabledServers, AppliedServers);

    public IReadOnlyList<string> SortedNames => Store.Mcps.Keys.Order(StringComparer.Ordinal).ToList();

    public IReadOnlyList<string> ProfileNames => Store.Profiles.Keys.Order(StringComparer.Ordinal).ToList();

    public string ActiveProfile => Store.ActiveProfile;

    /// <summary>Catalog §2.2 header subtitle.</summary>
    public string HeaderSubtitle
    {
        get
        {
            var total = Store.Mcps.Count;
            return total == 0 ? NoConnectorsSubtitle : $"{Store.EnabledServers.Count} of {total} enabled";
        }
    }

    // MARK: watchers (catalog §1.4, §1.5; spec §6.3)

    /// <summary>Replaces both watchers. Re-run on every repoint.</summary>
    private void ArmWatchers()
    {
        watcher?.Dispose();
        storeWatcher?.Dispose();
        watcher = new FileWatcher(Service.Paths.ClaudeConfigPath, () => RunWatcherCallback(() => Reload()), host.Marshal);
        watcher.Start();
        storeWatcher = new FileWatcher(Service.Paths.MasterStorePath, () => RunWatcherCallback(AdoptExternalStoreChange), host.Marshal);
        storeWatcher.Start();
    }

    private static void ReArm(FileWatcher? watcher)
    {
        if (watcher is { IsArmed: false })
        {
            watcher.Start();
        }
    }

    /// <summary>
    /// The store's mtime changed on disk. Classify before adopting: our own PersistStore writes echo
    /// through this watcher (skip — memory already matches), a sync tool's mid-write partial parses as
    /// garbage (wait for the completed write to fire again — adopting it would rebuild the store from
    /// the local Claude config and clobber the synced list), and only a decodable store that differs
    /// from memory is a genuine outside edit to adopt and announce.
    /// </summary>
    private void AdoptExternalStoreChange()
    {
        var storePath = Service.Paths.MasterStorePath;
        if (!File.Exists(storePath))
        {
            // Deleted store file: Reload's self-heal re-persists the in-memory truth; nothing external to adopt or announce.
            Reload(ReloadTrigger.QuietStoreAdoption);
            return;
        }
        var onDisk = MasterStoreIO.Read(storePath);
        if (onDisk is null || onDisk.Equals(Store))
        {
            return;
        }
        Reload(ReloadTrigger.ExternalStoreAdoption);
    }

    /// <summary>
    /// Runs a watcher-triggered callback with a catch-all around its ENTIRE body, not only Reload's
    /// own narrower catch filter (Task 3 review). This also covers <see cref="AdoptExternalStoreChange"/>'s
    /// File.Exists/MasterStoreIO.Read/equality work, which runs before any Reload is reached (Task 5
    /// review of Task 4's controller) — without this, an exception there would escape through a
    /// marshalled FileWatcher callback and take the whole app down instead of showing a banner.
    /// Also re-arms both watchers here: an exception means the ReArm at the bottom of Reload never
    /// ran, so without this a watcher could stay disarmed until the next reload. Not used for a Reload
    /// called directly from a public method — those are already on the caller's stack, not a marshalled
    /// callback, so their exceptions propagate as before.
    /// </summary>
    private void RunWatcherCallback(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            LastError = Friendly(ex);
            try
            {
                RefreshRestartState();
            }
            catch (Exception)
            {
                // RefreshRestartState reaches into IClaudeProcess; a throw there must not
                // escape the guard and leave the watchers disarmed / the banner unset below.
            }
            ReArm(watcher);
            ReArm(storeWatcher);
            RaiseAll();
        }
    }

    // MARK: repointing (catalog §1.12) and restore (catalog §1.13)

    /// <summary>
    /// Repoints the master store to a new directory (or back to the default when <paramref name="dir"/>
    /// is null). Seeds the new location from the current store if it has no mcps.json yet, rebuilds the
    /// service, re-arms both watchers, and adopts the store quietly (a pre-existing store is authoritative).
    /// </summary>
    public void RepointStore(string? dir)
    {
        var previousStorePath = Service.Paths.MasterStorePath;
        settings.MasterStoreDir = dir;
        var rebuilt = MakeService(settings, paths);
        var newStorePath = rebuilt.Paths.MasterStorePath;
        if (!File.Exists(newStorePath) && File.Exists(previousStorePath))
        {
            try
            {
                Directory.CreateDirectory(rebuilt.Paths.StoreDir);
                File.Copy(previousStorePath, newStorePath);
                OwnerOnlyAcl.TryApply(rebuilt.Paths.StoreDir);
                OwnerOnlyAcl.TryApply(newStorePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // like the Mac's try?: adopt whatever is (or isn't) there
            }
        }
        Service = rebuilt;
        ArmWatchers();
        Reload(ReloadTrigger.QuietStoreAdoption);
    }

    /// <summary>
    /// Settings ▸ Claude ▸ config path. A different Claude file is a fresh start against that file:
    /// first-launch import semantics (null baseline), no notifications, the store still wins any divergence.
    /// </summary>
    public void RepointClaudeConfig(string? path)
    {
        settings.ClaudeConfigPath = path;
        Service = MakeService(settings, paths);
        hasLoadedOnce = false;
        AppliedServers = EmptyServers;
        ArmWatchers();
        Reload();
    }

    /// <summary>Rebuilds the service from current settings (backup retention) without moving the store or resetting the baseline.</summary>
    public void RefreshServiceSettings()
    {
        Service = MakeService(settings, paths);
        ArmWatchers();
        RaiseAll();
    }

    /// <summary>
    /// Restores Claude's config from a backup and syncs the reconciliation baseline to the restored
    /// contents BEFORE reloading, so the app's own restore isn't misread as an external change or a re-add.
    /// Throws on a bad backup; nothing is written then.
    /// </summary>
    public void RestoreClaudeConfig(string backupPath)
    {
        var servers = Service.RestoreClaudeConfig(backupPath, Store);
        AppliedServers = servers;
        hasLoadedOnce = true;
        settings.LastApplyDate = host.UtcNow();
        // ConfigService already merged and persisted the store; a quiet adoption takes it as-is.
        Reload(ReloadTrigger.QuietStoreAdoption);
    }

    // MARK: service construction (catalog §1.3, spec §5.6)

    public static ConfigService MakeService(ISettings settings, PathContext paths)
    {
        var resolved = AppPathsResolver.Resolve(
            paths.Environment,
            new PathOverrides(settings.ClaudeConfigPath, settings.MasterStoreDir),
            paths.Folders,
            paths.Probe);
        return new ConfigService(resolved, settings.BackupKeepCount);
    }

    // MARK: reload (catalog §1.7 + §1.8)

    public void Reload(ReloadTrigger trigger = ReloadTrigger.Routine)
    {
        try
        {
            // Capture "before" state for the notification rules below, BEFORE any state is overwritten.
            var wasLoaded = hasLoadedOnce;
            var previousApplied = AppliedServers;
            var previousStoreMcps = new Dictionary<string, McpEntry>(Store.Mcps, StringComparer.Ordinal);

            // The store file vanished mid-session (deleted store dir, sync eviction). The in-memory
            // store is the source of truth — persist it back rather than loading an empty store and
            // regenerating Claude's config down to nothing.
            if (wasLoaded && !File.Exists(Service.Paths.MasterStorePath))
            {
                Service.SaveStore(Store);
            }

            var result = Service.LoadAndReconcile(
                baseline: hasLoadedOnce ? AppliedServers : null,
                storeAuthoritative: trigger != ReloadTrigger.Routine);
            Store = result.Store;
            var claudeConfigChangedExternally = false;
            if (result.ClaudeServers is { } servers)
            {
                claudeConfigChangedExternally = wasLoaded && !DictionaryEquality.Equal(servers, previousApplied);
                AppliedServers = servers;
                hasLoadedOnce = true;
            }
            // Store-side external change that needs no regeneration (e.g. a synced edit to a
            // disabled connector) still deserves a heads-up on the routine path.
            var storeChangedExternally =
                trigger == ReloadTrigger.Routine
                && wasLoaded
                && !DictionaryEquality.Equal(result.Store.Mcps, previousStoreMcps)
                && !claudeConfigChangedExternally;
            LastError = result.Notes.Count > 0 ? result.Notes[0] : null;
            if (!IsDirty)
            {
                ApplyRetryNeeded = false;
            }

            // The store is the source of truth; Claude's config is downstream. Any divergence from
            // the render is regenerated away, arming the same Restart Required footer as a user-made
            // change. No loop: the regenerating write satisfies the watcher-triggered follow-up reload.
            var regenerated = false;
            var regenerationFailed = false;
            if (result.ClaudeServers is { } fileServers && !DictionaryEquality.Equal(fileServers, Store.EnabledServers))
            {
                var alreadyFailing = ApplyRetryNeeded;
                PerformApply();
                regenerated = !ApplyRetryNeeded;
                // Notify a failure only on the transition into it — retry reloads (every flyout open) must not re-post it.
                regenerationFailed = ApplyRetryNeeded && !alreadyFailing;
            }

            // Fire notifications AFTER all state above has been assigned, never on first load or for
            // quiet adoptions. At most one per reload (catalog §1.8).
            if (regenerated && wasLoaded && trigger == ReloadTrigger.Routine && claudeConfigChangedExternally)
            {
                Notify(ClaudeConfigRegeneratedBody);
            }
            else if (regenerated && wasLoaded && trigger == ReloadTrigger.ExternalStoreAdoption && NeedsClaudeRestart)
            {
                // A remote (synced) connector-list change landed while nobody was looking and Claude
                // is running on the older config — the one restart-pending case with no in-app feedback.
                Notify(ConnectorListChangedRestartBody, Notifications.RestartCategory);
            }
            else if (regenerationFailed && wasLoaded && trigger != ReloadTrigger.QuietStoreAdoption)
            {
                Notify(RegenerationFailedBody);
            }
            else if (claudeConfigChangedExternally)
            {
                Notify(ClaudeConfigChangedBody);
            }
            else if (storeChangedExternally)
            {
                Notify(StoreChangedBody);
            }
            RefreshRestartState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ClaudeConfigException or JsonException or FormatException)
        {
            LastError = Friendly(ex);
            RefreshRestartState();
        }
        // Only when arming previously failed — the parent directory did not exist, or
        // FileWatcher.HandleError disarmed itself because the directory was deleted.
        // Never a blanket re-arm: a flyout open reloads (catalog §2.1), and tearing two
        // FileSystemWatchers down and rebuilding them each time would re-baseline the
        // last-seen write time and open a gap where an external write is simply lost.
        // (FileWatcher.Start() is itself a no-op while armed; the IsArmed test states the
        // intent at the call site rather than relying on that.)
        ReArm(watcher);
        ReArm(storeWatcher);
        RaiseAll();
    }

    // MARK: apply / persist (catalog §1.10)

    private void PerformApply()
    {
        try
        {
            Service.Apply(Store);
            AppliedServers = Store.EnabledServers;
            settings.LastApplyDate = host.UtcNow();   // ISettings setters never throw, so this cannot turn a good apply into a failed one
            RefreshRestartState();
            LastError = null;
            ApplyRetryNeeded = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ClaudeConfigException)
        {
            LastError = Friendly(ex);
            ApplyRetryNeeded = true;
        }
    }

    private void PersistStore()
    {
        try
        {
            Service.SaveStore(Store);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = Friendly(ex);
        }
    }

    /// <summary>Toggles take effect immediately; the Restart Required button is the only follow-up step.</summary>
    public void SetEnabled(string name, bool on)
    {
        if (Store.Mcps.TryGetValue(name, out var entry))
        {
            Store.Mcps[name] = entry with { Enabled = on };
        }
        PersistStore();
        PerformApply();
        RaiseAll();
    }

    /// <summary>The flyout's retry button: unconditional.</summary>
    public void Apply()
    {
        PerformApply();
        RaiseAll();
    }

    /// <summary>Editor-window flow: saving there is a deliberate final act, so apply immediately — but only if something changed.</summary>
    public void ApplyInteractively()
    {
        if (!IsDirty)
        {
            return;
        }
        PerformApply();
        RaiseAll();
    }

    /// <summary>Validates and saves an entry. Returns an error message, or null on success.</summary>
    public string? Upsert(string name, McpEntry entry, string? renamedFrom)
    {
        var trimmed = name.TrimSpaces();
        if (trimmed.Length == 0)
        {
            return "Name must not be empty.";
        }
        if (trimmed != renamedFrom && Store.Mcps.ContainsKey(trimmed))
        {
            return $"A connector named “{trimmed}” already exists.";
        }
        if (renamedFrom is { } old && old != trimmed)
        {
            Store.Mcps.Remove(old);
        }
        Store.Mcps[trimmed] = entry;
        PersistStore();
        RaiseAll();
        return null;
    }

    /// <summary>Removes and persists; the caller applies (catalog §3.10 does both in one turn).</summary>
    public void Remove(string name)
    {
        Store.Mcps.Remove(name);
        PersistStore();
        RaiseAll();
    }

    // MARK: restart-required derivation (catalog §1.11, spec §6.2)

    /// <summary>Claude needs a restart iff it's running on a config older than our last write; self-clears however Claude gets restarted.</summary>
    public void RefreshRestartState()
    {
        var lastApply = settings.LastApplyDate;
        var launched = claude.LaunchTime;
        NeedsClaudeRestart = lastApply is { } applied
            && claude.IsRunning
            && launched is { } launchTime
            && launchTime.ToUniversalTime() < applied.ToUniversalTime();
    }

    // MARK: quit (catalog §1.16)

    /// <summary>Raised when the app should terminate (after the optional confirmation).</summary>
    public event Action? QuitRequested;

    public void QuitApp()
    {
        if (settings.ConfirmBeforeQuit && !Dialogs.Confirm(QuitMessage, null, QuitButton))
        {
            return;
        }
        QuitRequested?.Invoke();
    }

    // MARK: restart Claude (catalog §1.17)

    /// <summary>The in-app button: confirm (unless disabled), then restart.</summary>
    public Task RestartClaudeAsync()
    {
        if (settings.ConfirmBeforeRestart && !Dialogs.Confirm(RestartMessage, RestartInformative, RestartButton))
        {
            return Task.CompletedTask;
        }
        return PerformRestartClaudeAsync();
    }

    /// <summary>Restart with no confirmation: after the in-app confirm, or from the toast action where the click is the confirmation.</summary>
    public async Task PerformRestartClaudeAsync()
    {
        string? message;
        try
        {
            message = await claude.RestartAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // IClaudeProcess documents that a cancelled 15 s wait throws rather than
            // returning a message. Nothing here passes a token, so this is defence only.
            message = null;
        }
        catch (Exception ex)
        {
            // The toast action calls this fire-and-forget, so install probing / process
            // enumeration outside RestartAsync's own launch guard must not fault silently:
            // the marshalled completion below always runs with either null or a message,
            // same guarantee as the Mac's ClaudeRestarter completion handler.
            message = Friendly(ex);
        }
        host.Marshal(() =>
        {
            LastError = message;   // null on success clears any prior banner
            RefreshRestartState();
            host.Delay(RestartRecheckDelay, () =>
            {
                if (disposed)
                {
                    return;
                }
                RefreshRestartState();
                RaiseAll();
            });
            host.Delay(RestartRelaunchCheck, () =>
            {
                if (disposed)
                {
                    return;
                }
                RefreshRestartState();
                if (!claude.IsRunning && LastError is null)
                {
                    LastError = RelaunchFailedMessage;
                }
                RaiseAll();
            });
            RaiseAll();
        });
    }

    private void OnRestartActionActivated()
    {
        if (!NeedsClaudeRestart)
        {
            return;
        }
        _ = PerformRestartClaudeAsync();
    }

    // MARK: profiles (catalog §1.18)

    /// <summary>Switching profiles applies immediately, like every other change. An unknown name is silently ignored.</summary>
    public void SwitchProfile(string name)
    {
        if (Store.SwitchProfile(name) is not null)
        {
            return;
        }
        PersistStore();
        PerformApply();
        RaiseAll();
    }

    public void NewProfile()
    {
        if (Dialogs.PromptForName(NewProfileTitle, "") is not { } name)
        {
            return;
        }
        FinishProfileChange(Store.AddProfile(name, copyingCurrent: true));
    }

    public void RenameProfile()
    {
        if (Dialogs.PromptForName(RenameProfileTitle, Store.ActiveProfile) is not { } name)
        {
            return;
        }
        FinishProfileChange(Store.RenameActiveProfile(name));
    }

    public void DeleteProfile()
    {
        if (!Dialogs.Confirm($"Delete Profile “{Store.ActiveProfile}”?", DeleteProfileInformative, DeleteButton, destructive: true))
        {
            return;
        }
        FinishProfileChange(Store.DeleteActiveProfile());
    }

    private void FinishProfileChange(string? error)
    {
        if (error is null)
        {
            PersistStore();
            PerformApply();
        }
        else
        {
            LastError = error;
        }
        RaiseAll();
    }

    // MARK: notifications (catalog §1.8)

    private void Notify(string body, string? category = null)
    {
        if (!settings.NotifyExternalChanges)
        {
            return;
        }
        notifier.Notify(Notifications.Title, body, category);
    }

    /// <summary>Catalog §1.10 friendly(): the malformed-config case gets the guided message; everything else its own text.</summary>
    public static string Friendly(Exception error) => error is ClaudeConfigException malformed
        ? $"Claude's config file is not valid JSON ({malformed.Detail}). Nothing was written. Use Backups ▸ Restore… to recover it."
        : error.Message;

    public void Dispose()
    {
        disposed = true;
        notifier.RestartActionActivated -= OnRestartActionActivated;
        watcher?.Dispose();
        storeWatcher?.Dispose();
        watcher = null;
        storeWatcher = null;
    }
}
