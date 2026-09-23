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
    /// <summary>"this PC" is the platform-forced half of this sentence; the Mac mirror says "this Mac".</summary>
    public const string UnpublishedDirectoryCaution = "${COLLECTION_DIR} has no folder until this collection is published from this PC.";
    /// <summary>Windows only: the Mac never starts a connector through cmd.exe, so it has nothing to say here.</summary>
    public const string CollectionDirCmdUnsafeCaution = "The folder ${COLLECTION_DIR} stands for" + RemotePattern.CmdUnsafeSuffix;
    public const string OwnCollectionError = "This is your own published collection.";
    public const string NewerDocumentError = "This collection was made by a newer Connector Control.";
    public const string PublishIntoStoreError = "Choose a folder other than the master list folder or its backups.";
    public const string TargetMustBeLocalError = "Copies go into a local collection.";
    public const string CollectionsNotSavedNote = "Collections could not be saved: the collections file is unreadable. Your change is not on disk and will be lost when the file is read again.";
    /// <summary>The platform named here is the platform-forced half: the caution names the OTHER one, so a PC flags a Mac-authored connector and the Mac mirror says "authored on Windows".</summary>
    public const string AuthoredElsewhereCaution = "authored on macOS";
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
    public static string SourceUnreadableError(string fileName, string detail) => $"{fileName} couldn\u2019t be read: {detail}";
    public static string PublishSlugTakenError(string fileName) => $"{fileName} already exists there and belongs to a different collection.";
    public static string PathMarkMovedError(string connector) => $"A path marked in “{connector}” has moved. Open Publish… to mark it again.";
    public static string PublishFolderCarriedError(string connector, string field) => $"“{connector}” carries this machine's publish folder as written, in {field}. Open Publish… to use ${{COLLECTION_DIR}} in its place.";
    public static string KeptPathCarriedError(string connector, string field) => $"“{connector}” carries a path this machine keeps back, in {field}. Open Publish… to review it.";
    /// <summary>The way back is the second sentence: the refusal holds whatever the user does, and a collection of that name makes the same backup restorable.</summary>
    public static string RestoreCollectionGoneError(string collection) => $"This backup was taken from “{collection}”, which no longer exists. Nothing was restored. Create a collection named “{collection}” again, and this backup goes back into it.";
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
    private CollectionsWindowRequest? collectionsWindowRequest;
    private FileWatcher? watcher;
    private FileWatcher? storeWatcher;
    /// <summary>One watcher per bound synced collection, by collection name.</summary>
    private readonly Dictionary<string, SourceWatch> sourceWatchers = new(StringComparer.Ordinal);
    /// <summary>
    /// The last render of each synced collection's document, with the bytes it came from. Kept in
    /// memory only: it is derived from a file this machine can read again at any time, and nothing
    /// outside the review sheet and the row cautions needs it.
    /// </summary>
    private readonly Dictionary<string, RenderedSource> pendingRendered = new(StringComparer.Ordinal);
    /// <summary>Consecutive failed reads per collection, which decide the backoff and when to speak up.</summary>
    private readonly Dictionary<string, int> sourceFailures = new(StringComparer.Ordinal);
    /// <summary>Collections with a retry already waiting on the clock, so a burst of watcher events over one half-written file leaves one chain of attempts rather than one per event.</summary>
    private readonly HashSet<string> sourceRetryScheduled = new(StringComparer.Ordinal);
    /// <summary>The document hash each collection's update was last announced for, so one change is announced once however many times it is re-derived.</summary>
    private readonly Dictionary<string, string> notifiedSourceHashes = new(StringComparer.Ordinal);
    /// <summary>True once the sidecar has been read successfully at least once this run. Until then there is no in-memory state for an unreadable sidecar to protect.</summary>
    private bool hasLoadedCollectionsOnce;
    /// <summary>
    /// Whether the LAST load could read the sidecar (a file that is not there counts: there is
    /// nothing to protect). While it is false our copy of the sidecar may be empty or stale, so
    /// nothing writes it or the bindings that hang off it.
    /// </summary>
    private bool collectionsLoaded;
    /// <summary>
    /// The hash of the sidecar bytes on disk as this app last saw them — written or loaded — so a
    /// save that would change nothing costs neither a write nor a backup rotation, and a file
    /// another machine changed is still rewritten when our copy differs from it.
    /// </summary>
    private string? lastSavedSidecarHash;
    /// <summary>A persist skipped the sidecar because the last load could not read it, so what the user changed about collections is in memory only. Cleared by the next save that lands.</summary>
    private bool collectionsNotSaved;
    /// <summary>A note from the collections load for <see cref="Reload"/> to join with the service's own, since it assigns LastError after LoadCollections has run and would otherwise erase it.</summary>
    private string? collectionsNote;
    private bool disposed;

    /// <summary>
    /// A source watcher and the path it was armed on, so a binding that moves gets a new watcher
    /// and one that did not is left alone (replacing it would re-baseline its last-seen mtime).
    /// </summary>
    private sealed record SourceWatch(string Path, FileWatcher Watcher);

    /// <summary>
    /// One collection document as this platform renders it, with the bytes and the origin it was
    /// read from — what Apply needs, and what the row cautions read the author's platform from.
    /// </summary>
    private sealed record RenderedSource(RenderedCollection Rendered, string Hash, string? Origin);

    /// <summary>Test probe: both watchers are live. Should be true after every reload.</summary>
    internal bool WatchersArmed => watcher is { IsArmed: true } && storeWatcher is { IsArmed: true };

    /// <summary>Test probe: reference identity of both watchers, so a test can confirm a
    /// redundant reload leaves an already-armed watcher alone instead of tearing it down
    /// and re-baselining its mtime.</summary>
    internal (object? Claude, object? Store) WatcherIdentities => (watcher, storeWatcher);

    /// <summary>Test probe: which collections have a live source watcher.</summary>
    internal IReadOnlyList<string> WatchedSourceCollections => sourceWatchers.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Test probe: how many times a source document has been decoded and rendered. Re-deriving
    /// what is pending is cheap; decoding the same bytes again is what must not happen.
    /// </summary>
    internal int SourceRenders { get; private set; }

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

    /// <summary>
    /// What the flyout asked the Collections window to do as it opened. It travels through the
    /// shared state because the two surfaces are separate windows with no reference to each
    /// other; <see cref="TakeCollectionsWindowRequest"/> is how the window consumes it. A newer
    /// request replaces one nobody has taken yet: the menu items behind them cannot be pressed
    /// at once, so the last one asked for is the one the user meant.
    /// </summary>
    public CollectionsWindowRequest? CollectionsWindowRequest
    {
        get => collectionsWindowRequest;
        internal set => Set(ref collectionsWindowRequest, value);
    }

    public bool IsDirty => !DictionaryEquality.Equal(ExpandedServers, AppliedServers);

    /// <summary>
    /// The enabled connectors as Claude must see them, with <c>${COLLECTION_DIR}</c> resolved
    /// against the active collection's folder on this machine (<see cref="CollectionDirectory"/>).
    /// The store itself keeps the token, so the same list still resolves on the next machine, and
    /// a published document carries it as written for each subscriber to resolve against their own
    /// copy. With no folder the token is written as it stands — guessing one would start the wrong
    /// program — and the row carries the caution that says so.
    /// </summary>
    internal IReadOnlyDictionary<string, JsonValue> ExpandedServers
    {
        get
        {
            var servers = Store.EnabledServers;
            if (CollectionDirectory(ActiveCollection) is not { } directory)
            {
                return servers;
            }
            return servers.ToDictionary(p => p.Key, p => Placeholder.ExpandDirectoryToken(p.Value, directory), StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The folder <c>${COLLECTION_DIR}</c> stands for in <paramref name="collection"/> on this
    /// machine, or null when it stands for none here. A synced collection's is the folder its
    /// located document sits in. A local one's is the folder this machine publishes it into —
    /// where the document is written and the tools shipped beside it live, the same folder a
    /// subscriber's copy resolves against. A collection published from another machine, or not at
    /// all, has none on this one.
    /// </summary>
    internal string? CollectionDirectory(string collection)
    {
        if (IsSynced(collection))
        {
            return SourceBinding(collection)?.Path is { } path
                ? Path.GetDirectoryName(Path.GetFullPath(path)) ?? path
                : null;
        }
        return IsPublished(collection) ? CollectionsCache.Published.GetValueOrDefault(collection)?.Folder : null;
    }

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
        ArmSourceWatchers();
    }

    /// <summary>
    /// One watcher per synced collection this machine has located, so a document changing in the
    /// shared folder becomes a pending update without anyone asking. A binding that has not moved
    /// keeps its watcher: replacing a live one re-baselines the last-seen mtime and opens a gap
    /// where a write is simply lost.
    /// </summary>
    private void ArmSourceWatchers()
    {
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, entry) in CollectionsFile.Collections)
        {
            if (entry.Kind == CollectionKind.Synced && SourceBinding(name)?.Path is { } path)
            {
                wanted[name] = path;
            }
        }
        foreach (var name in sourceWatchers.Keys.ToList())
        {
            if (!wanted.TryGetValue(name, out var path) || path != sourceWatchers[name].Path)
            {
                sourceWatchers[name].Watcher.Dispose();
                sourceWatchers.Remove(name);
            }
        }
        foreach (var (name, path) in wanted)
        {
            if (sourceWatchers.ContainsKey(name))
            {
                continue;
            }
            var created = new FileWatcher(path, host.Marshal, () => RunWatcherCallback(() => ReadSource(name, manual: false)));
            created.Start();
            sourceWatchers[name] = new SourceWatch(path, created);
        }
        // Same retry as the other two: arming fails while the folder is missing (a cloud folder
        // not yet synced down), and the next load tries again.
        foreach (var watch in sourceWatchers.Values)
        {
            ReArm(watch.Watcher);
        }
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
        // The saved-sidecar hash describes the file in the old folder; the new one has its own.
        lastSavedSidecarHash = null;
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
        lastSavedSidecarHash = null;
        ArmWatchers();
        RaiseAll();
    }

    /// <summary>
    /// Restores Claude's config from a backup and syncs the reconciliation baseline to the restored
    /// contents BEFORE reloading, so the app's own restore isn't misread as an external change or a re-add.
    /// Throws on a bad backup; nothing is written then.
    ///
    /// The snapshot goes back into the collection the backup was taken from, which becomes the
    /// active one again, since Claude's file held that collection's connectors. A backup whose
    /// collection is gone is refused (<see cref="RestoreCollectionGoneException"/>). One with no
    /// record — older than the record, the first-run original, a file from elsewhere — goes into the
    /// active collection, and publishing still keeps back any path or folder it carries.
    /// </summary>
    public void RestoreClaudeConfig(string backupPath)
    {
        var target = Store.Clone();
        if (BackupCollections.CollectionOf(backupPath, Service.Paths.BackupsDir) is { } recorded)
        {
            if (!Store.Collections.ContainsKey(recorded))
            {
                throw new RestoreCollectionGoneException(recorded);
            }
            target.ActiveCollection = recorded;
        }
        // Every apply backed Claude's file up with this machine's publish folder where the store
        // holds ${COLLECTION_DIR}: a connector that renders just as the backup does keeps its token,
        // with the folder of the day, the current one or one the collection has since left. Only
        // this machine's own binding counts; another machine's record has no folder here.
        var collection = target.ActiveCollection;
        var binding = IsPublished(collection) ? CollectionsCache.Published.GetValueOrDefault(collection) : null;
        var earlier = binding is null ? [] : binding.PublishedFolders.Where(f => f != binding.Folder).Order(StringComparer.Ordinal).ToList();
        var servers = Service.RestoreClaudeConfig(backupPath, target, binding?.Folder, earlier,
            backedUpFrom: CollectionsCache.LastAppliedCollection,
            activating: !string.Equals(collection, Store.ActiveCollection, StringComparison.Ordinal));
        RecordApplied(collection, servers.Keys);
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

            var applied = LastApplied;
            var result = Service.LoadAndReconcile(
                baseline: hasLoadedOnce ? AppliedServers : null,
                storeAuthoritative: trigger != ReloadTrigger.Routine,
                lastAppliedCollection: applied.Collection,
                lastAppliedNames: applied.Names);
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
            // the second one is the actionable one (Backups ▸ Restore… is the way out). The
            // collections load runs above and adds its own note here rather than setting
            // LastError itself, which this line would then overwrite.
            List<string> notes = [.. result.Notes];
            if (collectionsNote is not null)
            {
                notes.Add(collectionsNote);
            }
            if (collectionsNotSaved)
            {
                notes.Add(CollectionsNotSavedNote);
            }
            LastError = notes.Count > 0 ? string.Join(" ", notes) : null;
            if (!IsDirty)
            {
                ApplyRetryNeeded = false;
            }

            // The store is the source of truth; Claude's config is downstream. Any divergence from
            // the render is regenerated away, arming the same Restart Required footer as a user-made
            // change. No loop: the regenerating write satisfies the watcher-triggered follow-up reload.
            var enabled = ExpandedServers;
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
            else if (result.ClaudeServers is { } held)
            {
                // Claude's file already holds exactly what the active collection renders, so it holds
                // that collection — which a first launch, with no apply yet, needs recorded.
                RecordApplied(ActiveCollection, held.Keys);
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
        // What this machine publishes follows the store it has just loaded: a change made on the
        // author's other machine arrives as a store change and reaches the team from here.
        PublishIfChanged();
        // Only when arming previously failed — the parent directory did not exist, or
        // FileWatcher.HandleError disarmed itself because the directory was deleted.
        // Never a blanket re-arm: a flyout open reloads, and tearing two
        // FileSystemWatchers down and rebuilding them each time would re-baseline the
        // last-seen write time and open a gap where an external write is simply lost.
        // (FileWatcher.Start() is itself a no-op while armed; the IsArmed test states the
        // intent at the call site rather than relying on that.)
        ReArm(watcher);
        ReArm(storeWatcher);
        // The source watchers too: a load that could not read the sidecar returns before
        // ArmSourceWatchers, so one that dropped during that blip would otherwise stay dead until
        // the next save. ReArm only starts the ones that are not already armed.
        foreach (var watch in sourceWatchers.Values)
        {
            ReArm(watch.Watcher);
        }
        RaiseAll();
    }

    // MARK: apply / persist

    private void PerformApply()
    {
        try
        {
            var enabled = ExpandedServers;
            Service.Apply(enabled, CollectionsCache.LastAppliedCollection);
            RecordApplied(ActiveCollection, enabled.Keys);
            AppliedServers = enabled;
            settings.LastApplyDate = host.Now();   // ISettings setters never throw, so this cannot turn a good apply into a failed one
            RefreshRestartState();
            // Clearing the banner keeps what the collections files still have to say: the apply
            // succeeding does not mean the sidecar was written.
            LastError = collectionsNotSaved ? CollectionsNotSavedNote : null;
            ApplyRetryNeeded = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ClaudeConfigException)
        {
            LastError = Friendly(ex);
            ApplyRetryNeeded = true;
        }
    }

    /// <summary>
    /// What Claude's file was last written from, for the launch ingest: the collection and the
    /// connector names that apply wrote. In memory once the collections files are loaded, and read
    /// from this machine's cache before then — at launch the store loads first.
    /// </summary>
    private (string? Collection, IReadOnlySet<string>? Names) LastApplied
    {
        get
        {
            var cache = hasLoadedCollectionsOnce
                ? CollectionsCache
                : CollectionsLocalCache.Load(Service.Paths.CollectionsCachePath);
            return (cache.LastAppliedCollection, cache.LastAppliedNames);
        }
    }

    /// <summary>
    /// Records what Claude's file now holds — <paramref name="collection"/>, and the names written
    /// into it — in this machine's cache, through the same gate as every other cache save. A save
    /// that fails leaves the record in memory for the next.
    /// </summary>
    private void RecordApplied(string collection, IEnumerable<string> names)
    {
        var written = new HashSet<string>(names, StringComparer.Ordinal);
        if (string.Equals(CollectionsCache.LastAppliedCollection, collection, StringComparison.Ordinal)
            && CollectionsCache.LastAppliedNames is { } before && before.SetEquals(written))
        {
            return;
        }
        CollectionsCache = CollectionsCache with { LastAppliedCollection = collection, LastAppliedNames = written };
        if (!collectionsLoaded)
        {
            return;
        }
        try
        {
            CollectionsCache.Save(Service.Paths.CollectionsCachePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; the next cache save writes it.
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
        // The store just changed, so what a source document would change with it may have too —
        // and the excluded lists a re-render produces belong in the cache this save writes.
        RecomputePending();
        try
        {
            var isProtected = Service.SaveStore(Store).Protected;
            // A sidecar that the last load could not read is one something else is writing. Our
            // copy of it may never have been filled, and saving that would land an empty sidecar
            // — and, behind it, a cache pruned against nothing — on top of the real file the
            // moment the other writer finishes. Both wait for a load that can read it. The
            // master list is ours alone and always saves.
            if (collectionsLoaded)
            {
                // The sidecar sits in the same folder as the master list, so a folder that
                // refuses the owner-only permission refuses it for both: one caution covers them.
                isProtected &= SaveCollectionsIfChanged();
                CollectionsCache.Save(Service.Paths.CollectionsCachePath);
                collectionsNotSaved = false;
                if (LastError == CollectionsNotSavedNote)
                {
                    // Only ever our own note: a real failure's message stays where it is.
                    LastError = null;
                }
            }
            else
            {
                // Nothing was written. The user just changed something about collections and it
                // exists only in memory, which is worth saying rather than looking like a save.
                collectionsNotSaved = true;
                LastError = CollectionsNotSavedNote;
            }
            StoreNotPrivate = !isProtected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = Friendly(ex);
        }
        // Every binding change reaches disk through here: subscribe, locate, stop syncing,
        // rename and delete all end in a save, so this is where the watchers follow them.
        ArmSourceWatchers();
        // Publishing is derived from the store on every store change, never from a save event:
        // the store has just changed, so what the team reads may have to change with it.
        PublishIfChanged();
    }

    /// <summary>The sidecar's bytes as they are on disk, or null when it is not there to read.</summary>
    private string? ReadSidecarHash()
    {
        try
        {
            return ContentHash.Sha256(File.ReadAllBytes(Service.Paths.CollectionsFilePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The sidecar only when it would differ: every connector change persists the store, and most
    /// of them say nothing new about collections. Rewriting the same bytes would rotate a backup
    /// and churn a file that travels through someone's sync tool for nothing. Returns whether the
    /// bytes on disk are owner-only — true when there was nothing to write.
    /// </summary>
    private bool SaveCollectionsIfChanged()
    {
        var hash = ContentHash.Sha256(CollectionsFile.Encode().Serialize());
        if (hash == lastSavedSidecarHash)
        {
            return true;
        }
        var result = Service.SaveCollections(CollectionsFile);
        lastSavedSidecarHash = hash;
        return result.Protected;
    }

    /// <summary>
    /// Toggles take effect immediately; the Restart Required button is the only follow-up step.
    ///
    /// <paramref name="collection"/> (null: the active one) follows <see cref="Upsert"/>: only the
    /// active collection reaches Claude, so a toggle anywhere else stops at the store.
    /// </summary>
    public void SetEnabled(string name, bool on, string? collection = null)
    {
        var target = collection ?? ActiveCollection;
        // Read-only lookup: a collection that isn't there has nothing to toggle, and must not be
        // brought into being by the attempt — what Swift's optional chain gives for free.
        if (Store.Collections.TryGetValue(target, out var held) && held.Mcps.TryGetValue(name, out var entry))
        {
            held.Mcps[name] = entry with { Enabled = on };
        }
        PersistStore();
        if (target == ActiveCollection)
        {
            PerformApply();
        }
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

    /// <summary>
    /// Validates and saves an entry into <paramref name="collection"/> (null: the active one).
    /// Returns an error message, or null on success.
    ///
    /// Only the active collection reaches Claude, so a write to any other one stops at the
    /// store: the caller's apply finds nothing Claude runs has changed and writes nothing.
    ///
    /// What the collection's publish record says about the connector follows it: a rename carries
    /// its ticks to the new name, and <paramref name="pathMarks"/> (null: leave them) replaces its
    /// path marks with ones re-keyed to where the saved arguments put them. Both land in the same
    /// save as the connector, so the document written at the end of it never pairs the new
    /// arguments with the old marks.
    /// </summary>
    public string? Upsert(string name, McpEntry entry, string? renamedFrom, string? collection = null,
                          IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark>? pathMarks = null)
    {
        var target = collection ?? ActiveCollection;
        var trimmed = name.TrimSpaces();
        if (trimmed.Length == 0)
        {
            return NameEmptyError;
        }
        // Read-only until both guards pass: a rejected save must leave no trace, and creating the
        // collection up front would leave an empty one behind. Swift's optional chain does the
        // same by construction.
        var existing = Store.Collections.GetValueOrDefault(target)?.Mcps;
        if (trimmed != renamedFrom && existing is not null && existing.ContainsKey(trimmed))
        {
            return DuplicateNameError(trimmed);
        }
        if (renamedFrom is { } old && old != trimmed)
        {
            existing?.Remove(old);
        }
        McpsIn(target)[trimmed] = entry;
        EditPublishIntent(target, intent =>
        {
            if (renamedFrom is { } previous && previous != trimmed)
            {
                intent = intent.MovingConnector(previous, trimmed);
            }
            return pathMarks is null ? intent : intent.ReplacingPathMarks(trimmed, pathMarks);
        });
        PersistStore();
        RaiseAll();
        return null;
    }

    /// <summary>
    /// Removes and persists; the caller applies (both happen in one turn). Whatever the publish
    /// record said about the connector goes with it: a connector added later under the same name
    /// was never ticked, and a mark left behind would refuse every publish as a path that had
    /// moved.
    /// </summary>
    public void Remove(string name, string? collection = null)
    {
        var target = collection ?? ActiveCollection;
        // A collection that isn't there has nothing to remove — and must not be brought into
        // being by the attempt, which is what Swift's optional chain gives for free.
        if (Store.Collections.TryGetValue(target, out var held))
        {
            held.Mcps.Remove(name);
        }
        EditPublishIntent(target, intent => intent.MovingConnector(name, null));
        PersistStore();
        RaiseAll();
    }

    /// <summary>
    /// Rewrites a published collection's intent in memory, for the save that follows to write. A
    /// collection that publishes nothing is left alone, and so is the sidecar when the edit changes
    /// nothing — every assignment announces itself to the windows watching it.
    /// </summary>
    private void EditPublishIntent(string collection, Func<PublishIntent, PublishIntent> edit)
    {
        if (CollectionsFile.Collections.GetValueOrDefault(collection) is not { Publish: { } record } entry)
        {
            return;
        }
        var intent = edit(record.Intent);
        if (intent.Equals(record.Intent))
        {
            return;
        }
        SetSidecarEntry(collection, new CollectionsFile.Entry(
            entry.Kind, entry.FileName, entry.RelativeToStore, entry.Origin, entry.Needs,
            new CollectionsFile.PublishRecord(record.Slug, record.Origin, intent), entry.Provenance));
    }

    /// <summary>
    /// A named collection's connectors, ready to be written to. Store.Mcps is deliberately
    /// side-effect free, so the one place that may create a missing collection is here — the
    /// same thing Swift's <c>store.collections[target, default: Collection()]</c> does. Called
    /// only once a write is certain, so nothing that fails leaves an empty collection behind.
    /// </summary>
    private Dictionary<string, McpEntry> McpsIn(string? collection)
    {
        var target = collection ?? ActiveCollection;
        if (!Store.Collections.TryGetValue(target, out var held))
        {
            held = new Collection();
            Store.Collections[target] = held;
        }
        return held.Mcps;
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
    /// The pending window request, cleared, so a request acted on once cannot be acted on again
    /// the next time that window opens.
    ///
    /// The window has to call this twice over: once as it appears, and again on every
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> for
    /// <see cref="CollectionsWindowRequest"/> while it is already on screen. That second call is
    /// what this platform needs most: <c>WindowRegistry</c> keeps one Collections window and
    /// re-activates it, so a window that only reads on load would strand every request raised
    /// after the first.
    /// </summary>
    public CollectionsWindowRequest? TakeCollectionsWindowRequest()
    {
        var request = collectionsWindowRequest;
        CollectionsWindowRequest = null;
        return request;
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
            // The store refuses a name a live collection bears, so a record already under the new
            // name is a departed collection's: this machine's memory of the folder it published
            // into, which the rename must not write over. The two merge, that folder departed to
            // the collection now bearing the name.
            var displaced = CollectionsCache.Kept.GetValueOrDefault(trimmed);
            var kept = Moved(Without(CollectionsCache.Kept, trimmed), name, trimmed);
            if (displaced is not null)
            {
                kept[trimmed] = CollectionsLocalCache.KeptRecord.Renamed(kept.GetValueOrDefault(trimmed), displaced);
            }
            // Claude's file and its backups name the collection they were applied from, and the
            // rename carries both. A backup record that fails to follow restores as refused, the
            // name it holds being gone, never into the wrong collection.
            CollectionsCache = new CollectionsLocalCache(
                Moved(CollectionsCache.Synced, name, trimmed), Moved(CollectionsCache.Published, name, trimmed),
                kept,
                CollectionsCache.LastAppliedCollection == name ? trimmed : CollectionsCache.LastAppliedCollection,
                CollectionsCache.LastAppliedNames);
            try
            {
                BackupCollections.Rename(name, trimmed, Service.Paths.BackupsDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // As above: such a backup is refused on restore.
            }
            PendingUpdates = Moved(PendingUpdates, name, trimmed);
            SourceErrors = Moved(SourceErrors, name, trimmed);
            MoveInPlace(pendingRendered, name, trimmed);
            MoveInPlace(sourceFailures, name, trimmed);
            MoveInPlace(notifiedSourceHashes, name, trimmed);
            if (sourceRetryScheduled.Remove(name))
            {
                sourceRetryScheduled.Add(trimmed);
            }
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
            Without(CollectionsCache.Synced, name), CollectionsCache.Published, CollectionsCache.Kept,
            CollectionsCache.LastAppliedCollection, CollectionsCache.LastAppliedNames);
        // Deleting a collection is not the author's word that the paths it kept back may travel: the
        // connector that carried one is still in another collection, or comes back by an import, a
        // copy or an ingest. What Stop Publishing remembers, this remembers too.
        RememberWhatWasKeptBack(name);
        ForgetOriginsOfDepartedCollections();
        PendingUpdates = Without(PendingUpdates, name);
        SourceErrors = Without(SourceErrors, name);
        ForgetSource(name);
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

    private static void MoveInPlace<TValue>(Dictionary<string, TValue> dictionary, string name, string newName)
    {
        if (dictionary.Remove(name, out var value))
        {
            dictionary[newName] = value;
        }
    }

    /// <summary>The Swift side mutates its published dictionaries in place; here each one is a
    /// read-only view over a dictionary that has to be rebuilt to change one key.</summary>
    private void SetPending(string collection, CollectionDiff? diff) =>
        PendingUpdates = diff is null ? Without(PendingUpdates, collection) : With(PendingUpdates, collection, diff);

    private void SetSourceError(string collection, string? message) =>
        SourceErrors = message is null ? Without(SourceErrors, collection) : With(SourceErrors, collection, message);

    private void SetSidecarEntry(string collection, CollectionsFile.Entry? entry) =>
        CollectionsFile = new CollectionsFile(entry is null
            ? Without(CollectionsFile.Collections, collection)
            : With(CollectionsFile.Collections, collection, entry));

    private void SetBinding(string collection, CollectionsLocalCache.SyncedBinding? binding) =>
        CollectionsCache = new CollectionsLocalCache(
            binding is null ? Without(CollectionsCache.Synced, collection) : With(CollectionsCache.Synced, collection, binding),
            CollectionsCache.Published,
            CollectionsCache.Kept,
            CollectionsCache.LastAppliedCollection,
            CollectionsCache.LastAppliedNames);

    private void SetPublishBinding(string collection, CollectionsLocalCache.PublishBinding? binding) =>
        CollectionsCache = new CollectionsLocalCache(
            CollectionsCache.Synced,
            binding is null
                ? Without(CollectionsCache.Published, collection)
                : With(CollectionsCache.Published, collection, binding),
            CollectionsCache.Kept,
            CollectionsCache.LastAppliedCollection,
            CollectionsCache.LastAppliedNames);

    /// <summary>
    /// Whether a kept record filed under <paramref name="name"/> belongs to
    /// <paramref name="collection"/>, which publishes under <paramref name="origin"/>: it published
    /// under that same origin, or under none yet and the record is filed under this name. A record
    /// whose collection has left the store carries no origin at all
    /// (<see cref="ForgetOriginsOfDepartedCollections"/>), so it belongs to no collection here and
    /// whatever later bears the name inherits its paths but not its folders. One test for both
    /// readers: the union that decides what a document may carry, and the binding a new publish
    /// builds.
    /// </summary>
    internal static bool IsOwn(CollectionsLocalCache.KeptRecord? record, string name, string collection, string? origin) =>
        record?.Origin is { } recorded
        && (string.Equals(recorded, origin, StringComparison.Ordinal) || (origin is null && name == collection));

    /// <summary>
    /// A kept record belongs to the collection that published it. Once that collection has left the
    /// store — deleted here, or gone from a store that synced — the record outlives it and belongs
    /// to nothing: a collection that later bears the name, made here or arriving from the author's
    /// other machine, is a different one, and the folders this one left are another collection's to
    /// it. A collection that only stopped publishing never left, and keeps its own.
    /// </summary>
    private void ForgetOriginsOfDepartedCollections()
    {
        foreach (var (name, remembered) in CollectionsCache.Kept)
        {
            if (remembered.Origin is not null && !Store.Collections.ContainsKey(name))
            {
                SetKeptRecord(name, remembered with { Origin = null });
            }
        }
    }

    /// <summary>
    /// Takes the binding away and keeps what it knew about paths that must not travel: the marked
    /// paths, the released ones and every folder it published into. A collection published again,
    /// here or from another folder, still refuses them.
    /// </summary>
    private void RememberWhatWasKeptBack(string collection)
    {
        if (CollectionsCache.Published.GetValueOrDefault(collection) is not { } binding)
        {
            return;
        }
        var remembered = CollectionsLocalCache.KeptRecord.Remembering(
            binding, CollectionsCache.Kept.GetValueOrDefault(collection));
        SetPublishBinding(collection, null);
        SetKeptRecord(collection, remembered.IsEmpty ? null : remembered);
    }

    /// <summary>What a stopped publish left behind, set or dropped for one collection.</summary>
    private void SetKeptRecord(string collection, CollectionsLocalCache.KeptRecord? record) =>
        CollectionsCache = new CollectionsLocalCache(
            CollectionsCache.Synced,
            CollectionsCache.Published,
            record is null
                ? Without(CollectionsCache.Kept, collection)
                : With(CollectionsCache.Kept, collection, record),
            CollectionsCache.LastAppliedCollection,
            CollectionsCache.LastAppliedNames);

    private static Dictionary<string, TValue> With<TValue>(IReadOnlyDictionary<string, TValue> source, string name, TValue value)
    {
        var copy = new Dictionary<string, TValue>(source, StringComparer.Ordinal);
        copy[name] = value;
        return copy;
    }

    // MARK: collection kinds and bindings

    public CollectionKind KindOf(string collection) => CollectionsFile.KindOf(collection);

    public bool IsSynced(string collection) => KindOf(collection) == CollectionKind.Synced;

    public bool IsPublished(string collection) =>
        CollectionsFile.Collections.TryGetValue(collection, out var entry) && entry.Publish is not null;

    public CollectionsLocalCache.SyncedBinding? SourceBinding(string collection) =>
        CollectionsCache.Synced.TryGetValue(collection, out var binding) ? binding : null;

    /// <summary>
    /// Where a synced collection's document is, as far as this machine knows: the path it is
    /// bound to, or the name the sidecar recorded while the file is still to be found. Null for a
    /// local collection, and for a synced one the sidecar never named. The flyout's chip, its
    /// menu rows and the Collections window's sidebar all name a source this way, so the rule
    /// lives here once rather than in each of them.
    /// </summary>
    public string? SourceLocation(string collection)
    {
        if (!IsSynced(collection))
        {
            return null;
        }
        var found = SourceBinding(collection)?.Path
            ?? (CollectionsFile.Collections.TryGetValue(collection, out var entry) ? entry.FileName : null);
        return string.IsNullOrEmpty(found) ? null : found;
    }

    /// <summary>
    /// Whether this machine can reach the collection's document: true for a local collection,
    /// which has none to find. The same predicate the Locate banner asks, so a window and a
    /// banner can never disagree about whether a file has been found.
    /// </summary>
    public bool IsLocated(string collection) => UnlocatedFileName(collection) is null;

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
    /// The "authored on &lt;platform&gt;" caution reads the launcher platform out of the last
    /// render of the bound document: only a local connector carries one, and only the other
    /// platform's is worth saying anything about.
    ///
    /// A connector using <c>${COLLECTION_DIR}</c> where this machine has no folder for it says so:
    /// a synced collection's document has not been located yet, or a local collection is not
    /// published from here (<see cref="CollectionDirectory"/>).
    /// </summary>
    public string? ConnectorCaution(string connector, string collection)
    {
        if (!Store.Collections.TryGetValue(collection, out var held) || !held.Mcps.TryGetValue(connector, out var entry))
        {
            return null;
        }
        var unfilled = Placeholder.UnfilledNamesIn(entry.Config);
        if (unfilled.Count > 0)
        {
            return NeedsValueCaution(string.Join(", ", unfilled));
        }
        if (pendingRendered.GetValueOrDefault(collection)?.Rendered.Connectors.GetValueOrDefault(connector)?.AuthoredOn
            is { } authored && authored != CollectionPlatforms.Current)
        {
            return AuthoredElsewhereCaution;
        }
        if (Placeholder.UsesDirectoryToken(entry.Config) && IsSynced(collection) && SourceBinding(collection)?.Path is null)
        {
            return LocateCaution;
        }
        if (Placeholder.UsesDirectoryToken(entry.Config) && !IsSynced(collection) && CollectionDirectory(collection) is null)
        {
            return UnpublishedDirectoryCaution;
        }
        // cmd.exe re-parses the arguments of a connector it starts, so a folder the token expands
        // to with a space or one of its metacharacters in it splits the argument, or runs part of
        // it as a command of its own.
        if (CollectionDirectory(collection) is { } directory && RemotePattern.CmdUnsafeCharacter(directory) is not null
            && CommandLine.TryRead(entry.Config, out var command, out var args)
            && (command.Equals("cmd", StringComparison.OrdinalIgnoreCase) || command.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
            && args.Count > 0 && args[0].Equals("/c", StringComparison.OrdinalIgnoreCase)
            && args.Any(arg => arg.Contains(Placeholder.DirectoryToken, StringComparison.Ordinal)))
        {
            return CollectionDirCmdUnsafeCaution;
        }
        return null;
    }

    // MARK: synced collections

    /// <summary>Backoff for a source that could not be read: a sync tool's half-written file, or a cloud placeholder that has not hydrated yet, is the common case and fixes itself in seconds.</summary>
    private static readonly TimeSpan[] SourceRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    /// <summary>
    /// Subscribes to a collection document: the collection is created with what the document
    /// describes, every connector disabled, this machine's binding recorded, and the sidecar told
    /// what each connector still asks the user for. null on success, else the message.
    /// <para>
    /// The new collection does not become the active one. Everything in it arrives disabled, so
    /// switching would empty Claude's config the moment anyone subscribed.
    /// </para>
    /// </summary>
    public string? Subscribe(string path, string? requestedName)
    {
        var full = Path.GetFullPath(path);
        var (document, data, failure) = ReadDocument(full);
        if (document is null)
        {
            return failure;
        }
        // Subscribing to what this machine publishes would make the app its own author: every
        // local edit would come straight back as a pending update to itself.
        if (document.Origin is { } origin && CollectionsFile.Collections.Values.Any(e => e.Publish?.Origin == origin))
        {
            return OwnCollectionError;
        }
        var requested = requestedName?.TrimSpaces() ?? string.Empty;
        var name = requested.Length == 0 ? document.Name : requested;
        var active = Store.ActiveCollection;
        if (Store.AddCollection(name, copyingCurrent: false) is { } error)
        {
            return error;
        }
        Store.ActiveCollection = active;
        var rendered = document.Render();
        var result = CollectionApply.Apply(rendered, new Dictionary<string, McpEntry>(StringComparer.Ordinal), EmptyNeedsByConnector);
        Store.Collections[name] = new Collection(result.Entries);
        SetSidecarEntry(name, new CollectionsFile.Entry(
            CollectionKind.Synced, Path.GetFileName(full), RelativeToStore(full), document.Origin, result.Needs));
        var hash = ContentHash.Sha256(data!);
        SetBinding(name, new CollectionsLocalCache.SyncedBinding(full, hash, rendered.Excluded));
        pendingRendered[name] = new RenderedSource(rendered, hash, document.Origin);
        notifiedSourceHashes[name] = hash;
        PersistStore();
        RaiseAll();
        return null;
    }

    /// <summary>
    /// Points a synced collection at its document on this machine. null on success, else the
    /// message: a file that cannot be read or decoded is not bound, so the Locate banner stays.
    /// </summary>
    public string? LocateSource(string collection, string path)
    {
        // Only a synced collection has a document to point at; anything else is silently
        // ignored, as switching to a collection that does not exist is.
        if (!IsSynced(collection))
        {
            return null;
        }
        var full = Path.GetFullPath(path);
        var (document, data, failure) = ReadDocument(full);
        if (document is null)
        {
            return failure;
        }
        SetBinding(collection, new CollectionsLocalCache.SyncedBinding(
            full, ContentHash.Sha256(data!), SourceBinding(collection)?.Excluded));
        var entry = CollectionsFile.Collections[collection];
        // relativeToStore is only ever set, never cleared: where the document sits relative to
        // the store is a fact every machine shares, and this one finding it elsewhere does not
        // make it untrue.
        SetSidecarEntry(collection, new CollectionsFile.Entry(
            entry.Kind, Path.GetFileName(full), RelativeToStore(full) ?? entry.RelativeToStore,
            entry.Origin, entry.Needs, entry.Publish, entry.Provenance));
        sourceFailures.Remove(collection);
        sourceRetryScheduled.Remove(collection);
        SetSourceError(collection, null);
        // PersistStore re-derives what the newly bound document would change.
        PersistStore();
        // ${COLLECTION_DIR} resolves against the folder just bound, so what Claude runs changes
        // the moment the file is found — with no second click. The dirty check keeps a locate
        // that resolves to nothing new from rewriting Claude's config for the sake of it.
        if (collection == ActiveCollection && IsDirty)
        {
            PerformApply();
        }
        RaiseAll();
        return null;
    }

    /// <summary>The Refresh button: read the source now, and report a failure at once rather than giving it the three chances a watcher-driven read allows.</summary>
    public void RefreshSource(string collection)
    {
        ReadSource(collection, manual: true);
        RaiseAll();
    }

    /// <summary>
    /// Adopts what the source says, through the normal store path: a value the user filled in
    /// follows its marker wherever the author moved it, enabled flags survive, added connectors
    /// arrive disabled, and the sidecar's needs are rewritten from the new render. null on
    /// success; nothing pending is a no-op, so a second Apply cannot undo the first.
    /// </summary>
    public string? ApplyPendingUpdate(string collection)
    {
        if (!PendingUpdates.ContainsKey(collection) || pendingRendered.GetValueOrDefault(collection) is not { } source)
        {
            return null;
        }
        var current = Store.Collections.TryGetValue(collection, out var held)
            ? held.Mcps
            : new Dictionary<string, McpEntry>(StringComparer.Ordinal);
        var entry = CollectionsFile.Collections.GetValueOrDefault(collection) ?? CollectionsFile.Entry.Local;
        var result = CollectionApply.Apply(source.Rendered, current, entry.Needs);
        Store.Collections[collection] = new Collection(result.Entries);
        SetSidecarEntry(collection, new CollectionsFile.Entry(
            entry.Kind, entry.FileName, entry.RelativeToStore, source.Origin, result.Needs, entry.Publish, entry.Provenance));
        SetBinding(collection, new CollectionsLocalCache.SyncedBinding(
            SourceBinding(collection)?.Path, source.Hash, source.Rendered.Excluded));
        // Cleared BEFORE the save: PersistStore re-derives what is pending from the document and
        // the store it is about to write, and clearing afterwards would throw that answer away.
        SetPending(collection, null);
        notifiedSourceHashes[collection] = source.Hash;
        PersistStore();
        // Claude only runs the active collection, so only that one reaches its config.
        if (collection == ActiveCollection)
        {
            PerformApply();
        }
        RaiseAll();
        return null;
    }

    /// <summary>
    /// Stop Syncing: the collection keeps its connectors, its filled values and its enabled flags
    /// and becomes an ordinary local one. Unfilled markers stay as text, so a row still says what
    /// it needs — without the hint, which travelled with the document.
    /// </summary>
    public void StopSyncing(string collection)
    {
        if (!IsSynced(collection))
        {
            return;
        }
        // The token resolved against the bound document's folder for as long as there was one. A
        // local collection has no document, so the folder it resolved to is written into the
        // configs once, exactly as an imported copy expands it — otherwise a path that worked a
        // second ago would become the literal token, with nothing left to explain it.
        if (SourceBinding(collection)?.Path is { } bound && Store.Collections.TryGetValue(collection, out var held))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(bound)) ?? bound;
            foreach (var (name, entry) in held.Mcps.ToList())
            {
                if (Placeholder.UsesDirectoryToken(entry.Config))
                {
                    held.Mcps[name] = entry with { Config = Placeholder.ExpandDirectoryToken(entry.Config, directory) };
                }
            }
        }
        SetSidecarEntry(collection, null);
        SetBinding(collection, null);
        SetPending(collection, null);
        SetSourceError(collection, null);
        ForgetSource(collection);
        PersistStore();
        // The expansion above changed what the collection holds; if it is the live one, that is
        // a change to what Claude runs. Baking in the same folder the token already resolved to
        // normally leaves the two identical, and the dirty check spares the write.
        if (collection == ActiveCollection && IsDirty)
        {
            PerformApply();
        }
        RaiseAll();
    }

    /// <summary>The document the review sheet lists, as this platform renders it.</summary>
    public RenderedCollection? PendingDocument(string collection) => pendingRendered.GetValueOrDefault(collection)?.Rendered;

    /// <summary>
    /// The identity of the bytes <see cref="PendingDocument"/> was rendered from. The review
    /// sheet holds on to it so Apply can tell that the document it listed is still the document
    /// it would land.
    /// </summary>
    public string? PendingSourceHash(string collection) => pendingRendered.GetValueOrDefault(collection)?.Hash;

    /// <summary>Every bound synced collection's pending update, re-derived from its document and the store as it stands now: pending is derived, never stored as a fact.</summary>
    internal void RecomputePending()
    {
        foreach (var name in CollectionsFile.Collections.Keys.Order(StringComparer.Ordinal).ToList())
        {
            if (CollectionsFile.Collections[name].Kind == CollectionKind.Synced && SourceBinding(name)?.Path is not null)
            {
                ReadSource(name, manual: false);
            }
        }
    }

    /// <summary>Reads one collection's document and derives what it would change. Nothing here reaches Claude's config: that takes <see cref="ApplyPendingUpdate"/>.</summary>
    private void ReadSource(string collection, bool manual)
    {
        if (SourceBinding(collection) is not { Path: { } path } binding)
        {
            return;
        }
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            NoteSourceFailure(collection, SourceUnreadableError(Path.GetFileName(path), ex.Message), manual);
            return;
        }
        var hash = ContentHash.Sha256(data);
        RenderedCollection rendered;
        if (pendingRendered.GetValueOrDefault(collection) is { } kept && kept.Hash == hash)
        {
            // The same bytes as last time: the render they produce cannot have changed, so only
            // the diff below is re-derived. The store moves under it constantly — a local edit,
            // another machine's apply arriving — and the answer has to follow it.
            rendered = kept.Rendered;
        }
        else
        {
            CollectionDocument document;
            try
            {
                document = CollectionDocument.Decode(data);
            }
            catch (CollectionDocumentException ex)
            {
                if (ex.NewerFormatVersion is not null)
                {
                    // No amount of waiting makes this readable, so it is said at once and never retried.
                    sourceFailures.Remove(collection);
                    SetSourceError(collection, NewerDocumentError);
                    return;
                }
                NoteSourceFailure(collection, SourceUnreadableError(Path.GetFileName(path), ex.Message), manual);
                return;
            }
            rendered = document.Render();
            SourceRenders++;
            pendingRendered[collection] = new RenderedSource(rendered, hash, document.Origin);
        }
        sourceFailures.Remove(collection);
        sourceRetryScheduled.Remove(collection);
        SetSourceError(collection, null);
        SetBinding(collection, new CollectionsLocalCache.SyncedBinding(binding.Path, binding.LastHash, rendered.Excluded));
        var current = Store.Collections.TryGetValue(collection, out var held)
            ? held.Mcps
            : new Dictionary<string, McpEntry>(StringComparer.Ordinal);
        var diff = CollectionDiff.Pending(rendered, current);
        if (diff.IsEmpty)
        {
            SetPending(collection, null);
            return;
        }
        SetPending(collection, diff);
        // Once per document: the same change is re-derived on every store change, and the user
        // hears about it once.
        if (notifiedSourceHashes.GetValueOrDefault(collection) == hash)
        {
            return;
        }
        // Recorded before the first-load gate, not after it: an update that was already there
        // when the app opened is not news, and it must not become news on the next reload.
        notifiedSourceHashes[collection] = hash;
        if (!hasLoadedOnce)
        {
            return;
        }
        Notify(CollectionUpdateNotificationBody(collection, diff.Summary()));
    }

    /// <summary>
    /// A failed read. A watcher-driven one backs off and tries again — the third consecutive
    /// failure is the one the user hears about; Refresh, with someone waiting for an answer,
    /// reports the first.
    /// </summary>
    private void NoteSourceFailure(string collection, string message, bool manual)
    {
        var failures = sourceFailures.GetValueOrDefault(collection) + 1;
        sourceFailures[collection] = failures;
        if (manual || failures >= SourceRetryDelays.Length)
        {
            SetSourceError(collection, message);
        }
        if (manual || failures > SourceRetryDelays.Length || !sourceRetryScheduled.Add(collection))
        {
            return;
        }
        host.Delay(SourceRetryDelays[failures - 1], () =>
        {
            if (disposed)
            {
                return;
            }
            sourceRetryScheduled.Remove(collection);
            // A read that succeeded in the meantime leaves nothing to retry.
            if (!sourceFailures.ContainsKey(collection))
            {
                return;
            }
            ReadSource(collection, manual: false);
            RaiseAll();
        });
    }

    /// <summary>Everything this run knows about one collection's document, dropped when the collection stops being synced or goes away.</summary>
    private void ForgetSource(string collection)
    {
        pendingRendered.Remove(collection);
        sourceFailures.Remove(collection);
        sourceRetryScheduled.Remove(collection);
        notifiedSourceHashes.Remove(collection);
    }

    /// <summary>
    /// The document at <paramref name="path"/>, or the message to show for it — the one
    /// read-and-decode every caller that opens a collection document shares. The tuple's document
    /// is null exactly when the failure is not; the bytes come back too, since the callers that
    /// keep a binding hash exactly what they read.
    /// </summary>
    internal static (CollectionDocument? Document, byte[]? Data, string? Failure) ReadDocument(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, null, SourceUnreadableError(Path.GetFileName(path), ex.Message));
        }
        try
        {
            return (CollectionDocument.Decode(data), data, null);
        }
        catch (CollectionDocumentException ex)
        {
            return (null, null, ex.NewerFormatVersion is null ? SourceUnreadableError(Path.GetFileName(path), ex.Message) : NewerDocumentError);
        }
    }

    /// <summary>
    /// Where <paramref name="path"/> sits inside the store's own folder, or null when it is
    /// somewhere else. A document that travels with the master list is found again on every
    /// machine from this, so the separator is the one the Mac writes: this value is shared.
    /// </summary>
    private string? RelativeToStore(string path)
    {
        var relative = Path.GetRelativePath(Service.Paths.StoreDir, path);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal) || relative == path)
        {
            return null;
        }
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, CollectionsFile.Need>> EmptyNeedsByConnector =
        new Dictionary<string, IReadOnlyDictionary<string, CollectionsFile.Need>>(StringComparer.Ordinal);

    // MARK: import as copies

    /// <summary>The calendar date a copy records as the day it arrived.</summary>
    internal string Today => IsoTimestamp.LocalDate(host.Now());

    /// <summary>
    /// Imports a document's connectors into a local collection as copies: each one rendered for
    /// this platform, <c>${COLLECTION_DIR}</c> expanded once against the folder the document sits
    /// in, disabled, and stamped with where it came from. There is no link to the file afterwards
    /// — that is what Keep as its own collection is for.
    /// <para>
    /// <paramref name="choices"/> answers, per connector, what to do where the target already
    /// holds that name: Replace keeps the values the user filled in, Keep both lands a suffixed
    /// copy beside it, Skip leaves it alone. A name the target does not hold is simply added.
    /// null on success.
    /// </para>
    /// </summary>
    public string? ImportCopies(string path, string collection, IReadOnlyDictionary<string, ImportChoice> choices, string date)
    {
        // A name that is not a collection is silently ignored, as switching to one is.
        if (!Store.Collections.TryGetValue(collection, out var target))
        {
            return null;
        }
        // Copies belong where the user owns what they hold. A synced collection answers to its
        // document, so anything added beside it would show up as a pending removal at once.
        if (KindOf(collection) != CollectionKind.Local)
        {
            return TargetMustBeLocalError;
        }
        var full = Path.GetFullPath(path);
        var (document, _, failure) = ReadDocument(full);
        if (document is null)
        {
            return failure;
        }
        var directory = Path.GetDirectoryName(full) ?? full;
        var rendered = document.Render();
        var entry = CollectionsFile.Collections.GetValueOrDefault(collection) ?? CollectionsFile.Entry.Local;
        var provenance = new Dictionary<string, CollectionsFile.Provenance>(entry.Provenance, StringComparer.Ordinal);
        var landed = false;
        // Sorted so two connectors that want the same suffixed name always get the same one.
        foreach (var name in rendered.Connectors.Keys.Order(StringComparer.Ordinal).ToList())
        {
            var connector = rendered.Connectors[name];
            var present = target.Mcps.ContainsKey(name);
            // A collision with nothing said about it is left alone: an import must never
            // overwrite something the user did not point at. Add on a collision means the same
            // thing — there is no way to add under a name that is taken.
            var choice = choices.TryGetValue(name, out var chosen)
                ? chosen
                : present ? ImportChoice.Skip : ImportChoice.Add;
            if (choice == ImportChoice.Skip || (present && choice == ImportChoice.Add))
            {
                continue;
            }
            // Expanded here, once: a copy has no document to resolve the token against later.
            var incoming = new RenderedConnector(
                Placeholder.ExpandDirectoryToken(connector.Config, directory), connector.Needs, connector.AuthoredOn);
            var replacing = present && choice == ImportChoice.Replace;
            var landing = replacing ? name : FreeConnectorName(name, collection);
            // Replace carries the filled values across by marker name, the way an update to a
            // synced collection does: the incoming markers say where each value belongs, and the
            // same pointers in what is already there say what the user typed. Everything else has
            // nothing to carry, so the same call lands it disabled and as it stands.
            var current = new Dictionary<string, McpEntry>(StringComparer.Ordinal);
            if (replacing)
            {
                current[landing] = target.Mcps[name];
            }
            var result = CollectionApply.Apply(
                new RenderedCollection(
                    new Dictionary<string, RenderedConnector>(StringComparer.Ordinal) { [landing] = incoming },
                    new Dictionary<string, string>(StringComparer.Ordinal)),
                current,
                new Dictionary<string, IReadOnlyDictionary<string, CollectionsFile.Need>>(StringComparer.Ordinal)
                {
                    [landing] = incoming.Needs.ToDictionary(
                        pair => pair.Key,
                        pair => new CollectionsFile.Need(pair.Value.Hint, pair.Value.Pointer),
                        StringComparer.Ordinal),
                });
            target.Mcps[landing] = result.Entries[landing];
            // The needs the render produced are not kept: a copy is not waiting on an author, and
            // the markers left in its config are what the row's caution reads.
            provenance[landing] = new CollectionsFile.Provenance(document.Name, document.Author, date);
            landed = true;
        }
        if (!landed)
        {
            return null;
        }
        SetSidecarEntry(collection, new CollectionsFile.Entry(
            entry.Kind, entry.FileName, entry.RelativeToStore, entry.Origin, entry.Needs, entry.Publish, provenance));
        PersistStore();
        // Everything imported arrives off, so only a Replace over a connector that was already on
        // can change what Claude runs — and the dirty check spares the write when it doesn't.
        if (collection == ActiveCollection && IsDirty)
        {
            PerformApply();
        }
        RaiseAll();
        return null;
    }

    /// <summary>
    /// Copies connectors from one collection into a local one exactly as they stand: markers stay
    /// unfilled, every copy arrives disabled, and each one records where it came from. A name the
    /// target already holds lands beside it as "&lt;name&gt; 2" unless <paramref name="choices"/>
    /// says otherwise: Replace takes the target's entry over under its own name, Skip copies
    /// nothing. <paramref name="choices"/> is keyed by the connector's name in the source. null on
    /// success.
    /// <para>
    /// Unlike <see cref="ImportCopies"/>, a collision nothing is said about still lands beside —
    /// and Add means the same. An import arrives from a file the user did not write, where
    /// silence should change nothing; a copy is an act they just asked for on rows they ticked,
    /// where silence should do it. Only Skip and Replace read the same way in both.
    /// </para>
    /// </summary>
    public string? MakeLocalCopy(IReadOnlyList<string> connectors, string source, string target,
        IReadOnlyDictionary<string, ImportChoice>? choices = null)
    {
        if (!Store.Collections.TryGetValue(source, out var from) || !Store.Collections.TryGetValue(target, out var into))
        {
            return null;
        }
        if (KindOf(target) != CollectionKind.Local)
        {
            return TargetMustBeLocalError;
        }
        var date = Today;
        var entry = CollectionsFile.Collections.GetValueOrDefault(target) ?? CollectionsFile.Entry.Local;
        var provenance = new Dictionary<string, CollectionsFile.Provenance>(entry.Provenance, StringComparer.Ordinal);
        var landed = false;
        foreach (var name in connectors.Order(StringComparer.Ordinal).ToList())
        {
            if (!from.Mcps.TryGetValue(name, out var held))
            {
                continue;
            }
            if (choices?.GetValueOrDefault(name) == ImportChoice.Skip)
            {
                continue;
            }
            // Replace keeps the target's own key, so the connector that subscribers and Claude
            // know by name is the one that changes rather than gaining a neighbour.
            var taken = into.Mcps.ContainsKey(name);
            var copied = choices?.GetValueOrDefault(name) == ImportChoice.Replace && taken
                ? name
                : FreeConnectorName(name, target);
            into.Mcps[copied] = held with { Enabled = false, Config = CopiedConfig(held.Config, source) };
            provenance[copied] = new CollectionsFile.Provenance(source, null, date);
            landed = true;
        }
        if (!landed)
        {
            return null;
        }
        SetSidecarEntry(target, new CollectionsFile.Entry(
            entry.Kind, entry.FileName, entry.RelativeToStore, entry.Origin, entry.Needs, entry.Publish, provenance));
        PersistStore();
        // Every copy is off, so nothing here can change what Claude runs.
        RaiseAll();
        return null;
    }

    /// <summary>
    /// The whole collection again as a new local one, every connector copied as it stands and
    /// recording where it came from. It does not become the active collection: everything in it
    /// is off, so switching would empty Claude's config. null on success, else the message.
    /// </summary>
    public string? MakeLocalCopyOfCollection(string source, string newName)
    {
        if (!Store.Collections.TryGetValue(source, out var held))
        {
            return null;
        }
        var active = Store.ActiveCollection;
        if (Store.AddCollection(newName, copyingCurrent: false) is { } error)
        {
            return error;
        }
        Store.ActiveCollection = active;
        var name = newName.TrimSpaces();
        var date = Today;
        var entries = new Dictionary<string, McpEntry>(StringComparer.Ordinal);
        var provenance = new Dictionary<string, CollectionsFile.Provenance>(StringComparer.Ordinal);
        foreach (var (connector, entry) in held.Mcps)
        {
            entries[connector] = entry with { Enabled = false, Config = CopiedConfig(entry.Config, source) };
            provenance[connector] = new CollectionsFile.Provenance(source, null, date);
        }
        Store.Collections[name] = new Collection(entries);
        SetSidecarEntry(name, new CollectionsFile.Entry(CollectionKind.Local, provenance: provenance));
        PersistStore();
        RaiseAll();
        return null;
    }

    /// <summary>
    /// One connector's config as a local collection has to hold it: what it says, with
    /// <c>${COLLECTION_DIR}</c> resolved against the folder the source's document sits in. A local
    /// collection has no document, so the token would resolve to nothing afterwards — exactly what
    /// Stop Syncing bakes in for the same reason. <c>${CC_NEEDS:…}</c> markers stay as they are: a
    /// copy asks the user for the same values the original did.
    /// </summary>
    private JsonValue CopiedConfig(JsonValue config, string source)
    {
        if (!Placeholder.UsesDirectoryToken(config) || SourceBinding(source)?.Path is not { } bound)
        {
            return config;
        }
        return Placeholder.ExpandDirectoryToken(config, Path.GetDirectoryName(Path.GetFullPath(bound)) ?? bound);
    }

    /// <summary>
    /// <paramref name="name"/>, or "name 2", "name 3", … — the first one no connector in
    /// <paramref name="collection"/> is called. Keep both lands beside what is already there
    /// rather than over it.
    /// </summary>
    private string FreeConnectorName(string name, string collection)
    {
        var mcps = Store.Collections.TryGetValue(collection, out var held) ? held.Mcps : null;
        if (mcps is null || !mcps.ContainsKey(name))
        {
            return name;
        }
        var suffix = 2;
        while (mcps.ContainsKey(name + " " + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            suffix++;
        }
        return name + " " + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // MARK: publishing and export

    /// <summary>
    /// Starts publishing a local collection into <paramref name="folder"/>, or re-points one that
    /// already publishes (the failed-write banner's Choose Folder…). The slug and the origin are
    /// fixed the first time and never re-derived, so renaming the collection cannot orphan the
    /// document the team already subscribed to. The document is written before this returns.
    /// null on success, else the message to show.
    /// </summary>
    /// <param name="reviewedValues">
    /// What the author's Publish in the dialog says must never travel as written: it replaces this
    /// machine's list of marked paths (<see cref="CollectionsLocalCache.PublishBinding.MarkedValues"/>).
    /// Null — the banner's Choose Folder…, which nobody reviewed — keeps the list the collection
    /// already had.
    /// </param>
    /// <param name="releasedValues">
    /// The paths the author released in the dialog, let travel in this collection's document
    /// although this machine keeps them back elsewhere.
    /// </param>
    public string? StartPublishing(string collection, string folder, PublishIntent intent,
                                   IReadOnlySet<string>? reviewedValues = null, IReadOnlySet<string>? releasedValues = null)
    {
        // A synced collection has an author elsewhere, and a name that is not a collection has
        // nothing to publish. Nothing offers either, so both get the silence LocateSource gives a
        // collection that is not synced.
        if (!Store.Collections.ContainsKey(collection) || IsSynced(collection))
        {
            return null;
        }
        // A record the next save cannot write would leave a document in a shared folder that
        // nothing here remembers publishing. It waits for a sidecar that can be read.
        if (!collectionsLoaded)
        {
            return CollectionsNotSavedNote;
        }
        var full = Path.GetFullPath(folder);
        // The master list's own folder is synced to every machine the user owns, and the backups
        // folder is rotated; a document in either would be swept up by machinery that is not
        // about publishing at all.
        if (IsInside(full, Service.Paths.StoreDir) || IsInside(full, Service.Paths.BackupsDir))
        {
            return PublishIntoStoreError;
        }
        var entry = CollectionsFile.Collections.GetValueOrDefault(collection) ?? CollectionsFile.Entry.Local;
        var slug = entry.Publish?.Slug ?? Slug.Make(collection);
        var previousOrigin = entry.Publish?.Origin;
        var origin = previousOrigin ?? Guid.NewGuid().ToString("D").ToLowerInvariant();
        var fileName = slug + "." + CollectionDocument.FileExtension;
        var target = Path.Combine(full, fileName);
        // Somebody else's document under the name this one would take: publishing over it would
        // replace what their subscribers follow. A file that cannot be decoded counts too — it
        // has no origin to vouch for it.
        if (File.Exists(target) && ReadOrigin(target) != origin)
        {
            return PublishSlugTakenError(fileName);
        }
        SetSidecarEntry(collection, new CollectionsFile.Entry(
            entry.Kind, entry.FileName, entry.RelativeToStore, entry.Origin, entry.Needs,
            new CollectionsFile.PublishRecord(slug, origin, intent), entry.Provenance));
        var previous = CollectionsCache.Published.GetValueOrDefault(collection);
        // Publishing again takes back what stopping left behind: the lists are this machine's memory
        // of what must not travel, and they outlive the binding. The folders, though, are this
        // collection's to take back only where the record is its own — entry.Publish is the origin
        // it published under before this call, and a record whose collection has gone belongs to
        // none. A re-used name inherits its paths, but the folders stay in the record, kept back as
        // another collection's: taking them would withdraw a release the dialog offered and refuse
        // every save after it with no answer left, and dropping them would let a folder this
        // machine published into travel the moment a connector brings it back. An own record is
        // spent, except for the departed folders it already held, which stay behind under the new
        // origin so the next Stop merges them as departed rather than as its own.
        var remembered = CollectionsCache.Kept.GetValueOrDefault(collection);
        var own = IsOwn(remembered, collection, collection, previousOrigin);
        var marked = previous?.MarkedValues ?? remembered?.MarkedValues;
        SetPublishBinding(collection, new CollectionsLocalCache.PublishBinding(
            full,
            // A new folder has nothing in it this app wrote, so the next write is unconditional.
            string.Equals(previous?.Folder, full, StringComparison.Ordinal) ? previous?.LastWrittenHash : null,
            reviewedValues ?? marked,
            Released(previous?.ReleasedValues ?? remembered?.ReleasedValues, releasedValues, reviewedValues ?? marked),
            // The folder it left stays this machine's own: a backup or a connector can bring it back.
            (previous?.PublishedFolders
                ?? (own ? remembered?.PublishedFolders : null)
                ?? new HashSet<string>(StringComparer.Ordinal))
                .Concat(previous is null ? [] : [previous.Folder]).Append(full),
            // Carried so what this binding leaves behind still says which collection's folders they
            // were, after the sidecar entry that names the origin has gone.
            origin));
        if (own)
        {
            var departed = remembered?.DepartedFolders ?? new HashSet<string>(StringComparer.Ordinal);
            SetKeptRecord(collection, departed.Count == 0
                ? null : new CollectionsLocalCache.KeptRecord(null, null, null, departed, origin));
        }
        // PersistStore ends in PublishIfChanged, which is what writes the document.
        PersistStore();
        // ${COLLECTION_DIR} stands for the folder just chosen from now on, so what Claude runs
        // changes the moment publishing starts. The dirty check spares the write when nothing in
        // the collection uses the token.
        if (collection == ActiveCollection && IsDirty)
        {
            PerformApply();
        }
        RaiseAll();
        return PublishError?.Collection == collection ? PublishError.Message : null;
    }

    /// <summary>
    /// Publishing again into a different folder, which is what the failed-write banner's Choose
    /// Folder… does from the flyout and from the Collections window alike. The recorded intent
    /// travels unchanged: the dialog is where what the document says gets edited, not this.
    /// null on success, else the message.
    ///
    /// Refused, with nothing changed, while the collection's publish is blocked for review. The
    /// intent carried here is the one that blocked, so the new folder would be bound, receive
    /// nothing, and leave the old folder's document behind. The Publish dialog is the way out,
    /// and it goes through <see cref="StartPublishing"/> with the intent the author has just
    /// corrected.
    /// </summary>
    public string? ChangePublishFolder(string collection, string folder)
    {
        if (PublishError is { } blocked && blocked.Collection == collection
            && blocked.Kind == PublishErrorKind.BlockedForReview)
        {
            return blocked.Message;
        }
        return StartPublishing(collection, folder,
            CollectionsFile.Collections.TryGetValue(collection, out var entry) && entry.Publish is { } record
                ? record.Intent
                : PublishIntent.None);
    }

    /// <summary>
    /// What the author ticked in the sheet, for a collection that already publishes. The document
    /// is rewritten if the change makes it say something different. null on success.
    /// </summary>
    /// <param name="reviewedValues">
    /// From the dialog's Publish: replaces this machine's list of marked paths as
    /// <see cref="StartPublishing"/> describes. It is the one way a path leaves that list.
    /// </param>
    /// <param name="releasedValues">As <see cref="StartPublishing"/> takes them.</param>
    public string? UpdatePublishIntent(string collection, PublishIntent intent, IReadOnlySet<string>? reviewedValues = null,
                                       IReadOnlySet<string>? releasedValues = null)
    {
        if (CollectionsFile.Collections.GetValueOrDefault(collection) is not { Publish: { } record } entry)
        {
            return null;
        }
        // The lists are this machine's: another machine's publish record has no binding here.
        var binding = CollectionsCache.Published.GetValueOrDefault(collection);
        var changed = reviewedValues is not null && binding is not null
            ? new CollectionsLocalCache.PublishBinding(binding.Folder, binding.LastWrittenHash, reviewedValues,
                                                       Released(binding.ReleasedValues, releasedValues, reviewedValues),
                                                       binding.PublishedFolders, binding.Origin)
            : binding;
        var listsChanged = !Equals(changed, binding);
        if (record.Intent.Equals(intent) && !listsChanged)
        {
            return null;
        }
        if (listsChanged)
        {
            SetPublishBinding(collection, changed);
        }
        SetSidecarEntry(collection, new CollectionsFile.Entry(
            entry.Kind, entry.FileName, entry.RelativeToStore, entry.Origin, entry.Needs,
            new CollectionsFile.PublishRecord(record.Slug, record.Origin, intent), entry.Provenance));
        PersistStore();
        RaiseAll();
        return PublishError?.Collection == collection ? PublishError.Message : null;
    }

    /// <summary>
    /// Stop Publishing: the record and this machine's binding go, and the document in the folder
    /// stays unless the user asked for it too. The collection itself is untouched.
    /// </summary>
    public void StopPublishing(string collection, bool deleteFile)
    {
        if (CollectionsFile.Collections.GetValueOrDefault(collection) is not { Publish: { } record } entry)
        {
            return;
        }
        var folder = CollectionsCache.Published.GetValueOrDefault(collection)?.Folder;
        var stripped = new CollectionsFile.Entry(entry.Kind, entry.FileName, entry.RelativeToStore, entry.Origin,
                                                 entry.Needs, null, entry.Provenance);
        // An entry with nothing left to say is no entry at all, which is how the sidecar writes it
        // and how the next load reads it back.
        SetSidecarEntry(collection, stripped.Equals(CollectionsFile.Entry.Local) ? null : stripped);
        RememberWhatWasKeptBack(collection);
        if (PublishError?.Collection == collection)
        {
            PublishError = null;
        }
        PersistStore();
        // ${COLLECTION_DIR} has no folder here any more: Claude gets the token as written, and the
        // row's caution says why.
        if (collection == ActiveCollection && IsDirty)
        {
            PerformApply();
        }
        // After the save, so a failure here reaches the banner rather than being overwritten by it.
        if (deleteFile && folder is not null)
        {
            var target = Path.Combine(folder, record.Slug + "." + CollectionDocument.FileExtension);
            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = Friendly(ex);
            }
        }
        RaiseAll();
    }

    /// <summary>
    /// The document this collection travels as: every connector it holds, rendered through the
    /// publish intent. The origin is the one publishing fixed, so an export of a published
    /// collection is the same document the folder holds.
    /// </summary>
    /// <param name="only">
    /// The connectors to carry, for the Export dialog's ticked subset; null is the whole
    /// collection, which is what publishing always writes. Rendered from the subset rather than
    /// filtered afterwards, so nothing in the document describes a connector that is not in it.
    /// The origin travels unchanged, so a subset is indistinguishable by origin from the whole
    /// collection — deliberate: the origin says who published it, not how much of it.
    /// </param>
    /// <exception cref="PathMarkMovedException">
    /// A path mark cannot be placed on the argument it was made on, so a marked path is never
    /// written into a document as it stands. A mark for a connector the collection no longer holds
    /// counts, whatever the subset: it was made on one renamed or removed where the record could
    /// not follow — an older app, a hand edit, a master list that synced ahead of the file beside
    /// it — and the path it stood for may be travelling under another name.
    /// </exception>
    public CollectionDocument ExportDocument(string collection, PublishIntent intent,
                                             IReadOnlyList<string>? only = null)
    {
        var connectors = Store.Collections.TryGetValue(collection, out var held)
            ? held.Mcps.ToDictionary(pair => pair.Key, pair => pair.Value.Config, StringComparer.Ordinal)
            : new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        var orphan = intent.PathMarks
            .Where(pair => !connectors.ContainsKey(pair.Key) && pair.Value.Values.Any(mark => mark.Value is not null))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        if (orphan is not null)
        {
            throw new PathMarkMovedException(orphan);
        }
        if (only is not null)
        {
            var keep = only.ToHashSet(StringComparer.Ordinal);
            connectors = connectors.Where(pair => keep.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        return CollectionDocument.Export(
            collection,
            // No author setting exists yet; the field travels as absent rather than guessed at.
            author: null,
            CollectionsFile.Collections.GetValueOrDefault(collection)?.Publish?.Origin,
            IsoTimestamp.String(host.Now()),
            connectors,
            intent);
    }

    /// <summary>
    /// Export: the same document written once, wherever the user chose. null on success.
    ///
    /// It is refused as a publish is when it carries a path this machine keeps back
    /// (<see cref="KeptBack"/>); the dialog passes what it marks and what it released. The export
    /// binds nothing, so it leaves this machine's lists as they were.
    /// </summary>
    public string? WriteExport(string collection, PublishIntent intent, string path,
                               IReadOnlyList<string>? only = null, IReadOnlySet<string>? reviewed = null,
                               IReadOnlySet<string>? released = null)
    {
        try
        {
            var document = ExportDocument(collection, intent, only);
            RefuseKeptBackPaths(document, collection, reviewed, released);
            AtomicFile.Write(document.Serialize(), path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                   or PathMarkMovedException or PublishFolderCarriedException or KeptPathCarriedException)
        {
            return Friendly(ex);
        }
    }

    /// <summary>
    /// Publish now, whatever the recorded hash says: the sheet's Publish button pressed again
    /// after a write failed, where nothing about the document has changed and the only thing that
    /// did is that the folder is reachable again. null on success, else the message.
    /// </summary>
    public string? Republish(string collection)
    {
        if (!CollectionsCache.Published.ContainsKey(collection))
        {
            return null;
        }
        PublishIfChanged(collection);
        RaiseAll();
        return PublishError?.Collection == collection ? PublishError.Message : null;
    }

    /// <summary>
    /// Every collection this machine publishes, written when what it says has changed. Only the
    /// bindings in this machine's cache: the sidecar travels with the master list, so another
    /// machine's publish folder is recorded there but is that machine's to write.
    /// </summary>
    /// <param name="forced">
    /// The one collection whose write happens whether or not the document changed — a retry,
    /// where the recorded hash says the bytes are in a folder they never reached.
    /// </param>
    internal void PublishIfChanged(string? forced = null)
    {
        if (CollectionsCache.Published.Count == 0)
        {
            return;
        }
        var cacheChanged = false;
        foreach (var collection in CollectionsCache.Published.Keys.Order(StringComparer.Ordinal).ToList())
        {
            if (CollectionsCache.Published.GetValueOrDefault(collection) is not { } binding
                || CollectionsFile.Collections.GetValueOrDefault(collection)?.Publish is not { } record)
            {
                continue;
            }
            try
            {
                var document = ExportDocument(collection, record.Intent);
                // A path this machine has published as a placeholder, now in the document as
                // written: whatever the marks say — the other machine dropped them in a sidecar that
                // landed before its master list, a connector came back without them — this machine
                // does not send it. Only the author's Publish in the dialog clears it.
                RefuseKeptBackPaths(document, collection);
                var hash = PublishHash(document);
                if (hash == binding.LastWrittenHash && collection != forced)
                {
                    // The folder already holds what the store renders — the change that failed or
                    // was refused has been undone — so nothing is failing any more.
                    if (PublishError?.Collection == collection)
                    {
                        PublishError = null;
                    }
                    continue;
                }
                var target = Path.Combine(binding.Folder, record.Slug + "." + CollectionDocument.FileExtension);
                AtomicFile.Write(document.Serialize(), target);
                // Only ever added to here: a publish nobody reviewed may learn a path it now keeps
                // back, never forget one.
                var held = Store.Collections.TryGetValue(collection, out var heldCollection)
                    ? heldCollection.Mcps.ToDictionary(p => p.Key, p => p.Value.Config, StringComparer.Ordinal)
                    : new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                var placed = record.Intent.PlacedArguments(held).ToHashSet(StringComparer.Ordinal);
                SetPublishBinding(collection, new CollectionsLocalCache.PublishBinding(
                    binding.Folder, hash, binding.MarkedValues.Concat(placed),
                    // A path written as a placeholder is kept back again, so it is released no longer.
                    binding.ReleasedValues.Where(value => !placed.Contains(value)),
                    binding.PublishedFolders, binding.Origin));
                cacheChanged = true;
                if (PublishError?.Collection == collection)
                {
                    PublishError = null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or PathMarkMovedException or PublishFolderCarriedException or KeptPathCarriedException)
            {
                // The recorded hash is left as it was, so the next change tries this write again.
                // A mark that has moved lands here too, before anything is written: the document
                // already in the folder stays as it was, placeholder and all, until the author
                // marks the path again.
                PublishError = new CollectionPublishError(collection, Friendly(ex), PublishErrorKindOf(ex));
            }
        }
        // The cache save the write earned, through the same gate as every other one.
        if (!cacheChanged || !collectionsLoaded)
        {
            return;
        }
        try
        {
            CollectionsCache.Save(Service.Paths.CollectionsCachePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = Friendly(ex);
        }
    }

    /// <summary>
    /// What a document says, with the export stamp left out. The moment it was written is not part
    /// of its content, and hashing it would rewrite the shared folder on every store change —
    /// including the toggles that must never publish.
    /// </summary>
    private static string PublishHash(CollectionDocument document) =>
        ContentHash.Sha256(new CollectionDocument(
            document.Name, document.Author, document.Origin, string.Empty, document.Connectors).Serialize());

    /// <summary>The origin of the document at <paramref name="path"/>, or null when there is nothing readable there to vouch for it.</summary>
    private static string? ReadOrigin(string path)
    {
        try
        {
            return CollectionDocument.Decode(File.ReadAllBytes(path)).Origin;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or NotSupportedException or CollectionDocumentException)
        {
            return null;
        }
    }

    /// <summary><paramref name="path"/> is <paramref name="folder"/> itself or somewhere under it.</summary>
    private static bool IsInside(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return relative == "."
            || (!Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal) && relative != path);
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
                return failure.Kind == PublishErrorKind.BlockedForReview
                    ? new CollectionBanner.PublishBlocked(failure.Collection, failure.Message)
                    : new CollectionBanner.PublishFailed(failure.Collection, failure.Message);
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
    /// a file name to name in the request. An empty name counts as none, as it does in
    /// <see cref="SourceLocation"/>, so a malformed sidecar cannot raise "Locate …" over a blank
    /// while the chip and the menu stay silent about the same collection.
    /// </summary>
    private string? UnlocatedFileName(string collection) =>
        CollectionsFile.Collections.TryGetValue(collection, out var entry)
            && entry.Kind == CollectionKind.Synced
            && SourceBinding(collection)?.Path is null
            && !string.IsNullOrEmpty(entry.FileName)
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
        collectionsNote = null;
        // An unreadable sidecar loads as empty (see CollectionsFile.Load). Reconciling the cache
        // against that would drop every binding on this machine over a file a sync tool is
        // halfway through writing, so a sidecar that exists yet loads as empty is left alone and
        // whatever is already in memory stands. A genuinely empty sidecar reads the same way and
        // costs only a prune deferred to the next load.
        if (loaded.Collections.Count == 0 && File.Exists(Service.Paths.CollectionsFilePath))
        {
            collectionsLoaded = false;
            // At launch there is no in-memory state to stand yet, so the cache is taken as it
            // stands — unreconciled, since the sidecar that would vouch for it is the file that
            // cannot be read. A good set of bindings then outlives a bad sidecar, and the first
            // load that can read the sidecar again prunes whatever it no longer vouches for.
            if (!hasLoadedCollectionsOnce)
            {
                CollectionsCache = CollectionsLocalCache.Load(Service.Paths.CollectionsCachePath);
            }
            return;
        }
        CollectionsFile = loaded.Reconciled(Store);
        var cache = CollectionsLocalCache.Load(Service.Paths.CollectionsCachePath).Reconciled(CollectionsFile);
        // Only this machine writes its cache, so a record of the last apply made in memory is never
        // older than the file's — and it may be newer, when the save it waited for was held back.
        // The names that apply wrote are half of that record and travel with it: dropping them would
        // leave a collection recorded with no account of what it rendered.
        CollectionsCache = CollectionsCache.LastAppliedCollection is { } applied
            ? cache with { LastAppliedCollection = applied, LastAppliedNames = CollectionsCache.LastAppliedNames }
            : cache;
        ForgetOriginsOfDepartedCollections();
        collectionsLoaded = true;
        hasLoadedCollectionsOnce = true;
        // What is on disk NOW, not what this app last wrote: another machine's sidecar is the
        // file the next save has to differ from, or a change that happens to restore our old
        // bytes would be skipped and reverted by the reload after it.
        lastSavedSidecarHash = ReadSidecarHash();
        BindSourcesBesideTheStore();
        ArmSourceWatchers();
        RecomputePending();
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
        CollectionsCache = new CollectionsLocalCache(synced, CollectionsCache.Published, CollectionsCache.Kept,
                                                     CollectionsCache.LastAppliedCollection,
                                                     CollectionsCache.LastAppliedNames);
        try
        {
            CollectionsCache.Save(Service.Paths.CollectionsCachePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            collectionsNote = Friendly(ex);
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

    /// <summary>
    /// Which way a caught publish error did not land. A mark that has moved stops the write for the
    /// author to review, so another folder is no answer to it, and so does a path this machine
    /// keeps back or its own publish folder; anything else is a write that failed. A new reason to
    /// block a publish for review belongs here, so the banner can tell it from a failed write.
    /// </summary>
    internal static PublishErrorKind PublishErrorKindOf(Exception error) =>
        error is PathMarkMovedException or PublishFolderCarriedException or KeptPathCarriedException
            ? PublishErrorKind.BlockedForReview
            : PublishErrorKind.WriteFailed;

    /// <summary>
    /// Refuses <paramref name="document"/> when it carries, as written, a path this machine keeps
    /// back from <paramref name="collection"/>'s document (<see cref="KeptBack"/>), naming the
    /// connector and the field it sits in.
    /// </summary>
    internal void RefuseKeptBackPaths(CollectionDocument document, string collection,
                                      IReadOnlySet<string>? reviewed = null, IReadOnlySet<string>? released = null)
    {
        var (values, folders) = KeptBack(collection, reviewed, released);
        if (document.Findings(values).FirstOrDefault() is { } value)
        {
            throw new KeptPathCarriedException(value.Connector, FieldNameOf(value, collection));
        }
        if (document.Findings(folders).FirstOrDefault() is { } folder)
        {
            throw new PublishFolderCarriedException(folder.Connector, FieldNameOf(folder, collection));
        }
    }

    /// <summary>
    /// What a finding's field is called to the author: the editor's own words where the connector
    /// opens in the local form (<see cref="FieldName"/>), the document's name otherwise.
    /// </summary>
    internal string FieldNameOf(KeptValueFinding found, string collection) =>
        Store.Collections.GetValueOrDefault(collection)?.Mcps.GetValueOrDefault(found.Connector) is { } entry
            ? FieldName.Of(found.Field, entry.Config, found.Value)
            : FieldName.Document(found.Field);

    /// <summary>
    /// What this machine keeps back from <paramref name="collection"/>'s document. <c>Folders</c> are
    /// the collection's own: every folder this machine has published it into, which
    /// <c>${COLLECTION_DIR}</c> stands for, and none of them is ever released. <c>Values</c> are
    /// everything else, less the paths the author released for this collection — every path on any of its lists of marked paths, and every other folder
    /// it binds: another collection's publish folders, the folders of collections that have since
    /// left the store, and each synced collection's located folder. The lists are not the
    /// collection's own alone: Claude's file carries whichever collection was last applied, and a
    /// connector reaches another collection by a copy, an ingest or a restore with its paths intact.
    /// </summary>
    /// <param name="reviewed">From the Publish dialog, what the author has ticked there, added to the lists.</param>
    /// <param name="released">What they let go there, added to the collection's own released paths. A path that is ticked is not released.</param>
    internal (IReadOnlySet<string> Values, IReadOnlySet<string> Folders) KeptBack(
        string collection, IReadOnlySet<string>? reviewed = null, IReadOnlySet<string>? released = null)
    {
        var values = new HashSet<string>(reviewed ?? new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, binding) in CollectionsCache.Published)
        {
            values.UnionWith(binding.MarkedValues);
            var bound = binding.PublishedFolders.Append(binding.Folder);
            if (name == collection)
            {
                folders.UnionWith(bound);
            }
            else
            {
                values.UnionWith(bound);
            }
        }
        // Every mark in the sidecar's publish records, whichever machine made it: the sidecar travels
        // with the master list, so a collection the author publishes from another machine of their own
        // marks its paths here too. A colleague's collection is another matter — this machine never
        // sees their sidecar — and their document reaches it as placeholders anyway.
        foreach (var entry in CollectionsFile.Collections.Values)
        {
            if (entry.Publish is { } record)
            {
                values.UnionWith(record.Intent.PathMarks.Values
                    .SelectMany(marks => marks.Values).Select(mark => mark.Value).OfType<string>());
            }
        }
        // What a stopped publish left behind keeps its say, so publishing the collection again — or
        // another collection carrying one of its paths — is still refused.
        var ownOrigin = CollectionsFile.Collections.GetValueOrDefault(collection)?.Publish?.Origin;
        foreach (var (name, remembered) in CollectionsCache.Kept)
        {
            values.UnionWith(remembered.MarkedValues);
            // The folders of the collections that bore a record's name and left are no collection's
            // own here, whatever the record's origin says about the rest of it.
            values.UnionWith(remembered.DepartedFolders);
            // A record's folders are this collection's own — the ones ${COLLECTION_DIR} stands for,
            // never released — only where the record is this collection's. Anywhere else they are a
            // path it keeps back, released rather than written over and named as that collection's
            // in the dialog.
            if (IsOwn(remembered, name, collection, ownOrigin))
            {
                folders.UnionWith(remembered.PublishedFolders);
            }
            else
            {
                values.UnionWith(remembered.PublishedFolders);
            }
        }
        foreach (var (name, binding) in CollectionsCache.Synced)
        {
            if (IsSynced(name) && binding.Path is { } path && Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                values.Add(folder);
            }
        }
        var letGo = Released(CollectionsCache.Published.GetValueOrDefault(collection)?.ReleasedValues
                             ?? CollectionsCache.Kept.GetValueOrDefault(collection)?.ReleasedValues, released, reviewed);
        values.ExceptWith(folders);
        values.ExceptWith(letGo);
        // The collection's own folders are never let go: the token stands for them, and writing it in
        // their place is the one answer.
        return (values, folders);
    }

    /// <summary>
    /// The collection this machine binds <paramref name="folder"/> to: the one it publishes there,
    /// now or before, the name a collection that published there bore before it left, or the synced
    /// one whose document sits in it. Named in the dialog's note, so the author releasing another
    /// collection's folder reads whose it is first.
    /// </summary>
    internal string? CollectionBound(string folder)
    {
        var wanted = KeptValue.Nfc(folder);
        foreach (var name in CollectionsCache.Published.Keys.Order(StringComparer.Ordinal))
        {
            var binding = CollectionsCache.Published[name];
            if (binding.PublishedFolders.Append(binding.Folder).Any(bound => KeptValue.Nfc(bound) == wanted))
            {
                return name;
            }
        }
        foreach (var name in CollectionsCache.Kept.Keys.Order(StringComparer.Ordinal))
        {
            var remembered = CollectionsCache.Kept[name];
            // A departed collection's folder is still named for the collection it was filed under:
            // the name is what the author knows it by.
            if (remembered.PublishedFolders.Concat(remembered.DepartedFolders).Any(bound => KeptValue.Nfc(bound) == wanted))
            {
                return name;
            }
        }
        foreach (var name in CollectionsCache.Synced.Keys.Order(StringComparer.Ordinal))
        {
            if (IsSynced(name) && CollectionsCache.Synced[name].Path is { } path
                && Path.GetDirectoryName(path) is { Length: > 0 } folderOf && KeptValue.Nfc(folderOf) == wanted)
            {
                return name;
            }
        }
        return null;
    }

    /// <summary>A collection's released paths after the dialog's answer: what it released before and now, less anything the author ticks, since a ticked path is kept back again.</summary>
    internal static IReadOnlySet<string> Released(IEnumerable<string>? before, IEnumerable<string>? released, IEnumerable<string>? marked)
    {
        var result = new HashSet<string>(before ?? [], StringComparer.Ordinal);
        result.UnionWith(released ?? []);
        result.ExceptWith(marked ?? []);
        return result;
    }

    /// <summary>friendly(): the malformed-config case gets the guided message; everything else its own text.</summary>
    public static string Friendly(Exception error) => error switch
    {
        ClaudeConfigException malformed => MalformedConfigMessage(malformed.Detail),
        PathMarkMovedException moved => PathMarkMovedError(moved.Connector),
        KeptPathCarriedException kept => KeptPathCarriedError(kept.Connector, kept.Field),
        RestoreCollectionGoneException gone => RestoreCollectionGoneError(gone.Collection),
        PublishFolderCarriedException carried => PublishFolderCarriedError(carried.Connector, carried.Field),
        _ => error.Message,
    };

    public void Dispose()
    {
        disposed = true;
        notifier.RestartActionActivated -= OnRestartActionActivated;
        watcher?.Dispose();
        storeWatcher?.Dispose();
        watcher = null;
        storeWatcher = null;
        foreach (var watch in sourceWatchers.Values)
        {
            watch.Watcher.Dispose();
        }
        sourceWatchers.Clear();
    }
}

/// <summary>
/// A restore refused before anything was written: the backup was taken from a collection that no
/// longer exists. Its message is the one the restore dialog shows.
///
/// Mirror: <c>RestoreError.collectionGone</c> in Sources/ConnectorControlState/AppState.swift
/// </summary>
public sealed class RestoreCollectionGoneException(string collection)
    : Exception(AppState.RestoreCollectionGoneError(collection))
{
    public string Collection { get; } = collection;
}
