import Foundation
import Combine
import ConnectorControlCore

/// The app's state (catalog §1). Main-actor only; everything that arrives
/// from another thread comes through `AppHost.marshal`. Init sequence
/// (catalog §1.2 minus the legacy migration, which LiveServices runs first):
/// resolve the service, one-time permissions sweep, route the notification's
/// Restart Claude action, reload, arm the watchers.
@MainActor
public final class AppState: ObservableObject {
    // MARK: - Strings (catalog §1.8, §1.10, §1.16–§1.18, §2.2)
    // nonisolated, like today's restartCategoryID: LiveClaudeProcess reads
    // defaultClaudeAppPath off the main actor. Every constant is one line so
    // Task 12's string check can find it on its `static let`/`static func` line.

    nonisolated public static let noConnectorsSubtitle = "No connectors configured"
    nonisolated public static let claudeConfigRegeneratedBody = "Claude's config was changed outside Connector Control — regenerated from your connector list. Restart Claude to pick it up."
    nonisolated public static let connectorListChangedRestartBody = "Connector list has changed, restart required."
    nonisolated public static let regenerationFailedBody = "The connector configuration changed, but Claude's config could not be updated — open Connector Control to retry."
    nonisolated public static let claudeConfigChangedBody = "Claude's config changed outside Connector Control."
    nonisolated public static let storeChangedBody = "The connector list changed outside Connector Control — review it before your next change is applied."
    nonisolated public static let quitMessage = "Quit Connector Control?"
    nonisolated public static let quitButton = "Quit"
    nonisolated public static let restartMessage = "Restart Claude Desktop now?"
    nonisolated public static let restartInformative = "Any in-progress Claude conversation will be interrupted."
    nonisolated public static let restartButton = "Restart"
    nonisolated public static let newProfileTitle = "New Profile"
    nonisolated public static let renameProfileTitle = "Rename Profile"
    nonisolated public static let deleteProfileInformative = "Its connector list is removed; backups keep prior states."
    nonisolated public static let deleteButton = "Delete"
    nonisolated public static let nameEmptyError = "Name must not be empty."
    nonisolated public static let defaultClaudeAppPath = "/Applications/Claude.app"
    /// Catalog §1.17: Claude's launch date is re-read 3 s after the restart completes.
    nonisolated public static let restartRecheckDelay: TimeInterval = 3

    nonisolated public static func deleteProfileMessage(_ profile: String) -> String { "Delete Profile “\(profile)”?" }

    nonisolated public static func duplicateNameError(_ name: String) -> String { "A connector named “\(name)” already exists." }

    nonisolated public static func malformedConfigMessage(detail: String) -> String { "Claude's config file is not valid JSON (\(detail)). Nothing was written. Use Backups ▸ Restore… to recover it." }

    nonisolated public static func enabledSubtitle(enabled: Int, total: Int) -> String { "\(enabled) of \(total) enabled" }

    // MARK: - Published state (catalog §1.1)

    @Published public private(set) var store: MasterStore = .empty
    /// Settable: the restore sheet reports its failure here (catalog §5).
    @Published public var lastError: String?
    @Published public private(set) var needsClaudeRestart = false
    /// True when the last apply threw; keeps a retry affordance visible even
    /// after reload() refreshes lastError.
    @Published public private(set) var applyRetryNeeded = false
    /// mcpServers as last read from / written to Claude's file, for dirty tracking.
    @Published public private(set) var appliedServers: [String: JSONValue] = [:]
    @Published public private(set) var service: ConfigService
    /// Which of the four launchers Claude Desktop can start (spec
    /// 2026-09-05-tool-probe §3.6): probed on demand and cached for the run.
    /// A tool absent here has not been probed yet.
    @Published public private(set) var toolStatuses: [Tool: ToolStatus] = [:]

    /// The prompts AppState itself raises (quit, restart, profiles); the editor owns its own.
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

    /// Test probe: both watchers are live. Spec §6.3 wants this true after every reload.
    var watchersArmed: Bool { (watcher?.isArmed ?? false) && (storeWatcher?.isArmed ?? false) }

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

    // MARK: - Derived (catalog §1.1, §2.2)

    public var isDirty: Bool { store.enabledServers != appliedServers }

    public var sortedNames: [String] { store.mcps.keys.sorted() }

    public var profileNames: [String] { store.profiles.keys.sorted() }

    public var activeProfile: String { store.activeProfile }

    /// Catalog §2.2 header subtitle.
    public var headerSubtitle: String {
        let total = store.mcps.count
        return total == 0
            ? AppState.noConnectorsSubtitle
            : AppState.enabledSubtitle(enabled: store.enabledServers.count, total: total)
    }

    // MARK: - Service construction (catalog §1.3)

    nonisolated public static func makeService(settings: AppSettings, paths: PathContext) -> ConfigService {
        var resolved = AppPaths.live(environment: paths.environment, appSupport: paths.appSupport)
        // Env override (dev sandboxing) beats the user setting.
        if paths.environment["CONNECTOR_CONTROL_STORE_DIR"] == nil, let custom = settings.masterStoreDir {
            // Backups always stay machine-local: a synced store directory must
            // not fill the user's repo/cloud folder with rotating backups.
            resolved = AppPaths(
                claudeConfigURL: resolved.claudeConfigURL,
                storeDirURL: URL(fileURLWithPath: custom),
                backupsDirURL: AppPaths.live(environment: [:], appSupport: paths.appSupport).backupsDirURL)
        }
        return ConfigService(paths: resolved, keepCount: settings.backupKeepCount)
    }

    // MARK: - Watchers (catalog §1.4, §1.5)

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
    /// Never a blanket re-arm: a popover open reloads (catalog §2.1), and tearing
    /// the sources down each time would re-baseline the last-seen mtime and open
    /// a gap where an external write is simply lost.
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

    // MARK: - Repointing (catalog §1.12) and restore (catalog §1.13)

    /// Repoints the master store to a new directory (or back to the default when
    /// `dir` is nil). Seeds the new location from the current store if it has no
    /// mcps.json yet, rebuilds the service, and re-arms both watchers.
    public func repointStore(to dir: URL?) {
        let previousStoreURL = service.paths.masterStoreURL
        settings.masterStoreDir = dir?.path
        let rebuilt = AppState.makeService(settings: settings, paths: paths)
        let newStoreURL = rebuilt.paths.masterStoreURL
        let fm = FileManager.default
        if !fm.fileExists(atPath: newStoreURL.path), fm.fileExists(atPath: previousStoreURL.path) {
            try? fm.createDirectory(at: rebuilt.paths.storeDirURL, withIntermediateDirectories: true)
            try? fm.copyItem(at: previousStoreURL, to: newStoreURL)
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

    // MARK: - Tools (spec 2026-09-05-tool-probe §3.6)

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

    // MARK: - Restart-required derivation (catalog §1.11)

    /// Claude needs a restart iff it is running on a config older than our last
    /// write. Derived from the process launch date, so it self-clears however
    /// Claude gets restarted — via us, by hand, or by an update.
    public func refreshRestartState() {
        guard let lastApply = settings.lastApplyDate, claude.isRunning, let launched = claude.launchDate else {
            needsClaudeRestart = false
            return
        }
        needsClaudeRestart = launched < lastApply
    }

    // MARK: - Reload (catalog §1.7 + §1.8)

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
            } else if regenerated && wasLoaded && trigger == .externalStoreAdoption && needsClaudeRestart {
                // A remote (synced) connector-list change landed while nobody
                // was looking and Claude is running on the older config — the
                // one restart-pending case with no in-app feedback in view.
                notify(AppState.connectorListChangedRestartBody, category: Notifications.restartCategory)
            } else if regenerationFailed && wasLoaded && trigger != .quietStoreAdoption {
                notify(AppState.regenerationFailedBody)
            } else if claudeConfigChangedExternally {
                notify(AppState.claudeConfigChangedBody)
            } else if storeChangedExternally {
                notify(AppState.storeChangedBody)
            }
            refreshRestartState()
        } catch {
            lastError = AppState.friendly(error)
            refreshRestartState()
        }
        AppState.reArm(watcher)
        AppState.reArm(storeWatcher)
    }

    // MARK: - Apply / persist (catalog §1.10)

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

    private func persistStore() {
        do { try service.saveStore(store) } catch { lastError = AppState.friendly(error) }
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

    /// Removes and persists; the caller applies (catalog §3.10 does both in one turn).
    public func remove(name: String) {
        store.mcps.removeValue(forKey: name)
        persistStore()
    }

    // MARK: - Quit (catalog §1.16)

    public func quitApp() {
        if settings.confirmBeforeQuit,
           !dialogs.confirm(message: AppState.quitMessage, informative: nil, primary: AppState.quitButton) {
            return
        }
        quitRequested?()
    }

    // MARK: - Restart Claude (catalog §1.17)

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

    // MARK: - Profiles (catalog §1.18)

    /// Switching profiles applies immediately, like every other change. An unknown name is silently ignored.
    public func switchProfile(to name: String) {
        guard store.switchProfile(to: name) == nil else { return }
        persistStore()
        performApply()
    }

    public func newProfile() {
        guard let name = dialogs.promptForName(title: AppState.newProfileTitle, initial: "") else { return }
        finishProfileChange(store.addProfile(named: name, copyingCurrent: true))
    }

    public func renameProfile() {
        guard let name = dialogs.promptForName(title: AppState.renameProfileTitle, initial: store.activeProfile) else { return }
        finishProfileChange(store.renameActiveProfile(to: name))
    }

    public func deleteProfile() {
        guard dialogs.confirm(message: AppState.deleteProfileMessage(store.activeProfile),
                              informative: AppState.deleteProfileInformative,
                              primary: AppState.deleteButton, destructive: true) else { return }
        finishProfileChange(store.deleteActiveProfile())
    }

    private func finishProfileChange(_ error: String?) {
        if let error {
            lastError = error
        } else {
            persistStore()
            performApply()
        }
    }

    // MARK: - Notifications (catalog §1.8)

    private func notify(_ body: String, category: String? = nil) {
        guard settings.notifyExternalChanges else { return }
        notifier.notify(title: Notifications.title, body: body, category: category)
    }

    /// Catalog §1.10 friendly(): the malformed-config case gets the guided message; everything else its own text.
    nonisolated public static func friendly(_ error: Error) -> String {
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
