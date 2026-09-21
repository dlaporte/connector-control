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
    private var hasLoadedOnce = false
    private var disposed = false

    /// Test probe: both watchers are live, which should hold true after every reload.
    var watchersArmed: Bool { (watcher?.isArmed ?? false) && (storeWatcher?.isArmed ?? false) }

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

    var isDirty: Bool { store.enabledServers != appliedServers }

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
            // (Backups ▸ Restore… is the way out).
            lastError = result.notes.isEmpty ? nil : result.notes.joined(separator: " ")
            if !isDirty { applyRetryNeeded = false }

            // The store is the source of truth; Claude's config is downstream.
            // Any divergence from the render is regenerated away, arming the same
            // Restart Required footer as a user-made change. No loop: the
            // regenerating write satisfies the watcher-triggered follow-up reload.
            var regenerated = false
            var regenerationFailed = false
            if let servers = result.claudeServers, servers != store.enabledServers {
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
                let delta = ServerDelta(from: previousApplied, to: store.enabledServers)
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
        refreshRestartState()
        AppState.reArm(watcher)
        AppState.reArm(storeWatcher)
    }

    // MARK: - Apply / persist

    private func performApply() {
        do {
            try service.apply(store)
            appliedServers = store.enabledServers
            settings.lastApplyDate = host.now()
            refreshRestartState()
            lastError = nil
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
        do {
            try service.saveStore(store)
            try service.saveCollections(collectionsFile)
            try collectionsCache.save(to: service.paths.collectionsCacheURL, staging: service.paths.stagingDirURL)
        } catch {
            lastError = AppState.friendly(error)
        }
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
    /// The "authored on <platform>" caution needs the source document's launcher platform,
    /// which only arrives once a bound source is rendered; it joins this list there.
    public func connectorCaution(_ connector: String, in collection: String) -> String? {
        guard let config = store.collections[collection]?.mcps[connector]?.config else { return nil }
        let unfilled = Placeholder.markers(in: config).flatMap(\.names)
        if !unfilled.isEmpty {
            // Ordered by first appearance and de-duplicated across leaves, so the sentence is
            // stable between two reads of the same config.
            var seen: Set<String> = []
            return AppState.needsValueCaution(unfilled.filter { seen.insert($0).inserted }.joined(separator: ", "))
        }
        if Placeholder.usesDirectoryToken(config), isSynced(collection), sourceBinding(of: collection)?.path == nil {
            return AppState.locateCaution
        }
        return nil
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
        if loaded.collections.isEmpty, FileManager.default.fileExists(atPath: service.paths.collectionsFileURL.path) {
            return
        }
        collectionsFile = loaded.reconciled(with: store)
        collectionsCache = CollectionsLocalCache.load(from: service.paths.collectionsCacheURL)
            .reconciled(with: collectionsFile)
        bindSourcesBesideTheStore()
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
            let url = URL(fileURLWithPath: relative, relativeTo: service.paths.storeDirURL).standardizedFileURL
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
            lastError = AppState.friendly(error)
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
    }
}
