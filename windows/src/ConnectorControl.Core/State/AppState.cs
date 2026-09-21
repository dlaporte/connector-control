using System.Text.Json;
using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.State;

/// <summary>
/// The Mac AppState, on Windows. UI-thread-only; everything that
/// arrives from another thread comes through <see cref="AppHost.Marshal"/>.
/// Init sequence: resolve the service, one-time ACL sweep, route the toast
/// Restart action, reload, arm watchers.
/// </summary>
public sealed class AppState : ObservableObject, IDisposable
{
    public const string NoConnectorsSubtitle = "No connectors configured";
    public const string ClaudeConfigRegeneratedBody = "Claude's config was changed outside Connector Control — regenerated from your connector list. Restart Claude to pick it up.";
    public const string RegenerationFailedBody = "The connector configuration changed, but Claude's config could not be updated — open Connector Control to retry.";
    public const string ClaudeConfigChangedBody = "Claude's config changed outside Connector Control.";
    public const string StoreChangedBody = "The connector list changed outside Connector Control — review it before your next change is applied.";
    public const string QuitMessage = "Quit Connector Control?";
    public const string QuitButton = "Quit";
    public const string RestartMessage = "Restart Claude Desktop now?";
    public const string RestartInformative = "Any in-progress Claude conversation will be interrupted.";
    public const string RestartButton = "Restart";
    public const string NewCollectionTitle = "New Collection";
    public const string RenameCollectionTitle = "Rename Collection";
    public const string DeleteCollectionInformative = "Its connector list is removed; backups keep prior states.";
    public const string DeleteButton = "Delete";
    /// <summary>Coined here, not taken from the Mac catalog: on macOS a relaunch cannot fail silently.</summary>
    public const string RelaunchFailedMessage = "Claude didn’t come back after the restart. Start Claude yourself, then try again.";
    public const string NameEmptyError = "Name must not be empty.";
    public const string LastLocalCollectionError = "The last local collection can\u2019t be deleted.";
    public const string LocateCaution = "Locate the collection file to resolve paths.";
    public static string DuplicateNameError(string name) => $"A connector named “{name}” already exists.";
    public static string DeleteCollectionMessage(string collection) => $"Delete Collection “{collection}”?";
    public static string MalformedConfigMessage(string detail) =>
        $"Claude's config file is not valid JSON ({detail}). Nothing was written. Use Backups ▸ Restore… to recover it.";
    public static string NeedsValueCaution(string names) => $"needs your value: {names}";
    public static string CollectionUpdateBanner(string collection, string summary) => $"{collection} changed at its source: {summary}.";
    /// <summary>"this PC" is the platform-forced half of this sentence; the Mac mirror says "this Mac".</summary>
    public static string CollectionLocateBanner(string collection) => $"{collection}'s file isn\u2019t on this PC yet.";
    public static string CollectionPublishFailedBanner(string collection, string folder, string reason) => $"Couldn\u2019t publish {collection} to {folder}: {reason}";
    public static string CollectionUpdateNotificationBody(string collection, string summary) => $"{collection} changed at its source: {summary}. Review it in Connector Control.";
    /// <summary>Claude's launch time is re-read 3 s after the restart completes.</summary>
    public static readonly TimeSpan RestartRecheckDelay = TimeSpan.FromSeconds(3);
    /// <summary>
    /// The probe that a relaunched Claude is up within 20 s. RestartAsync reports null
    /// even when the AUMID launch silently did nothing (explorer.exe succeeds for any
    /// AUMID), so this second look is the only place that failure becomes visible.
    /// </summary>
    public static readonly TimeSpan RestartRelaunchCheck = TimeSpan.FromSeconds(20);

    private static readonly IReadOnlyDictionary<string, JsonValue> EmptyServers = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, CollectionDiff> EmptyPending = new Dictionary<string, CollectionDiff>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> EmptySourceErrors = new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly ISettings settings;
    private readonly IClaudeProcess claude;
    private readonly INotifier notifier;
    /// <summary>The prompts AppState itself raises (quit, restart, collections); editor/settings windows own their own.</summary>
    private readonly IDialogs dialogs;
    private readonly PathContext paths;
    private readonly AppHost host;
    private readonly IToolProbe toolProbe;
    private readonly Dictionary<Tool, ToolStatus> toolStatuses = [];
    private readonly HashSet<Tool> toolsInFlight = [];

    private MasterStore store = MasterStore.Empty();
    private string? lastError;
    private bool needsClaudeRestart;
    private bool applyRetryNeeded;
    private IReadOnlyDictionary<string, JsonValue> appliedServers = EmptyServers;
    private ConfigService service;
    private bool hasLoadedOnce;
    private CollectionsFile collectionsFile = new([]);
    private CollectionsLocalCache collectionsCache = new([], []);
    private IReadOnlyDictionary<string, CollectionDiff> pendingUpdates = EmptyPending;
    private IReadOnlyDictionary<string, string> sourceErrors = EmptySourceErrors;
    private CollectionPublishError? publishError;
    private FileWatcher? watcher;
    private FileWatcher? storeWatcher;
    private bool disposed;

    /// <summary>Test probe: both watchers are live. Should be true after every reload.</summary>
    internal bool WatchersArmed => watcher is { IsArmed: true } && storeWatcher is { IsArmed: true };

    /// <summary>Test probe: reference identity of both watchers, so a test can confirm a
    /// redundant reload leaves an already-armed watcher alone instead of tearing it down
    /// and re-baselining its mtime.</summary>
    internal (object? Claude, object? Store) WatcherIdentities => (watcher, storeWatcher);

    public AppState(ISettings settings, IClaudeProcess claude, INotifier notifier, IDialogs dialogs, PathContext paths, AppHost host, IToolProbe tools)
    {
        this.settings = settings;
        this.claude = claude;
        this.notifier = notifier;
        this.dialogs = dialogs;
        this.paths = paths;
        this.host = host;
        toolProbe = tools;
        service = MakeService(settings, this.paths);
        // Sweep the RESOLVED paths (a repointed store lives outside the default dir).
        PermissionsSweep.RunOnce(settings, service.Paths);
        // The toast's Restart Claude button routes back here. Skipping the confirm-before-restart
        // dialog is deliberate: clicking the explicit action IS the confirmation. Stale-click guard:
        // an old toast must not restart a Claude that already picked up the config.
        notifier.RestartActionActivated += OnRestartActionActivated;
        Reload();
        ArmWatchers();
    }

    // MARK: published state

    public MasterStore Store { get => store; private set => Set(ref store, value); }

    /// <summary>Settable: the restore dialog reports its failure here.</summary>
    public string? LastError { get => lastError; set => Set(ref lastError, value); }

    private bool storeNotPrivate;
    /// <summary>
    /// Windows: the last master-list save could not make mcps.json owner-only — the folder refused
    /// the permission change — so the connector secrets in it are readable by whoever can read that
    /// folder. Cleared by the next save that succeeds. Always false off Windows.
    /// </summary>
    public bool StoreNotPrivate { get => storeNotPrivate; internal set => Set(ref storeNotPrivate, value); }

    public bool NeedsClaudeRestart { get => needsClaudeRestart; private set => Set(ref needsClaudeRestart, value); }

    /// <summary>True when the last apply threw; keeps a retry affordance visible even after Reload refreshes LastError.</summary>
    public bool ApplyRetryNeeded { get => applyRetryNeeded; private set => Set(ref applyRetryNeeded, value); }

    /// <summary>mcpServers as last read from / written to Claude's file, for dirty tracking.</summary>
    public IReadOnlyDictionary<string, JsonValue> AppliedServers { get => appliedServers; private set => Set(ref appliedServers, value); }

    public ConfigService Service { get => service; private set => Set(ref service, value); }

    /// <summary>The sidecar beside the master list, reconciled with the store on every load.</summary>
    public CollectionsFile CollectionsFile { get => collectionsFile; private set => Set(ref collectionsFile, value); }

    /// <summary>This machine's bindings, reconciled with the sidecar on every load.</summary>
    public CollectionsLocalCache CollectionsCache { get => collectionsCache; private set => Set(ref collectionsCache, value); }

    /// <summary>
    /// Synced collections whose source differs from what the store holds. Settable inside the
    /// assembly so the banner rules can be exercised without a source file behind them; the
    /// source watcher is what fills it in the app.
    /// </summary>
    public IReadOnlyDictionary<string, CollectionDiff> PendingUpdates { get => pendingUpdates; internal set => Set(ref pendingUpdates, value); }

    /// <summary>Collection → why its source could not be read, after repeated failures or a manual refresh.</summary>
    public IReadOnlyDictionary<string, string> SourceErrors { get => sourceErrors; internal set => Set(ref sourceErrors, value); }

    /// <summary>The last publish that failed, with the reason. Cleared by a write that succeeds.</summary>
    public CollectionPublishError? PublishError { get => publishError; internal set => Set(ref publishError, value); }

    public bool IsDirty => !DictionaryEquality.Equal(Store.EnabledServers, AppliedServers);

    public IReadOnlyList<string> SortedNames => Store.Mcps.Keys.Order(StringComparer.Ordinal).ToList();

    public IReadOnlyList<string> CollectionNames => Store.Collections.Keys.Order(StringComparer.Ordinal).ToList();

    public string ActiveCollection => Store.ActiveCollection;

    /// <summary>The header subtitle.</summary>
    public string HeaderSubtitle
    {
        get
        {
            var total = Store.Mcps.Count;
            return total == 0 ? NoConnectorsSubtitle : EnabledSubtitle(Store.EnabledCount, total);
        }
    }

    public static string EnabledSubtitle(int enabled, int total) => $"{enabled} of {total} enabled";

    // MARK: tools

    /// <summary>
    /// Which of the four launchers Claude Desktop can start: probed on demand — Settings ▸ Claude,
    /// the editor — and cached for the rest of the run. A tool absent here has not been probed yet.
    /// </summary>
    public IReadOnlyDictionary<Tool, ToolStatus> ToolStatuses => toolStatuses;

    /// <summary>
    /// Probes <paramref name="tools"/> (all four when null) off the UI thread and posts the
    /// results to the host for publication; a tool already in flight is not probed twice. The
    /// returned task completes once the probe batch has been posted, not once the results are
    /// applied — when every requested tool is already in flight, it returns already completed.
    /// </summary>
    public Task RefreshToolsAsync(IReadOnlyList<Tool>? tools = null)
    {
        var wanted = (tools ?? ToolInfo.All).Where(toolsInFlight.Add).ToArray();
        return wanted.Length == 0 ? Task.CompletedTask : ProbeToolsAsync(wanted);
    }

    private async Task ProbeToolsAsync(Tool[] wanted)
    {
        IReadOnlyDictionary<Tool, ToolStatus> results;
        try
        {
            results = await Task.Run(() => toolProbe.Probe(wanted)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // IToolProbe promises never to throw; if one does anyway, "Not found" beats a dead tray app.
            results = wanted.ToDictionary(t => t, _ => ToolStatus.NotFound);
        }
        // Everything below touches state the UI thread owns, so it is posted like every other
        // marshalled state callback (fire-and-post) rather than awaited: an exception raised inside
        // (e.g. by a throwing PropertyChanged handler) must surface as an unhandled dispatcher
        // exception on the UI thread, not get captured into this fire-and-forget task and swallowed.
        host.Marshal(() =>
        {
            foreach (var tool in wanted)
            {
                toolsInFlight.Remove(tool);
            }
            if (disposed)
            {
                return;
            }
            foreach (var (tool, status) in results)
            {
                toolStatuses[tool] = status;
            }
            Raise(nameof(ToolStatuses));
        });
    }

    // MARK: watchers

    /// <summary>Replaces both watchers. Re-run on every repoint.</summary>
    private void ArmWatchers()
    {
        watcher?.Dispose();
        storeWatcher?.Dispose();
        watcher = new FileWatcher(Service.Paths.ClaudeConfigPath, host.Marshal, () => RunWatcherCallback(() => Reload()));
        watcher.Start();
        storeWatcher = new FileWatcher(Service.Paths.MasterStorePath, host.Marshal, () => RunWatcherCallback(AdoptExternalStoreChange));
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
    /// Runs a watcher-triggered callback with a catch-all around its ENTIRE body, including the
    /// part outside Reload's own catch-all. This also covers <see cref="AdoptExternalStoreChange"/>'s
    /// File.Exists/MasterStoreIO.Read/equality work, which runs before any Reload is reached —
    /// without this, an exception there would escape through a
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

    // MARK: repointing and restore

    /// <summary>
    /// Repoints the master store to a new directory (or back to the default when <paramref name="dir"/>
    /// is null). Seeds the new location from the current store if it has no mcps.json yet, rebuilds the
    /// service, re-arms both watchers, and adopts the store quietly (a pre-existing store is authoritative).
    /// </summary>
    public void RepointStore(string? dir)
    {
        var previousDir = settings.MasterStoreDir;
        var previousStorePath = Service.Paths.MasterStorePath;
        settings.MasterStoreDir = dir;
        var rebuilt = MakeService(settings, paths);
        var newStorePath = rebuilt.Paths.MasterStorePath;
        if (!File.Exists(newStorePath) && File.Exists(previousStorePath))
        {
            try
            {
                // Exactly the Mac's seed: a directory this app creates is private from the start, one
                // the user chose is left as it is (the sweep's rule), and the copy is owner-only from
                // its create call.
                AtomicFile.Write(File.ReadAllBytes(previousStorePath), newStorePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The new location refused the seed copy. Switching to it now would leave it with
                // no mcps.json, and Reload would read that as an empty store and quietly wipe the
                // connector list — so the repoint is abandoned and the old location stays authoritative.
                settings.MasterStoreDir = previousDir;
                LastError = Friendly(ex);
                RaiseAll();
                return;
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
        settings.LastApplyDate = host.Now();
        // ConfigService already merged and persisted the store; a quiet adoption takes it as-is.
        Reload(ReloadTrigger.QuietStoreAdoption);
    }

    // MARK: service construction

    public static ConfigService MakeService(ISettings settings, PathContext paths)
    {
        var resolved = AppPaths.Resolve(
            paths.Environment,
            new PathOverrides(settings.ClaudeConfigPath, settings.MasterStoreDir),
            paths.Folders,
            paths.Probe);
        return new ConfigService(resolved, settings.BackupKeepCount);
    }

    // MARK: reload

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
            LoadCollections();
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
            // Every note, not just the first: with a corrupt store AND a malformed Claude config,
            // the second one is the actionable one (Backups ▸ Restore… is the way out).
            LastError = result.Notes.Count > 0 ? string.Join(" ", result.Notes) : null;
            if (!IsDirty)
            {
                ApplyRetryNeeded = false;
            }

            // The store is the source of truth; Claude's config is downstream. Any divergence from
            // the render is regenerated away, arming the same Restart Required footer as a user-made
            // change. No loop: the regenerating write satisfies the watcher-triggered follow-up reload.
            var enabled = Store.EnabledServers;
            var regenerated = false;
            var regenerationFailed = false;
            if (result.ClaudeServers is { } fileServers && !DictionaryEquality.Equal(fileServers, enabled))
            {
                var alreadyFailing = ApplyRetryNeeded;
                PerformApply();
                regenerated = !ApplyRetryNeeded;
                // Notify a failure only on the transition into it — retry reloads (every flyout open) must not re-post it.
                regenerationFailed = ApplyRetryNeeded && !alreadyFailing;
            }

            // Fire notifications AFTER all state above has been assigned, never on first load or for
            // quiet adoptions. At most one per reload.
            if (regenerated && wasLoaded && trigger == ReloadTrigger.Routine && claudeConfigChangedExternally)
            {
                Notify(ClaudeConfigRegeneratedBody);
            }
            else if (regenerated && wasLoaded && trigger == ReloadTrigger.ExternalStoreAdoption)
            {
                // A remote (synced) connector-list change landed while nobody was looking and has just
                // been written into Claude's config. Every mcpServers entry is a command Claude runs, so
                // this is announced every time, naming what changed: with Claude running on the older
                // config the toast offers the restart; with Claude not running there is no restart to
                // offer, but the user still learns what starts next launch.
                var delta = ServerDelta.Between(previousApplied, enabled);
                if (NeedsClaudeRestart)
                {
                    Notify(ConnectorListChangedBody(delta, restartRequired: true), Notifications.RestartCategory);
                }
                else
                {
                    Notify(ConnectorListChangedBody(delta, restartRequired: false));
                }
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
        catch (Exception ex)
        {
            LastError = Friendly(ex);
            try
            {
                RefreshRestartState();
            }
            catch (Exception)
            {
                // RefreshRestartState reaches into IClaudeProcess; a second throw here must not
                // escape this handler and turn a friendly banner into an unhandled exception.
            }
        }
        // Only when arming previously failed — the parent directory did not exist, or
        // FileWatcher.HandleError disarmed itself because the directory was deleted.
        // Never a blanket re-arm: a flyout open reloads, and tearing two
        // FileSystemWatchers down and rebuilding them each time would re-baseline the
        // last-seen write time and open a gap where an external write is simply lost.
        // (FileWatcher.Start() is itself a no-op while armed; the IsArmed test states the
        // intent at the call site rather than relying on that.)
        ReArm(watcher);
        ReArm(storeWatcher);
        RaiseAll();
    }

    // MARK: apply / persist

    private void PerformApply()
    {
        try
        {
            Service.Apply(Store);
            var enabled = Store.EnabledServers;
            AppliedServers = enabled;
            settings.LastApplyDate = host.Now();   // ISettings setters never throw, so this cannot turn a good apply into a failed one
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

    /// <summary>
    /// The master list first, then the sidecar beside it, then this machine's cache — the order a
    /// crash has to survive: a half-done write leaves the collection loading as an ordinary local
    /// one until the next save, and loses nothing the app cannot rebuild. One catch for the
    /// three: the first failure stops the chain, since a sidecar written against a master list
    /// that never landed would describe collections that do not exist.
    /// </summary>
    private void PersistStore()
    {
        try
        {
            StoreNotPrivate = !Service.SaveStore(Store).Protected;
            Service.SaveCollections(CollectionsFile);
            CollectionsCache.Save(Service.Paths.CollectionsCachePath);
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
            return NameEmptyError;
        }
        if (trimmed != renamedFrom && Store.Mcps.ContainsKey(trimmed))
        {
            return DuplicateNameError(trimmed);
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

    /// <summary>Removes and persists; the caller applies (both happen in one turn).</summary>
    public void Remove(string name)
    {
        Store.Mcps.Remove(name);
        PersistStore();
        RaiseAll();
    }

    // MARK: restart-required derivation

    /// <summary>Claude needs a restart iff it's running on a config older than our last write; self-clears however Claude gets restarted.</summary>
    public void RefreshRestartState()
    {
        var lastApply = settings.LastApplyDate;
        var snapshot = claude.Snapshot();   // one enumeration instead of separate IsRunning/LaunchDate reads
        NeedsClaudeRestart = lastApply is { } applied
            && snapshot.IsRunning
            && snapshot.LaunchDate is { } launchDate
            && launchDate.ToUniversalTime() < applied.ToUniversalTime();
    }

    // MARK: quit

    /// <summary>Raised when the app should terminate (after the optional confirmation).</summary>
    public event Action? QuitRequested;

    public void QuitApp()
    {
        if (settings.ConfirmBeforeQuit && !dialogs.Confirm(QuitMessage, null, QuitButton))
        {
            return;
        }
        QuitRequested?.Invoke();
    }

    // MARK: restart Claude

    /// <summary>The in-app button: confirm (unless disabled), then restart.</summary>
    public Task RestartClaudeAsync()
    {
        if (settings.ConfirmBeforeRestart && !dialogs.Confirm(RestartMessage, RestartInformative, RestartButton))
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
            // A restart that finishes after Dispose (the app quitting mid-restart) must not
            // resurrect state or schedule fresh delays on an object nothing owns any more.
            if (disposed)
            {
                return;
            }
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
                if (!claude.Snapshot().IsRunning && LastError is null)
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

    // MARK: collections

    /// <summary>Switching collections applies immediately, like every other change. An unknown name is silently ignored.</summary>
    public void SwitchCollection(string name)
    {
        if (Store.SwitchCollection(name) is not null)
        {
            return;
        }
        PersistStore();
        PerformApply();
        RaiseAll();
    }

    /// <summary>
    /// Copies the active collection under a new name and makes it active, as the chip menu's
    /// New Collection has always done. null on success, else the message to show.
    /// </summary>
    public string? CreateCollection(string name)
    {
        if (Store.AddCollection(name, copyingCurrent: true) is { } error)
        {
            return error;
        }
        PersistStore();
        PerformApply();
        RaiseAll();
        return null;
    }

    /// <summary>
    /// Renames a collection wherever its name is a key: the master list, the sidecar entry, this
    /// machine's bindings, and the derived state the banner reads. null on success.
    /// </summary>
    public string? RenameCollection(string name, string newName)
    {
        if (Store.RenameCollection(name, newName) is { } error)
        {
            return error;
        }
        var trimmed = newName.TrimSpaces();
        if (trimmed != name)
        {
            CollectionsFile = new CollectionsFile(Moved(CollectionsFile.Collections, name, trimmed));
            CollectionsCache = new CollectionsLocalCache(
                Moved(CollectionsCache.Synced, name, trimmed), Moved(CollectionsCache.Published, name, trimmed));
            PendingUpdates = Moved(PendingUpdates, name, trimmed);
            SourceErrors = Moved(SourceErrors, name, trimmed);
            if (PublishError is { } failure && failure.Collection == name)
            {
                PublishError = failure with { Collection = trimmed };
            }
        }
        PersistStore();
        PerformApply();
        RaiseAll();
        return null;
    }

    /// <summary>
    /// Deletes a collection and everything keyed by its name. A synced collection's source file
    /// is never touched — only this machine's binding to it goes. null on success.
    /// </summary>
    public string? DeleteCollection(string name)
    {
        // There must always be somewhere to add a connector, and only a local collection takes
        // one — so the last local collection stays even when synced ones remain beside it. The
        // store still owns "no collection by that name": a name it does not have is not the last
        // anything, and its own message is the one to show.
        if (Store.Collections.ContainsKey(name) && KindOf(name) == CollectionKind.Local && LocalCollectionNames.Count <= 1)
        {
            return LastLocalCollectionError;
        }
        if (Store.DeleteCollection(name) is { } error)
        {
            return error;
        }
        CollectionsFile = new CollectionsFile(Without(CollectionsFile.Collections, name));
        CollectionsCache = new CollectionsLocalCache(
            Without(CollectionsCache.Synced, name), Without(CollectionsCache.Published, name));
        PendingUpdates = Without(PendingUpdates, name);
        SourceErrors = Without(SourceErrors, name);
        if (PublishError is { } failure && failure.Collection == name)
        {
            PublishError = null;
        }
        PersistStore();
        PerformApply();
        RaiseAll();
        return null;
    }

    private static Dictionary<string, TValue> Moved<TValue>(IReadOnlyDictionary<string, TValue> source, string name, string newName)
    {
        var copy = new Dictionary<string, TValue>(source, StringComparer.Ordinal);
        if (copy.Remove(name, out var value))
        {
            copy[newName] = value;
        }
        return copy;
    }

    private static Dictionary<string, TValue> Without<TValue>(IReadOnlyDictionary<string, TValue> source, string name)
    {
        var copy = new Dictionary<string, TValue>(source, StringComparer.Ordinal);
        copy.Remove(name);
        return copy;
    }

    // MARK: collection kinds and bindings

    public CollectionKind KindOf(string collection) => CollectionsFile.KindOf(collection);

    public bool IsSynced(string collection) => KindOf(collection) == CollectionKind.Synced;

    public bool IsPublished(string collection) =>
        CollectionsFile.Collections.TryGetValue(collection, out var entry) && entry.Publish is not null;

    public CollectionsLocalCache.SyncedBinding? SourceBinding(string collection) =>
        CollectionsCache.Synced.TryGetValue(collection, out var binding) ? binding : null;

    public bool ActiveCollectionIsSynced => IsSynced(ActiveCollection);

    public IReadOnlyList<string> LocalCollectionNames =>
        CollectionNames.Where(n => KindOf(n) == CollectionKind.Local).ToList();

    /// <summary>What the last Apply asked the user to fill in for one connector, by marker name.</summary>
    public IReadOnlyDictionary<string, CollectionsFile.Need> Needs(string connector, string collection) =>
        CollectionsFile.Collections.TryGetValue(collection, out var entry)
            && entry.Needs.TryGetValue(connector, out var needs)
            ? needs
            : EmptyNeeds;

    private static readonly IReadOnlyDictionary<string, CollectionsFile.Need> EmptyNeeds =
        new Dictionary<string, CollectionsFile.Need>(StringComparer.Ordinal);

    /// <summary>
    /// The collections-level caution for one row, distinct from the tool caution the launcher
    /// probe produces. The markers in the stored config are the source of truth, not the
    /// sidecar's needs: a detached collection keeps its unfilled markers after the needs are
    /// gone, and the row must still say so.
    ///
    /// The "authored on &lt;platform&gt;" caution needs the source document's launcher platform,
    /// which only arrives once a bound source is rendered; it joins this list there.
    /// </summary>
    public string? ConnectorCaution(string connector, string collection)
    {
        if (!Store.Collections.TryGetValue(collection, out var held) || !held.Mcps.TryGetValue(connector, out var entry))
        {
            return null;
        }
        // Ordered by first appearance and de-duplicated across leaves, so the sentence is stable
        // between two reads of the same config.
        var unfilled = Placeholder.MarkersIn(entry.Config).SelectMany(m => m.Names).Distinct(StringComparer.Ordinal).ToList();
        if (unfilled.Count > 0)
        {
            return NeedsValueCaution(string.Join(", ", unfilled));
        }
        if (Placeholder.UsesDirectoryToken(entry.Config) && IsSynced(collection) && SourceBinding(collection)?.Path is null)
        {
            return LocateCaution;
        }
        return null;
    }

    // MARK: collection banner

    /// <summary>
    /// The one banner the collection slot shows. A failed publish outranks everything: it is the
    /// only one where something the user asked for did not happen. Then the active collection's
    /// news before any other collection's, since that is the list in front of them.
    /// </summary>
    public CollectionBanner? CollectionBanner
    {
        get
        {
            if (PublishError is { } failure)
            {
                return new CollectionBanner.PublishFailed(failure.Collection, failure.Message);
            }
            if (PendingUpdates.TryGetValue(ActiveCollection, out var activeDiff))
            {
                return new CollectionBanner.UpdateAvailable(ActiveCollection, activeDiff.Summary());
            }
            if (PendingUpdates.Keys.Order(StringComparer.Ordinal).FirstOrDefault() is { } pending)
            {
                return new CollectionBanner.UpdateAvailable(pending, PendingUpdates[pending].Summary());
            }
            if (UnlocatedFileName(ActiveCollection) is { } activeFile)
            {
                return new CollectionBanner.Locate(ActiveCollection, activeFile);
            }
            foreach (var name in CollectionsFile.Collections.Keys.Order(StringComparer.Ordinal))
            {
                if (UnlocatedFileName(name) is { } fileName)
                {
                    return new CollectionBanner.Locate(name, fileName);
                }
            }
            return null;
        }
    }

    /// <summary>
    /// The document a synced collection is waiting to be pointed at, or null when there is
    /// nothing to ask for: the collection is local, already bound, or the sidecar never recorded
    /// a file name to name in the request.
    /// </summary>
    private string? UnlocatedFileName(string collection) =>
        CollectionsFile.Collections.TryGetValue(collection, out var entry)
            && entry.Kind == CollectionKind.Synced
            && SourceBinding(collection)?.Path is null
            ? entry.FileName
            : null;

    // MARK: collections load

    /// <summary>
    /// The sidecar and the cache follow the store on every load: the master list decides which
    /// collections exist, the sidecar annotates them, and the cache binds what this machine has
    /// found. Runs after the store is assigned, so both reconcile against the list just loaded.
    /// </summary>
    private void LoadCollections()
    {
        var loaded = Service.LoadCollections();
        // An unreadable sidecar loads as empty (see CollectionsFile.Load). Reconciling the cache
        // against that would drop every binding on this machine over a file a sync tool is
        // halfway through writing, so a sidecar that exists yet loads as empty is left alone and
        // whatever is already in memory stands. A genuinely empty sidecar reads the same way and
        // costs only a prune deferred to the next load.
        if (loaded.Collections.Count == 0 && File.Exists(Service.Paths.CollectionsFilePath))
        {
            return;
        }
        CollectionsFile = loaded.Reconciled(Store);
        CollectionsCache = CollectionsLocalCache.Load(Service.Paths.CollectionsCachePath).Reconciled(CollectionsFile);
        BindSourcesBesideTheStore();
    }

    /// <summary>
    /// The last load rule: a synced collection nothing has bound on this machine tries the path
    /// the sidecar recorded relative to the store dir. A collection that travels inside the
    /// store's own folder is found there without ever asking; anything else keeps the Locate
    /// banner. A binding found this way is persisted at once, so the next launch starts bound.
    /// </summary>
    private void BindSourcesBesideTheStore()
    {
        var synced = new Dictionary<string, CollectionsLocalCache.SyncedBinding>(CollectionsCache.Synced, StringComparer.Ordinal);
        var bound = false;
        foreach (var (name, entry) in CollectionsFile.Collections)
        {
            if (entry.Kind != CollectionKind.Synced || entry.RelativeToStore is not { } relative)
            {
                continue;
            }
            synced.TryGetValue(name, out var existing);
            if (existing?.Path is not null)
            {
                continue;
            }
            var path = Path.GetFullPath(Path.Combine(Service.Paths.StoreDir, relative));
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                continue;
            }
            synced[name] = new CollectionsLocalCache.SyncedBinding(path, ContentHash.Sha256(data), existing?.Excluded);
            bound = true;
        }
        if (!bound)
        {
            return;
        }
        CollectionsCache = new CollectionsLocalCache(synced, CollectionsCache.Published);
        try
        {
            CollectionsCache.Save(Service.Paths.CollectionsCachePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = Friendly(ex);
        }
    }

    // MARK: notifications

    private void Notify(string body, string? category = null)
    {
        if (!settings.NotifyExternalChanges)
        {
            return;
        }
        notifier.Notify(Notifications.Title, body, category);
    }

    /// <summary>A synced connector-list change was adopted and written into Claude's config: say what it runs now.</summary>
    public static string ConnectorListChangedBody(ServerDelta delta, bool restartRequired)
    {
        var what = delta.IsEmpty ? "was regenerated" : "now " + delta.Summary();
        var then = restartRequired ? "Restart Claude to pick it up." : "Claude will use it the next time it starts.";
        return $"The connector list changed outside Connector Control — Claude's config {what}. {then}";
    }

    /// <summary>friendly(): the malformed-config case gets the guided message; everything else its own text.</summary>
    public static string Friendly(Exception error) => error is ClaudeConfigException malformed
        ? MalformedConfigMessage(malformed.Detail)
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
