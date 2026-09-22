import Foundation
import Combine
import ConnectorControlCore

/// The app's state. Main-actor only; everything that arrives from another
/// thread comes through `AppHost.marshal`. Init sequence: resolve the
/// service, one-time permissions sweep, route the notification's Restart
/// Claude action, reload, arm the watchers.
///
/// Failure conventions used across this file and the models built on it:
/// UI validation a user can fix returns a `String?` message; I/O `throws`;
/// an optional parse or lookup that may legitimately find nothing returns
/// `nil`; a sheet-style confirmation flow reports success as `Bool` and
/// publishes its own error separately.
@MainActor
public final class AppState: ObservableObject {
    // MARK: - Strings
    // Every constant is one line so the string check can find it on its
    // `static let`/`static func` line. `chooseClaude` stays `nonisolated`:
    // ClaudeRestarter.verifyIsClaude reads it from a non-isolated context,
    // deliberately off the main thread while it validates a whole app bundle.

    public static let noConnectorsSubtitle = "No connectors configured"
    public static let claudeConfigRegeneratedBody = "Claude's config was changed outside Connector Control — regenerated from your connector list. Restart Claude to pick it up."
    public static let regenerationFailedBody = "The connector configuration changed, but Claude's config could not be updated — open Connector Control to retry."
    public static let claudeConfigChangedBody = "Claude's config changed outside Connector Control."
    public static let storeChangedBody = "The connector list changed outside Connector Control — review it before your next change is applied."
    public static let quitMessage = "Quit Connector Control?"
    public static let quitButton = "Quit"
    public static let restartMessage = "Restart Claude Desktop now?"
    public static let restartInformative = "Any in-progress Claude conversation will be interrupted."
    public static let restartButton = "Restart"
    public static let newCollectionTitle = "New Collection"
    public static let renameCollectionTitle = "Rename Collection"
    public static let deleteCollectionInformative = "Its connector list is removed; backups keep prior states."
    public static let deleteButton = "Delete"
    public static let nameEmptyError = "Name must not be empty."
    public static let lastLocalCollectionError = "The last local collection can’t be deleted."
    public static let locateCaution = "Locate the collection file to resolve paths."
    public static let ownCollectionError = "This is your own published collection."
    public static let newerDocumentError = "This collection was made by a newer Connector Control."
    public static let publishIntoStoreError = "Choose a folder other than the master list folder or its backups."
    public static let collectionsNotSavedNote = "Collections could not be saved: the collections file is unreadable. Your change is not on disk and will be lost when the file is read again."
    /// The platform named here is the platform-forced half: the caution names the OTHER one, so
    /// a Mac flags a Windows-authored connector and the Windows mirror says "authored on macOS".
    public static let authoredElsewhereCaution = "authored on Windows"
    public static let defaultClaudeAppPath = "/Applications/Claude.app"
    nonisolated public static let chooseClaude = "Choose the real Claude Desktop under Settings ▸ Claude."
    /// Claude's launch date is re-read 3 s after the restart completes.
    public static let restartRecheckDelay: TimeInterval = 3

    public static func deleteCollectionMessage(_ collection: String) -> String { "Delete Collection “\(collection)”?" }

    public static func duplicateNameError(_ name: String) -> String { "A connector named “\(name)” already exists." }

    public static func malformedConfigMessage(detail: String) -> String { "Claude's config file is not valid JSON (\(detail)). Nothing was written. Use Backups ▸ Restore… to recover it." }

    public static func enabledSubtitle(enabled: Int, total: Int) -> String { "\(enabled) of \(total) enabled" }

    public static func needsValueCaution(_ names: String) -> String { "needs your value: \(names)" }

    public static func collectionUpdateBanner(_ collection: String, _ summary: String) -> String { "\(collection) changed at its source: \(summary)." }

    /// "this Mac" is the platform-forced half of this sentence; the Windows mirror says "this PC".
    public static func collectionLocateBanner(_ collection: String) -> String { "\(collection)'s file isn’t on this Mac yet." }

    public static func collectionPublishFailedBanner(_ collection: String, _ folder: String, _ reason: String) -> String { "Couldn’t publish \(collection) to \(folder): \(reason)" }

    public static func collectionUpdateNotificationBody(_ collection: String, _ summary: String) -> String { "\(collection) changed at its source: \(summary). Review it in Connector Control." }

    public static func sourceUnreadableError(_ fileName: String, _ detail: String) -> String { "\(fileName) couldn’t be read: \(detail)" }

    public static func publishSlugTakenError(_ fileName: String) -> String { "\(fileName) already exists there and belongs to a different collection." }

    /// A synced connector-list change was adopted and written into Claude's config: say what it runs now.
    public static func connectorListChangedBody(_ delta: ServerDelta, restartRequired: Bool) -> String {
        let what = delta.isEmpty ? "was regenerated" : "now " + delta.summary()
        let then = restartRequired ? "Restart Claude to pick it up." : "Claude will use it the next time it starts."
        return "The connector list changed outside Connector Control — Claude's config \(what). \(then)"
    }

    // MARK: - Published state

    @Published public private(set) var store: MasterStore = .empty
    /// Settable: the restore sheet reports its failure here.
    @Published public var lastError: String?
    @Published public private(set) var needsClaudeRestart = false
    /// True when the last apply threw; keeps a retry affordance visible even
    /// after reload() refreshes lastError.
    @Published public private(set) var applyRetryNeeded = false
    /// mcpServers as last read from / written to Claude's file, for dirty tracking.
    @Published private(set) var appliedServers: [String: JSONValue] = [:]
    @Published public private(set) var service: ConfigService
    /// Which of the four launchers Claude Desktop can start: probed on demand
    /// and cached for the run. A tool absent here has not been probed yet.
    @Published public private(set) var toolStatuses: [Tool: ToolStatus] = [:]
    /// The sidecar beside the master list, reconciled with the store on every load.
    @Published public private(set) var collectionsFile = CollectionsFile(collections: [:])
    /// This machine's bindings, reconciled with the sidecar on every load.
    @Published public private(set) var collectionsCache = CollectionsLocalCache(synced: [:], published: [:])
    /// Synced collections whose source differs from what the store holds. Settable inside the
    /// module so the banner rules can be exercised without a source file behind them; the
    /// source watcher is what fills it in the app.
    @Published public internal(set) var pendingUpdates: [String: CollectionDiff] = [:]
    /// Collection → why its source could not be read, after repeated failures or a manual refresh.
    @Published public internal(set) var sourceErrors: [String: String] = [:]
    /// The last publish that failed, with the reason. Cleared by a write that succeeds.
    @Published public internal(set) var publishError: (collection: String, message: String)?

    /// The prompts AppState itself raises (quit, restart, collections); the editor owns its own.
    public let dialogs: Dialogs
    public let settings: AppSettings
    /// Raised when the app should terminate (after the optional confirmation).
    public var quitRequested: (() -> Void)?

    private let claude: ClaudeProcess
    private let notifier: Notifier
    private let paths: PathContext
    private let host: AppHost
    private let toolProbe: ToolProbing
    private var toolsInFlight: Set<Tool> = []
    private var watcher: FileWatcher?
    private var storeWatcher: FileWatcher?
    /// One watcher per bound synced collection, by collection name.
    private var sourceWatchers: [String: SourceWatch] = [:]
    /// The last render of each synced collection's document, with the bytes it came from. Kept
    /// in memory only: it is derived from a file this machine can read again at any time, and
    /// nothing outside the review sheet and the row cautions needs it.
    private var pendingRendered: [String: RenderedSource] = [:]
    /// Consecutive failed reads per collection, which decide the backoff and when to speak up.
    private var sourceFailures: [String: Int] = [:]
    /// Collections with a retry already waiting on the clock, so a burst of watcher events over
    /// one half-written file leaves one chain of attempts rather than one per event.
    private var sourceRetryScheduled: Set<String> = []
    /// The document hash each collection's update was last announced for, so one change is
    /// announced once however many times it is re-derived.
    private var notifiedSourceHashes: [String: String] = [:]
    private var hasLoadedOnce = false
    /// True once the sidecar has been read successfully at least once this run. Until then there
    /// is no in-memory state for an unreadable sidecar to protect.
    private var hasLoadedCollectionsOnce = false
    /// Whether the LAST load could read the sidecar (a file that is not there counts: there is
    /// nothing to protect). While it is false our copy of the sidecar may be empty or stale, so
    /// nothing writes it or the bindings that hang off it.
    private var collectionsLoaded = false
    /// The hash of the sidecar bytes on disk as this app last saw them — written or loaded — so
    /// a save that would change nothing costs neither a write nor a backup rotation, and a file
    /// another machine changed is still rewritten when our copy differs from it.
    private var lastSavedSidecarHash: String?
    /// A persist skipped the sidecar because the last load could not read it, so what the user
    /// changed about collections is in memory only. Cleared by the next save that lands.
    private var collectionsNotSaved = false
    /// A note from the collections load for `reload` to join with the service's own, since it
    /// assigns `lastError` after `loadCollections()` has run and would otherwise erase it.
    private var collectionsNote: String?
    private var disposed = false

    /// A source watcher and the path it was armed on, so a binding that moves gets a new watcher
    /// and one that did not is left alone (replacing it would re-baseline its last-seen mtime).
    private struct SourceWatch {
        let path: String
        let watcher: FileWatcher
    }

    /// One collection document as this platform renders it, with the bytes and the origin it was
    /// read from — what Apply needs, and what the row cautions read the author's platform from.
    private struct RenderedSource {
        var rendered: RenderedCollection
        var hash: String
        var origin: String?
    }

    /// Test probe: both watchers are live, which should hold true after every reload.
    var watchersArmed: Bool { (watcher?.isArmed ?? false) && (storeWatcher?.isArmed ?? false) }

    /// Test probe: which collections have a live source watcher.
    var watchedSourceCollections: [String] { sourceWatchers.keys.sorted() }

    /// Test probe: how many times a source document has been decoded and rendered. Re-deriving
    /// what is pending is cheap; decoding the same bytes again is what must not happen.
    private(set) var sourceRenders = 0

    /// Test probe: the watcher objects themselves, so a test can assert that a
    /// reload re-arms a dead watcher without replacing a live one (a
    /// replacement would re-baseline the last-seen mtime and lose an external
    /// write that lands in between).
    var watcherIdentities: (claude: ObjectIdentifier?, store: ObjectIdentifier?) {
        (watcher.map { ObjectIdentifier($0) }, storeWatcher.map { ObjectIdentifier($0) })
    }

    public init(settings: AppSettings, claude: ClaudeProcess, notifier: Notifier, dialogs: Dialogs,
                paths: PathContext, host: AppHost, toolProbe: ToolProbing) {
        self.settings = settings
        self.claude = claude
        self.notifier = notifier
        self.dialogs = dialogs
        self.paths = paths
        self.host = host
        self.toolProbe = toolProbe
        let resolved = AppState.makeService(settings: settings, paths: paths)
        service = resolved
        // Sweep the RESOLVED paths (a repointed store lives outside the default dir).
        PermissionsSweep.runOnce(settings: settings, paths: resolved.paths)
        // The notification's Restart Claude button routes back here. Skipping the
        // confirm-before-restart alert is deliberate: clicking the explicit action IS
        // the confirmation. Stale-click guard: an old notification must not restart a
        // Claude that already picked up the config.
        notifier.onRestartAction = { [weak self] in
            guard let self, self.needsClaudeRestart else { return }
            self.performRestartClaude()
        }
        reload()
        armWatchers()
    }

    // MARK: - Derived

    var isDirty: Bool { expandedServers != appliedServers }

    /// The enabled connectors as Claude must see them. Inside a synced collection this machine
    /// has located, `${COLLECTION_DIR}` resolves against the folder that document sits in; the
    /// store itself keeps the token, so the same list still resolves on the next machine. With
    /// nothing bound the token is written as it stands — guessing a folder would start the
    /// wrong program — and the row carries the caution that says so.
    var expandedServers: [String: JSONValue] {
        let servers = store.enabledServers
        guard isSynced(activeCollection), let path = sourceBinding(of: activeCollection)?.path else { return servers }
        let directory = URL(fileURLWithPath: path).deletingLastPathComponent().path
        return servers.mapValues { Placeholder.expandDirectoryToken(in: $0, directory: directory) }
    }

    public var sortedNames: [String] { store.mcps.keys.sorted() }

    public var collectionNames: [String] { store.collections.keys.sorted() }

    public var activeCollection: String { store.activeCollection }

    /// The popover header's subtitle.
    public var headerSubtitle: String {
        let total = store.mcps.count
        return total == 0
            ? AppState.noConnectorsSubtitle
            : AppState.enabledSubtitle(enabled: store.enabledServers.count, total: total)
    }

    // MARK: - Service construction

    public static func makeService(settings: AppSettings, paths: PathContext) -> ConfigService {
        var resolved = AppPaths.live(environment: paths.environment, appSupport: paths.appSupport)
        // Env override (dev sandboxing) beats the user setting; an empty value in
        // either one counts as absent (AppPaths.live applies the same rule).
        if (paths.environment[AppPaths.storeDirEnv] ?? "").isEmpty,
           let custom = settings.masterStoreDir, !custom.isEmpty {
            // Backups always stay machine-local: a synced store directory must
            // not fill the user's repo/cloud folder with rotating backups. The
            // staging folder stays there too: temp files must never be born in
            // the synced folder — AtomicFile.write takes it as its `staging:`
            // parameter, threaded through ConfigService/BackupManager. The
            // bindings cache is machine-local for a different reason: the paths
            // in it are true here and nowhere else, so a synced copy of it would
            // point every other machine at files it does not have.
            let machineLocal = AppPaths.live(environment: [:], appSupport: paths.appSupport)
            resolved = AppPaths(
                claudeConfigURL: resolved.claudeConfigURL,
                storeDirURL: URL(fileURLWithPath: custom),
                backupsDirURL: machineLocal.backupsDirURL,
                stagingDirURL: machineLocal.stagingDirURL,
                collectionsCacheURL: machineLocal.collectionsCacheURL)
        }
        return ConfigService(paths: resolved, keepCount: settings.backupKeepCount)
    }

    // MARK: - Watchers

    /// Replaces both watchers. Re-run on every repoint.
    private func armWatchers() {
        watcher?.stop()
        storeWatcher?.stop()
        watcher = FileWatcher(url: service.paths.claudeConfigURL, marshal: host.marshal) { [weak self] in
            self?.reload()
        }
        watcher?.start()
        storeWatcher = FileWatcher(url: service.paths.masterStoreURL, marshal: host.marshal) { [weak self] in
            self?.adoptExternalStoreChange()
        }
        storeWatcher?.start()
        armSourceWatchers()
    }

    /// One watcher per synced collection this machine has located, so a document changing in the
    /// shared folder becomes a pending update without anyone asking. A binding that has not moved
    /// keeps its watcher: replacing a live one re-baselines the last-seen mtime and opens a gap
    /// where a write is simply lost.
    private func armSourceWatchers() {
        var wanted: [String: String] = [:]
        for (name, entry) in collectionsFile.collections where entry.kind == .synced {
            if let path = collectionsCache.synced[name]?.path { wanted[name] = path }
        }
        for (name, watch) in sourceWatchers where wanted[name] != watch.path {
            watch.watcher.stop()
            sourceWatchers.removeValue(forKey: name)
        }
        for (name, path) in wanted where sourceWatchers[name] == nil {
            let watcher = FileWatcher(url: URL(fileURLWithPath: path), marshal: host.marshal) { [weak self] in
                self?.readSource(for: name, manual: false)
            }
            watcher.start()
            sourceWatchers[name] = SourceWatch(path: path, watcher: watcher)
        }
        // Same retry as the other two: arming fails while the folder is missing (a cloud folder
        // not yet synced down), and the next load tries again.
        for watch in sourceWatchers.values { AppState.reArm(watch.watcher) }
    }

    /// Only when arming previously failed (the parent directory did not exist).
    /// Never a blanket re-arm: a popover open reloads, and tearing the sources
    /// down each time would re-baseline the last-seen mtime and open a gap
    /// where an external write is simply lost.
    private static func reArm(_ watcher: FileWatcher?) {
        if let watcher, !watcher.isArmed { watcher.start() }
    }

    /// The store's mtime changed on disk. Classify before adopting: our own
    /// persistStore writes echo through this watcher (skip — memory already
    /// matches), a sync tool's mid-write partial parses as garbage (wait for
    /// the completed write to fire again — adopting it would rebuild the
    /// store from the local Claude config and clobber the synced list), and
    /// only a decodable store that differs from memory is a genuine outside
    /// edit to adopt and announce.
    private func adoptExternalStoreChange() {
        let storeURL = service.paths.masterStoreURL
        guard FileManager.default.fileExists(atPath: storeURL.path) else {
            // Deleted store file: reload's self-heal re-persists the
            // in-memory truth; nothing external to adopt or announce.
            reload(trigger: .quietStoreAdoption)
            return
        }
        guard let onDisk = MasterStoreIO.read(from: storeURL) else { return }
        guard onDisk != store else { return }
        reload(trigger: .externalStoreAdoption)
    }

    // MARK: - Repointing and restore

    /// Repoints the master store to a new directory (or back to the default when
    /// `dir` is nil). Seeds the new location from the current store if it has no
    /// mcps.json yet, rebuilds the service, and re-arms both watchers.
    public func repointStore(to dir: URL?) {
        let previousStoreURL = service.paths.masterStoreURL
        let previousMasterStoreDir = settings.masterStoreDir
        settings.masterStoreDir = dir?.path
        let rebuilt = AppState.makeService(settings: settings, paths: paths)
        let newStoreURL = rebuilt.paths.masterStoreURL
        let fm = FileManager.default
        if !fm.fileExists(atPath: newStoreURL.path), fm.fileExists(atPath: previousStoreURL.path) {
            // copyItem into a shared folder would inherit its ACEs; AtomicFile creates the
            // directory and the file private from the first instant.
            do {
                try AtomicFile.write(try Data(contentsOf: previousStoreURL), to: newStoreURL,
                                     staging: rebuilt.paths.stagingDirURL)
            } catch {
                // The new location is left seedless — repointing to it would silently
                // start empty — so the store stays where it was, settings included.
                lastError = AppState.friendly(error)
                settings.masterStoreDir = previousMasterStoreDir
                return
            }
        }
        service = rebuilt
        // The saved-sidecar hash describes the file in the old folder; the new one has its own.
        lastSavedSidecarHash = nil
        armWatchers()
        // An adopted (pre-existing) store is authoritative — reconciling it
        // against the local Claude config with fresh-launch "file wins"
        // semantics would clobber a synced list with local state.
        reload(trigger: .quietStoreAdoption)
    }

    /// Rebuilds the service from current settings (e.g. after backup retention
    /// changes) without moving the store directory or resetting the
    /// reconciliation baseline.
    public func refreshServiceSettings() {
        service = AppState.makeService(settings: settings, paths: paths)
        lastSavedSidecarHash = nil
        armWatchers()
    }

    /// Restores Claude's config from a backup and syncs the reconciliation
    /// baseline to the restored contents BEFORE reloading, so the app's own
    /// restore is not misread as an external change or a re-add.
    public func restoreClaudeConfig(from backup: URL) throws {
        let servers = try service.restoreClaudeConfig(from: backup, mergedWith: store)
        appliedServers = servers
        hasLoadedOnce = true
        settings.lastApplyDate = host.now()
        // ConfigService already merged and persisted the store; a quiet
        // adoption takes it as-is and suppresses notifications for the
        // user's own restore action.
        reload(trigger: .quietStoreAdoption)
    }

    // MARK: - Tools

    /// Probes `tools` off the main thread and publishes the results through
    /// the host; a tool already in flight is not probed twice.
    public func refreshTools(_ tools: [Tool] = Tool.allCases) {
        let wanted = tools.filter { toolsInFlight.insert($0).inserted }
        guard !wanted.isEmpty else { return }
        let probe = toolProbe
        let host = self.host
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let results = probe.probe(wanted)
            host.marshal { self?.publishToolStatuses(results, probed: wanted) }
        }
    }

    private func publishToolStatuses(_ results: [Tool: ToolStatus], probed: [Tool]) {
        toolsInFlight.subtract(probed)
        guard !disposed else { return }
        toolStatuses.merge(results) { _, new in new }
    }

    // MARK: - Restart-required derivation

    /// Claude needs a restart iff it is running on a config older than our last
    /// write. Derived from the process launch date, so it self-clears however
    /// Claude gets restarted — via us, by hand, or by an update.
    func refreshRestartState() {
        guard let lastApply = settings.lastApplyDate, claude.isRunning, let launched = claude.launchDate else {
            needsClaudeRestart = false
            return
        }
        needsClaudeRestart = launched < lastApply
    }

    // MARK: - Reload

    public func reload(trigger: ReloadTrigger = .routine) {
        do {
            // Capture "before" state for the notification rules below, computed
            // BEFORE any state is overwritten.
            let wasLoaded = hasLoadedOnce
            let previousApplied = appliedServers
            let previousStoreMcps = store.mcps

            // The store file vanished mid-session (deleted store dir, sync
            // eviction). The in-memory store is the source of truth — persist
            // it back rather than loading an empty store and regenerating
            // Claude's config down to nothing.
            if wasLoaded, !FileManager.default.fileExists(atPath: service.paths.masterStoreURL.path) {
                try service.saveStore(store)
            }

            let result = try service.loadAndReconcile(
                baseline: hasLoadedOnce ? appliedServers : nil,
                storeAuthoritative: trigger != .routine)
            store = result.store
            loadCollections()
            var claudeConfigChangedExternally = false
            if let servers = result.claudeServers {
                claudeConfigChangedExternally = wasLoaded && servers != previousApplied
                appliedServers = servers
                hasLoadedOnce = true
            }
            // Store-side external change that needs no regeneration (e.g. a
            // synced edit to a disabled connector) still deserves a heads-up
            // on the routine path.
            let storeChangedExternally =
                trigger == .routine
                && wasLoaded && result.store.mcps != previousStoreMcps
                && !claudeConfigChangedExternally
            // Every note, not just the first: with a corrupt store AND a
            // malformed Claude config, the second one is the actionable one
            // (Backups ▸ Restore… is the way out). The collections load runs above and adds
            // its own note here rather than setting lastError itself, which this line would
            // then overwrite.
            let notes = result.notes + [collectionsNote, collectionsNotSaved ? AppState.collectionsNotSavedNote : nil]
                .compactMap { $0 }
            lastError = notes.isEmpty ? nil : notes.joined(separator: " ")
            if !isDirty { applyRetryNeeded = false }

            // The store is the source of truth; Claude's config is downstream.
            // Any divergence from the render is regenerated away, arming the same
            // Restart Required footer as a user-made change. No loop: the
            // regenerating write satisfies the watcher-triggered follow-up reload.
            var regenerated = false
            var regenerationFailed = false
            if let servers = result.claudeServers, servers != expandedServers {
                let alreadyFailing = applyRetryNeeded
                performApply()
                regenerated = !applyRetryNeeded
                // Notify a failure only on the transition into it — retry
                // reloads (every popover open) must not re-post it.
                regenerationFailed = applyRetryNeeded && !alreadyFailing
            }

            // Fire notifications AFTER all state above has been assigned, never
            // on first load or for quiet adoptions. At most one per reload.
            if regenerated && wasLoaded && trigger == .routine && claudeConfigChangedExternally {
                notify(AppState.claudeConfigRegeneratedBody)
            } else if regenerated && wasLoaded && trigger == .externalStoreAdoption {
                // A remote (synced) connector-list change landed while nobody
                // was looking and has just been written into Claude's config.
                // Every mcpServers entry is a command Claude runs, so this is
                // announced every time, naming what changed: with Claude
                // running on the older config the notification offers the
                // restart; with Claude not running there is no restart to
                // offer, but the user still learns what starts next launch.
                let delta = ServerDelta(from: previousApplied, to: expandedServers)
                if needsClaudeRestart {
                    notify(AppState.connectorListChangedBody(delta, restartRequired: true),
                           category: Notifications.restartCategory)
                } else {
                    notify(AppState.connectorListChangedBody(delta, restartRequired: false))
                }
            } else if regenerationFailed && wasLoaded && trigger != .quietStoreAdoption {
                notify(AppState.regenerationFailedBody)
            } else if claudeConfigChangedExternally {
                notify(AppState.claudeConfigChangedBody)
            } else if storeChangedExternally {
                notify(AppState.storeChangedBody)
            }
        } catch {
            lastError = AppState.friendly(error)
        }
        // What this machine publishes follows the store it has just loaded: a change made on the
        // author's other machine arrives as a store change and reaches the team from here.
        publishIfChanged()
        refreshRestartState()
        AppState.reArm(watcher)
        AppState.reArm(storeWatcher)
        // The source watchers too: a load that could not read the sidecar returns before
        // armSourceWatchers, so one that dropped during that blip would otherwise stay dead
        // until the next save. reArm only starts the ones that are not already armed.
        for watch in sourceWatchers.values { AppState.reArm(watch.watcher) }
    }

    // MARK: - Apply / persist

    private func performApply() {
        do {
            let servers = expandedServers
            try service.apply(servers: servers)
            appliedServers = servers
            settings.lastApplyDate = host.now()
            refreshRestartState()
            // Clearing the banner keeps what the collections files still have to say: the apply
            // succeeding does not mean the sidecar was written.
            lastError = collectionsNotSaved ? AppState.collectionsNotSavedNote : nil
            applyRetryNeeded = false
        } catch {
            lastError = AppState.friendly(error)
            applyRetryNeeded = true
        }
    }

    /// The master list first, then the sidecar beside it, then this machine's cache — the order
    /// a crash has to survive: a half-done write leaves the collection loading as an ordinary
    /// local one until the next save, and loses nothing the app cannot rebuild. One `catch` for
    /// the three: the first failure stops the chain, since a sidecar written against a master
    /// list that never landed would describe collections that do not exist.
    private func persistStore() {
        // The store just changed, so what a source document would change with it may have too —
        // and the excluded lists a re-render produces belong in the cache this save writes.
        recomputePending()
        do {
            try service.saveStore(store)
            // A sidecar that the last load could not read is one something else is writing.
            // Our copy of it may never have been filled, and saving that would land an empty
            // sidecar — and, behind it, a cache pruned against nothing — on top of the real
            // file the moment the other writer finishes. Both wait for a load that can read it.
            // The master list is ours alone and always saves.
            if collectionsLoaded {
                try saveCollectionsIfChanged()
                try collectionsCache.save(to: service.paths.collectionsCacheURL, staging: service.paths.stagingDirURL)
                collectionsNotSaved = false
                // Only ever our own note: a real failure's message stays where it is.
                if lastError == AppState.collectionsNotSavedNote { lastError = nil }
            } else {
                // Nothing was written. The user just changed something about collections and it
                // exists only in memory, which is worth saying rather than looking like a save.
                collectionsNotSaved = true
                lastError = AppState.collectionsNotSavedNote
            }
        } catch {
            lastError = AppState.friendly(error)
        }
        // Every binding change reaches disk through here: subscribe, locate, stop syncing,
        // rename and delete all end in a save, so this is where the watchers follow them.
        armSourceWatchers()
        // Publishing is derived from the store on every store change, never from a save event:
        // the store has just changed, so what the team reads may have to change with it.
        publishIfChanged()
    }

    /// The sidecar only when it would differ: every connector change persists the store, and
    /// most of them say nothing new about collections. Rewriting the same bytes would rotate a
    /// backup and churn a file that travels through someone's sync tool for nothing.
    private func saveCollectionsIfChanged() throws {
        let data = try collectionsFile.encode().serialized()
        let hash = ContentHash.sha256(data)
        guard hash != lastSavedSidecarHash else { return }
        try service.saveCollections(collectionsFile)
        lastSavedSidecarHash = hash
    }

    /// Toggles take effect immediately; the Restart Required button is the only follow-up step.
    public func setEnabled(_ name: String, _ on: Bool) {
        store.mcps[name]?.enabled = on
        persistStore()
        performApply()
    }

    /// The popover's retry button: unconditional.
    public func apply() {
        performApply()
    }

    /// Editor-window flow: saving there is a deliberate final act, so apply
    /// immediately — but only if something changed.
    public func applyInteractively() {
        guard isDirty else { return }
        performApply()
    }

    /// Validates and saves an entry. Returns an error message, or nil on success.
    public func upsert(name: String, entry: MCPEntry, renamedFrom oldName: String?) -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return AppState.nameEmptyError }
        if trimmed != oldName, store.mcps[trimmed] != nil {
            return AppState.duplicateNameError(trimmed)
        }
        if let old = oldName, old != trimmed { store.mcps.removeValue(forKey: old) }
        store.mcps[trimmed] = entry
        persistStore()
        return nil
    }

    /// Removes and persists; the caller applies (the editor's remove flow does both in one turn).
    public func remove(name: String) {
        store.mcps.removeValue(forKey: name)
        persistStore()
    }

    // MARK: - Quit

    public func quitApp() {
        if settings.confirmBeforeQuit,
           !dialogs.confirm(message: AppState.quitMessage, informative: nil, primary: AppState.quitButton) {
            return
        }
        quitRequested?()
    }

    // MARK: - Restart Claude

    /// The in-app button: confirm (unless disabled), then restart.
    public func restartClaude() {
        if settings.confirmBeforeRestart,
           !dialogs.confirm(message: AppState.restartMessage, informative: AppState.restartInformative,
                            primary: AppState.restartButton) {
            return
        }
        performRestartClaude()
    }

    /// Restart with no confirmation: after the in-app confirm, or from the
    /// notification action where the deliberate click is the confirmation.
    /// The completion may arrive on any thread; it is marshalled like every
    /// other off-main result.
    public func performRestartClaude() {
        let host = self.host
        claude.restart { [weak self] message in
            host.marshal {
                guard let self, !self.disposed else { return }
                self.lastError = message   // nil on success clears any prior banner
                self.refreshRestartState()
                host.delay(AppState.restartRecheckDelay) { [weak self] in
                    guard let self, !self.disposed else { return }
                    self.refreshRestartState()
                }
            }
        }
    }

    // MARK: - Collections

    /// Switching collections applies immediately, like every other change. An unknown name is silently ignored.
    public func switchCollection(to name: String) {
        guard store.switchCollection(to: name) == nil else { return }
        persistStore()
        performApply()
    }

    /// Copies the active collection under a new name and makes it active, as the chip menu's
    /// New Collection has always done. nil on success, else the message to show.
    public func createCollection(named name: String) -> String? {
        if let error = store.addCollection(named: name, copyingCurrent: true) { return error }
        persistStore()
        performApply()
        return nil
    }

    /// Renames a collection wherever its name is a key: the master list, the sidecar entry, this
    /// machine's bindings, and the derived state the banner reads. nil on success.
    public func renameCollection(_ name: String, to newName: String) -> String? {
        if let error = store.renameCollection(name, to: newName) { return error }
        let trimmed = newName.trimmingCharacters(in: .whitespaces)
        if trimmed != name {
            move(&collectionsFile.collections, from: name, to: trimmed)
            move(&collectionsCache.synced, from: name, to: trimmed)
            move(&collectionsCache.published, from: name, to: trimmed)
            move(&pendingUpdates, from: name, to: trimmed)
            move(&sourceErrors, from: name, to: trimmed)
            move(&pendingRendered, from: name, to: trimmed)
            move(&sourceFailures, from: name, to: trimmed)
            move(&notifiedSourceHashes, from: name, to: trimmed)
            if let failure = publishError, failure.collection == name {
                publishError = (collection: trimmed, message: failure.message)
            }
        }
        persistStore()
        performApply()
        return nil
    }

    /// Deletes a collection and everything keyed by its name. A synced collection's source file
    /// is never touched — only this machine's binding to it goes. nil on success.
    public func deleteCollection(named name: String) -> String? {
        // There must always be somewhere to add a connector, and only a local collection takes
        // one — so the last local collection stays even when synced ones remain beside it. The
        // store still owns "no collection by that name": a name it does not have is not the
        // last anything, and its own message is the one to show.
        if store.collections[name] != nil, kind(of: name) == .local, localCollectionNames.count <= 1 {
            return AppState.lastLocalCollectionError
        }
        if let error = store.deleteCollection(named: name) { return error }
        collectionsFile.collections.removeValue(forKey: name)
        collectionsCache.synced.removeValue(forKey: name)
        collectionsCache.published.removeValue(forKey: name)
        pendingUpdates.removeValue(forKey: name)
        sourceErrors.removeValue(forKey: name)
        forgetSource(name)
        if publishError?.collection == name { publishError = nil }
        persistStore()
        performApply()
        return nil
    }

    private func move<Value>(_ dictionary: inout [String: Value], from name: String, to newName: String) {
        if let value = dictionary.removeValue(forKey: name) { dictionary[newName] = value }
    }

    // MARK: - Collection kinds and bindings

    public func kind(of collection: String) -> CollectionKind { collectionsFile.kind(of: collection) }

    public func isSynced(_ collection: String) -> Bool { kind(of: collection) == .synced }

    public func isPublished(_ collection: String) -> Bool { collectionsFile.collections[collection]?.publish != nil }

    public func sourceBinding(of collection: String) -> CollectionsLocalCache.SyncedBinding? {
        collectionsCache.synced[collection]
    }

    public var activeCollectionIsSynced: Bool { isSynced(activeCollection) }

    public var localCollectionNames: [String] { collectionNames.filter { kind(of: $0) == .local } }

    /// What the last Apply asked the user to fill in for one connector, by marker name.
    public func needs(of connector: String, in collection: String) -> [String: CollectionsFile.Need] {
        collectionsFile.collections[collection]?.needs[connector] ?? [:]
    }

    /// The collections-level caution for one row, distinct from the tool caution the launcher
    /// probe produces. The markers in the stored config are the source of truth, not the
    /// sidecar's `needs`: a detached collection keeps its unfilled markers after the needs are
    /// gone, and the row must still say so.
    ///
    /// The "authored on <platform>" caution reads the launcher platform out of the last render
    /// of the bound document: only a local connector carries one, and only the other platform's
    /// is worth saying anything about.
    public func connectorCaution(_ connector: String, in collection: String) -> String? {
        guard let config = store.collections[collection]?.mcps[connector]?.config else { return nil }
        let unfilled = Placeholder.markers(in: config).flatMap(\.names)
        if !unfilled.isEmpty {
            // Ordered by first appearance and de-duplicated across leaves, so the sentence is
            // stable between two reads of the same config.
            var seen: Set<String> = []
            return AppState.needsValueCaution(unfilled.filter { seen.insert($0).inserted }.joined(separator: ", "))
        }
        if let authored = pendingRendered[collection]?.rendered.connectors[connector]?.authoredOn,
           authored != CollectionPlatform.current {
            return AppState.authoredElsewhereCaution
        }
        if Placeholder.usesDirectoryToken(config), isSynced(collection), sourceBinding(of: collection)?.path == nil {
            return AppState.locateCaution
        }
        return nil
    }

    // MARK: - Synced collections

    /// Backoff for a source that could not be read: a sync tool's half-written file, or a cloud
    /// placeholder that has not hydrated yet, is the common case and fixes itself in seconds.
    private static let sourceRetryDelays: [TimeInterval] = [2, 10, 30]

    /// Subscribes to a collection document: the collection is created with what the document
    /// describes, every connector disabled, this machine's binding recorded, and the sidecar
    /// told what each connector still asks the user for. nil on success, else the message.
    ///
    /// The new collection does not become the active one. Everything in it arrives disabled, so
    /// switching would empty Claude's config the moment anyone subscribed.
    public func subscribe(documentAt path: String, as requestedName: String?) -> String? {
        let url = URL(fileURLWithPath: path).standardizedFileURL
        let data: Data
        do {
            data = try Data(contentsOf: url)
        } catch {
            return AppState.sourceUnreadableError(url.lastPathComponent, AppState.sourceDetail(error))
        }
        let document: CollectionDocument
        do {
            document = try CollectionDocument.decode(data)
        } catch CollectionDocumentError.newerFormat {
            return AppState.newerDocumentError
        } catch {
            return AppState.sourceUnreadableError(url.lastPathComponent, AppState.sourceDetail(error))
        }
        // Subscribing to what this machine publishes would make the app its own author: every
        // local edit would come straight back as a pending update to itself.
        if let origin = document.origin,
           collectionsFile.collections.values.contains(where: { $0.publish?.origin == origin }) {
            return AppState.ownCollectionError
        }
        let requested = requestedName?.trimmingCharacters(in: .whitespaces) ?? ""
        let name = requested.isEmpty ? document.name : requested
        let active = store.activeCollection
        if let error = store.addCollection(named: name, copyingCurrent: false) { return error }
        store.activeCollection = active
        let rendered = document.render()
        let result = CollectionApply.apply(rendered: rendered, current: [:], previousNeeds: [:])
        store.collections[name] = Collection(mcps: result.entries)
        collectionsFile.collections[name] = CollectionsFile.Entry(
            kind: .synced, fileName: url.lastPathComponent, relativeToStore: relativeToStore(url),
            origin: document.origin, needs: result.needs)
        let hash = ContentHash.sha256(data)
        collectionsCache.synced[name] = CollectionsLocalCache.SyncedBinding(
            path: url.path, lastHash: hash, excluded: rendered.excluded)
        pendingRendered[name] = RenderedSource(rendered: rendered, hash: hash, origin: document.origin)
        notifiedSourceHashes[name] = hash
        persistStore()
        return nil
    }

    /// Points a synced collection at its document on this machine. nil on success, else the
    /// message: a file that cannot be read or decoded is not bound, so the Locate banner stays.
    public func locateSource(for collection: String, path: String) -> String? {
        // Only a synced collection has a document to point at; anything else is silently
        // ignored, as switching to a collection that does not exist is.
        guard isSynced(collection) else { return nil }
        let url = URL(fileURLWithPath: path).standardizedFileURL
        let data: Data
        do {
            data = try Data(contentsOf: url)
        } catch {
            return AppState.sourceUnreadableError(url.lastPathComponent, AppState.sourceDetail(error))
        }
        do {
            _ = try CollectionDocument.decode(data)
        } catch CollectionDocumentError.newerFormat {
            return AppState.newerDocumentError
        } catch {
            return AppState.sourceUnreadableError(url.lastPathComponent, AppState.sourceDetail(error))
        }
        collectionsCache.synced[collection] = CollectionsLocalCache.SyncedBinding(
            path: url.path, lastHash: ContentHash.sha256(data),
            excluded: collectionsCache.synced[collection]?.excluded ?? [:])
        // Only ever set, never cleared: where the document sits relative to the store is a fact
        // every machine shares, and this one finding it elsewhere does not make it untrue.
        if let relative = relativeToStore(url) {
            collectionsFile.collections[collection]?.relativeToStore = relative
        }
        collectionsFile.collections[collection]?.fileName = url.lastPathComponent
        sourceFailures.removeValue(forKey: collection)
        sourceRetryScheduled.remove(collection)
        sourceErrors.removeValue(forKey: collection)
        // persistStore re-derives what the newly bound document would change.
        persistStore()
        // ${COLLECTION_DIR} resolves against the folder just bound, so what Claude runs changes
        // the moment the file is found — with no second click. The dirty check keeps a locate
        // that resolves to nothing new from rewriting Claude's config for the sake of it.
        if collection == activeCollection, isDirty { performApply() }
        return nil
    }

    /// The Refresh button: read the source now, and report a failure at once rather than giving
    /// it the three chances a watcher-driven read allows.
    public func refreshSource(for collection: String) {
        readSource(for: collection, manual: true)
    }

    /// Adopts what the source says, through the normal store path: a value the user filled in
    /// follows its marker wherever the author moved it, enabled flags survive, added connectors
    /// arrive disabled, and the sidecar's needs are rewritten from the new render. nil on
    /// success; nothing pending is a no-op, so a second Apply cannot undo the first.
    @discardableResult
    public func applyPendingUpdate(for collection: String) -> String? {
        guard pendingUpdates[collection] != nil, let source = pendingRendered[collection] else { return nil }
        let result = CollectionApply.apply(
            rendered: source.rendered,
            current: store.collections[collection]?.mcps ?? [:],
            previousNeeds: collectionsFile.collections[collection]?.needs ?? [:])
        store.collections[collection] = Collection(mcps: result.entries)
        collectionsFile.collections[collection]?.needs = result.needs
        collectionsFile.collections[collection]?.origin = source.origin
        collectionsCache.synced[collection]?.lastHash = source.hash
        collectionsCache.synced[collection]?.excluded = source.rendered.excluded
        // Cleared BEFORE the save: persistStore re-derives what is pending from the document and
        // the store it is about to write, and clearing afterwards would throw that answer away.
        pendingUpdates.removeValue(forKey: collection)
        notifiedSourceHashes[collection] = source.hash
        persistStore()
        // Claude only runs the active collection, so only that one reaches its config.
        if collection == activeCollection { performApply() }
        return nil
    }

    /// Stop Syncing: the collection keeps its connectors, its filled values and its enabled
    /// flags and becomes an ordinary local one. Unfilled markers stay as text, so a row still
    /// says what it needs — without the hint, which travelled with the document.
    public func stopSyncing(_ collection: String) {
        guard isSynced(collection) else { return }
        // The token resolved against the bound document's folder for as long as there was one.
        // A local collection has no document, so the folder it resolved to is written into the
        // configs once, exactly as an imported copy expands it — otherwise a path that worked a
        // second ago would become the literal token, with nothing left to explain it.
        if let path = collectionsCache.synced[collection]?.path {
            let directory = URL(fileURLWithPath: path).deletingLastPathComponent().path
            for (name, entry) in store.collections[collection]?.mcps ?? [:]
            where Placeholder.usesDirectoryToken(entry.config) {
                store.collections[collection]?.mcps[name]?.config =
                    Placeholder.expandDirectoryToken(in: entry.config, directory: directory)
            }
        }
        collectionsFile.collections.removeValue(forKey: collection)
        collectionsCache.synced.removeValue(forKey: collection)
        pendingUpdates.removeValue(forKey: collection)
        sourceErrors.removeValue(forKey: collection)
        forgetSource(collection)
        persistStore()
        // The expansion above changed what the collection holds; if it is the live one, that is
        // a change to what Claude runs. Baking in the same folder the token already resolved to
        // normally leaves the two identical, and the dirty check spares the write.
        if collection == activeCollection, isDirty { performApply() }
    }

    /// The document the review sheet lists, as this platform renders it.
    public func pendingDocument(for collection: String) -> RenderedCollection? {
        pendingRendered[collection]?.rendered
    }

    /// The identity of the bytes `pendingDocument(for:)` was rendered from. The review sheet
    /// holds on to it so Apply can tell that the document it listed is still the document it
    /// would land.
    public func pendingSourceHash(for collection: String) -> String? {
        pendingRendered[collection]?.hash
    }

    /// Every bound synced collection's pending update, re-derived from its document and the
    /// store as it stands now: pending is derived, never stored as a fact.
    func recomputePending() {
        for name in collectionsFile.collections.keys.sorted()
        where collectionsFile.collections[name]?.kind == .synced && collectionsCache.synced[name]?.path != nil {
            readSource(for: name, manual: false)
        }
    }

    /// Reads one collection's document and derives what it would change. Nothing here reaches
    /// Claude's config: that takes `applyPendingUpdate`.
    private func readSource(for collection: String, manual: Bool) {
        guard let binding = collectionsCache.synced[collection], let path = binding.path else { return }
        let url = URL(fileURLWithPath: path)
        let data: Data
        do {
            data = try Data(contentsOf: url)
        } catch {
            noteSourceFailure(collection, AppState.sourceUnreadableError(url.lastPathComponent, AppState.sourceDetail(error)),
                              manual: manual)
            return
        }
        let hash = ContentHash.sha256(data)
        let rendered: RenderedCollection
        if let kept = pendingRendered[collection], kept.hash == hash {
            // The same bytes as last time: the render they produce cannot have changed, so only
            // the diff below is re-derived. The store moves under it constantly — a local edit,
            // another machine's apply arriving — and the answer has to follow it.
            rendered = kept.rendered
        } else {
            let document: CollectionDocument
            do {
                document = try CollectionDocument.decode(data)
            } catch CollectionDocumentError.newerFormat {
                // No amount of waiting makes this readable, so it is said at once and never retried.
                sourceFailures.removeValue(forKey: collection)
                sourceErrors[collection] = AppState.newerDocumentError
                return
            } catch {
                noteSourceFailure(collection, AppState.sourceUnreadableError(url.lastPathComponent, AppState.sourceDetail(error)),
                                  manual: manual)
                return
            }
            rendered = document.render()
            sourceRenders += 1
            pendingRendered[collection] = RenderedSource(rendered: rendered, hash: hash, origin: document.origin)
        }
        sourceFailures.removeValue(forKey: collection)
        sourceRetryScheduled.remove(collection)
        sourceErrors.removeValue(forKey: collection)
        collectionsCache.synced[collection]?.excluded = rendered.excluded
        let diff = CollectionDiff.pending(rendered: rendered, current: store.collections[collection]?.mcps ?? [:])
        guard !diff.isEmpty else {
            pendingUpdates.removeValue(forKey: collection)
            return
        }
        pendingUpdates[collection] = diff
        // Once per document: the same change is re-derived on every store change, and the user
        // hears about it once.
        guard notifiedSourceHashes[collection] != hash else { return }
        // Recorded before the first-load gate, not after it: an update that was already there
        // when the app opened is not news, and it must not become news on the next reload.
        notifiedSourceHashes[collection] = hash
        guard hasLoadedOnce else { return }
        notify(AppState.collectionUpdateNotificationBody(collection, diff.summary()))
    }

    /// A failed read. A watcher-driven one backs off and tries again — the third consecutive
    /// failure is the one the user hears about; Refresh, with someone waiting for an answer,
    /// reports the first.
    private func noteSourceFailure(_ collection: String, _ message: String, manual: Bool) {
        let failures = (sourceFailures[collection] ?? 0) + 1
        sourceFailures[collection] = failures
        if manual || failures >= AppState.sourceRetryDelays.count {
            sourceErrors[collection] = message
        }
        guard !manual, failures <= AppState.sourceRetryDelays.count,
              sourceRetryScheduled.insert(collection).inserted else { return }
        host.delay(AppState.sourceRetryDelays[failures - 1]) { [weak self] in
            guard let self, !self.disposed else { return }
            self.sourceRetryScheduled.remove(collection)
            // A read that succeeded in the meantime leaves nothing to retry.
            guard self.sourceFailures[collection] != nil else { return }
            self.readSource(for: collection, manual: false)
        }
    }

    /// Everything this run knows about one collection's document, dropped when the collection
    /// stops being synced or goes away.
    private func forgetSource(_ collection: String) {
        pendingRendered.removeValue(forKey: collection)
        sourceFailures.removeValue(forKey: collection)
        sourceRetryScheduled.remove(collection)
        notifiedSourceHashes.removeValue(forKey: collection)
    }

    /// Where `url` sits inside the store's own folder, or nil when it is somewhere else. A
    /// document that travels with the master list is found again on every machine from this.
    private func relativeToStore(_ url: URL) -> String? {
        let base = service.paths.storeDirURL.standardizedFileURL.path
        let path = url.standardizedFileURL.path
        guard path.hasPrefix(base + "/") else { return nil }
        return String(path.dropFirst(base.count + 1))
    }

    /// The detail half of a source failure: the document decoder's own words where it has any,
    /// since "not JSON at line 3" is what tells the user which file to look at.
    private static func sourceDetail(_ error: Error) -> String {
        if case CollectionDocumentError.malformed(let detail) = error { return detail }
        return error.localizedDescription
    }

    // MARK: - Publishing and export

    /// Starts publishing a local collection into `folder`, or re-points one that already
    /// publishes (the failed-write banner's Choose Folder…). The slug and the origin are fixed
    /// the first time and never re-derived, so renaming the collection cannot orphan the document
    /// the team already subscribed to. The document is written before this returns.
    /// nil on success, else the message to show.
    public func startPublishing(_ collection: String, to folder: String, intent: PublishIntent) -> String? {
        // A synced collection has an author elsewhere, and a name that is not a collection has
        // nothing to publish. Nothing offers either, so both get the silence `locateSource` gives
        // a collection that is not synced.
        guard store.collections[collection] != nil, !isSynced(collection) else { return nil }
        // A record the next save cannot write would leave a document in a shared folder that
        // nothing here remembers publishing. It waits for a sidecar that can be read.
        guard collectionsLoaded else { return AppState.collectionsNotSavedNote }
        let url = URL(fileURLWithPath: folder).standardizedFileURL
        // The master list's own folder is synced to every machine the user owns, and the backups
        // folder is rotated; a document in either would be swept up by machinery that is not
        // about publishing at all.
        guard !AppState.isInside(url, service.paths.storeDirURL),
              !AppState.isInside(url, service.paths.backupsDirURL) else {
            return AppState.publishIntoStoreError
        }
        let record = collectionsFile.collections[collection]?.publish
        let slug = record?.slug ?? Slug.make(collection)
        let origin = record?.origin ?? UUID().uuidString.lowercased()
        let fileName = slug + "." + CollectionDocument.fileExtension
        let target = url.appendingPathComponent(fileName)
        // Somebody else's document under the name this one would take: publishing over it would
        // replace what their subscribers follow. A file that cannot be decoded counts too — it
        // has no origin to vouch for it.
        if FileManager.default.fileExists(atPath: target.path),
           (try? CollectionDocument.decode(try Data(contentsOf: target)))?.origin != origin {
            return AppState.publishSlugTakenError(fileName)
        }
        var entry = collectionsFile.collections[collection] ?? .local
        entry.publish = CollectionsFile.PublishRecord(slug: slug, origin: origin, intent: intent)
        collectionsFile.collections[collection] = entry
        let previous = collectionsCache.published[collection]
        collectionsCache.published[collection] = CollectionsLocalCache.PublishBinding(
            folder: url.path,
            // A new folder has nothing in it this app wrote, so the next write is unconditional.
            lastWrittenHash: previous?.folder == url.path ? previous?.lastWrittenHash : nil)
        // persistStore ends in publishIfChanged, which is what writes the document.
        persistStore()
        return publishError?.collection == collection ? publishError?.message : nil
    }

    /// What the author ticked in the sheet, for a collection that already publishes. The document
    /// is rewritten if the change makes it say something different. nil on success.
    public func updatePublishIntent(_ collection: String, intent: PublishIntent) -> String? {
        guard var entry = collectionsFile.collections[collection], let record = entry.publish,
              record.intent != intent else { return nil }
        entry.publish = CollectionsFile.PublishRecord(slug: record.slug, origin: record.origin, intent: intent)
        collectionsFile.collections[collection] = entry
        persistStore()
        return publishError?.collection == collection ? publishError?.message : nil
    }

    /// Stop Publishing: the record and this machine's binding go, and the document in the folder
    /// stays unless the user asked for it too. The collection itself is untouched.
    public func stopPublishing(_ collection: String, deleteFile: Bool) {
        guard var entry = collectionsFile.collections[collection], let record = entry.publish else { return }
        let folder = collectionsCache.published[collection]?.folder
        entry.publish = nil
        // An entry with nothing left to say is no entry at all, which is how the sidecar writes
        // it and how the next load reads it back.
        if entry == .local {
            collectionsFile.collections.removeValue(forKey: collection)
        } else {
            collectionsFile.collections[collection] = entry
        }
        collectionsCache.published.removeValue(forKey: collection)
        if publishError?.collection == collection { publishError = nil }
        persistStore()
        // After the save, so a failure here reaches the banner rather than being overwritten by it.
        guard deleteFile, let folder else { return }
        let target = URL(fileURLWithPath: folder).appendingPathComponent(record.slug + "." + CollectionDocument.fileExtension)
        guard FileManager.default.fileExists(atPath: target.path) else { return }
        do {
            try FileManager.default.removeItem(at: target)
        } catch {
            lastError = AppState.friendly(error)
        }
    }

    /// The document this collection travels as: every connector it holds, rendered through the
    /// publish intent. The origin is the one publishing fixed, so an export of a published
    /// collection is the same document the folder holds.
    public func exportDocument(for collection: String, intent: PublishIntent) -> CollectionDocument {
        CollectionDocument.export(
            name: collection,
            // No author setting exists yet; the field travels as absent rather than guessed at.
            author: nil,
            origin: collectionsFile.collections[collection]?.publish?.origin,
            exported: IsoTimestamp.string(from: host.now()),
            connectors: (store.collections[collection]?.mcps ?? [:]).mapValues(\.config),
            intent: intent)
    }

    /// Export: the same document written once, wherever the user chose. nil on success.
    public func writeExport(for collection: String, intent: PublishIntent, to path: String) -> String? {
        do {
            try AtomicFile.write(exportDocument(for: collection, intent: intent).serialized(),
                                 to: URL(fileURLWithPath: path), staging: service.paths.stagingDirURL)
            return nil
        } catch {
            return AppState.friendly(error)
        }
    }

    /// Every collection this machine publishes, written when what it says has changed. Only the
    /// bindings in this machine's cache: the sidecar travels with the master list, so another
    /// machine's publish folder is recorded there but is that machine's to write.
    func publishIfChanged() {
        guard !collectionsCache.published.isEmpty else { return }
        var cacheChanged = false
        for collection in collectionsCache.published.keys.sorted() {
            guard let binding = collectionsCache.published[collection],
                  let record = collectionsFile.collections[collection]?.publish else { continue }
            do {
                let document = exportDocument(for: collection, intent: record.intent)
                let hash = try AppState.publishHash(of: document)
                guard hash != binding.lastWrittenHash else { continue }
                let target = URL(fileURLWithPath: binding.folder)
                    .appendingPathComponent(record.slug + "." + CollectionDocument.fileExtension)
                try AtomicFile.write(document.serialized(), to: target, staging: service.paths.stagingDirURL)
                collectionsCache.published[collection]?.lastWrittenHash = hash
                cacheChanged = true
                if publishError?.collection == collection { publishError = nil }
            } catch {
                // The recorded hash is left as it was, so the next change tries this write again.
                publishError = (collection: collection, message: AppState.friendly(error))
            }
        }
        // The cache save the write earned, through the same gate as every other one.
        guard cacheChanged, collectionsLoaded else { return }
        do {
            try collectionsCache.save(to: service.paths.collectionsCacheURL, staging: service.paths.stagingDirURL)
        } catch {
            lastError = AppState.friendly(error)
        }
    }

    /// What a document says, with the export stamp left out. The moment it was written is not
    /// part of its content, and hashing it would rewrite the shared folder on every store change
    /// — including the toggles that must never publish.
    private static func publishHash(of document: CollectionDocument) throws -> String {
        var identity = document
        identity.exported = ""
        return ContentHash.sha256(try identity.serialized())
    }

    /// `url` is `folder` itself or somewhere under it.
    private static func isInside(_ url: URL, _ folder: URL) -> Bool {
        let base = folder.standardizedFileURL.path
        let path = url.standardizedFileURL.path
        return path == base || path.hasPrefix(base + "/")
    }

    // MARK: - Collection banner

    /// The one banner the collection slot shows. A failed publish outranks everything: it is the
    /// only one where something the user asked for did not happen. Then the active collection's
    /// news before any other collection's, since that is the list in front of them.
    public var collectionBanner: CollectionBanner? {
        if let failure = publishError {
            return .publishFailed(collection: failure.collection, message: failure.message)
        }
        if let diff = pendingUpdates[activeCollection] {
            return .updateAvailable(collection: activeCollection, summary: diff.summary())
        }
        if let name = pendingUpdates.keys.min(), let diff = pendingUpdates[name] {
            return .updateAvailable(collection: name, summary: diff.summary())
        }
        if let fileName = unlocatedFileName(of: activeCollection) {
            return .locate(collection: activeCollection, fileName: fileName)
        }
        for name in collectionsFile.collections.keys.sorted() {
            if let fileName = unlocatedFileName(of: name) { return .locate(collection: name, fileName: fileName) }
        }
        return nil
    }

    /// The document a synced collection is waiting to be pointed at, or nil when there is
    /// nothing to ask for: the collection is local, already bound, or the sidecar never recorded
    /// a file name to name in the request.
    private func unlocatedFileName(of collection: String) -> String? {
        guard let entry = collectionsFile.collections[collection], entry.kind == .synced,
              sourceBinding(of: collection)?.path == nil, let fileName = entry.fileName else { return nil }
        return fileName
    }

    // MARK: - Collections load

    /// The sidecar and the cache follow the store on every load: the master list decides which
    /// collections exist, the sidecar annotates them, and the cache binds what this machine has
    /// found. Runs after the store is assigned, so both reconcile against the list just loaded.
    private func loadCollections() {
        let loaded = service.loadCollections()
        // An unreadable sidecar loads as empty (see CollectionsFile.load). Reconciling the cache
        // against that would drop every binding on this machine over a file a sync tool is
        // halfway through writing, so a sidecar that exists yet loads as empty is left alone and
        // whatever is already in memory stands. A genuinely empty sidecar reads the same way and
        // costs only a prune deferred to the next load.
        collectionsNote = nil
        if loaded.collections.isEmpty, FileManager.default.fileExists(atPath: service.paths.collectionsFileURL.path) {
            collectionsLoaded = false
            // At launch there is no in-memory state to stand yet, so the cache is taken as it
            // stands — unreconciled, since the sidecar that would vouch for it is the file that
            // cannot be read. A good set of bindings then outlives a bad sidecar, and the first
            // load that can read the sidecar again prunes whatever it no longer vouches for.
            if !hasLoadedCollectionsOnce {
                collectionsCache = CollectionsLocalCache.load(from: service.paths.collectionsCacheURL)
            }
            return
        }
        collectionsFile = loaded.reconciled(with: store)
        collectionsCache = CollectionsLocalCache.load(from: service.paths.collectionsCacheURL)
            .reconciled(with: collectionsFile)
        collectionsLoaded = true
        hasLoadedCollectionsOnce = true
        // What is on disk NOW, not what this app last wrote: another machine's sidecar is the
        // file the next save has to differ from, or a change that happens to restore our old
        // bytes would be skipped and reverted by the reload after it.
        lastSavedSidecarHash = (try? Data(contentsOf: service.paths.collectionsFileURL)).map(ContentHash.sha256)
        bindSourcesBesideTheStore()
        armSourceWatchers()
        recomputePending()
    }

    /// The last load rule: a synced collection nothing has bound on this machine tries the path
    /// the sidecar recorded relative to the store dir. A collection that travels inside the
    /// store's own folder is found there without ever asking; anything else keeps the Locate
    /// banner. A binding found this way is persisted at once, so the next launch starts bound.
    private func bindSourcesBesideTheStore() {
        var cache = collectionsCache
        var bound = false
        for (name, entry) in collectionsFile.collections where entry.kind == .synced {
            guard cache.synced[name]?.path == nil, let relative = entry.relativeToStore else { continue }
            // appendingPathComponent, not URL(fileURLWithPath:relativeTo:): the latter resolves
            // against a base without a trailing slash by REPLACING its last component, and the
            // store dir only carries that slash when it already exists on disk.
            let url = service.paths.storeDirURL.appendingPathComponent(relative).standardizedFileURL
            guard let data = try? Data(contentsOf: url) else { continue }
            cache.synced[name] = CollectionsLocalCache.SyncedBinding(
                path: url.path, lastHash: ContentHash.sha256(data),
                excluded: cache.synced[name]?.excluded ?? [:])
            bound = true
        }
        guard bound else { return }
        collectionsCache = cache
        do {
            try cache.save(to: service.paths.collectionsCacheURL, staging: service.paths.stagingDirURL)
        } catch {
            collectionsNote = AppState.friendly(error)
        }
    }

    // MARK: - Notifications

    private func notify(_ body: String, category: String? = nil) {
        guard settings.notifyExternalChanges else { return }
        notifier.notify(title: Notifications.title, body: body, category: category)
    }

    /// The malformed-config case gets the guided message; everything else its own text.
    public static func friendly(_ error: Error) -> String {
        if case ClaudeConfigError.malformed(let detail) = error {
            return malformedConfigMessage(detail: detail)
        }
        return error.localizedDescription
    }

    /// Stops the watchers and the notification hook; pending delays become no-ops.
    public func dispose() {
        disposed = true
        notifier.onRestartAction = nil
        watcher?.stop()
        storeWatcher?.stop()
        watcher = nil
        storeWatcher = nil
        sourceWatchers.values.forEach { $0.watcher.stop() }
        sourceWatchers.removeAll()
    }
}
