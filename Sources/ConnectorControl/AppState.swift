import AppKit
import Foundation
@preconcurrency import UserNotifications
import Sparkle
import ConnectorControlCore

@MainActor
final class AppState: ObservableObject {
    @Published var store: MasterStore = .empty
    @Published var lastError: String?
    @Published var needsClaudeRestart = false
    /// True when the last apply threw; keeps a retry affordance visible even
    /// after reload() refreshes lastError.
    @Published var applyRetryNeeded = false
    /// mcpServers as last read from / written to Claude's file, for dirty tracking.
    @Published private(set) var appliedServers: [String: JSONValue] = [:]
    @Published private(set) var service: ConfigService
    private var watcher: FileWatcher?
    private var storeWatcher: FileWatcher?
    private var hasLoadedOnce = false
    /// The center holds its delegate weakly; AppState retains the bridge.
    private var notificationHandler: NotificationActionHandler?
    /// Sparkle auto-updater. Created not-started; started only from a real
    /// app bundle — bare `swift run` has none and Sparkle requires one.
    let updaterController = SPUStandardUpdaterController(
        startingUpdater: false, updaterDelegate: nil, userDriverDelegate: nil)
    private(set) var updaterRunning = false
    /// Which of the four launchers Claude Desktop can start (spec
    /// 2026-09-05-tool-probe §3.6): probed on demand — Settings ▸ Claude, the
    /// editor — and cached for the rest of the run. A tool absent here has
    /// not been probed yet.
    @Published private(set) var toolStatuses: [Tool: ToolStatus] = [:]
    private let toolProbe = ToolProbe.live()
    private var toolsInFlight: Set<Tool> = []

    nonisolated static let restartCategoryID = "restartPending"
    nonisolated static let restartActionID = "restartClaude"

    /// UNUserNotificationCenter.current() crashes under bare `swift run` (no
    /// app bundle), and Sparkle requires a bundle too.
    private static let hasAppBundle = Bundle.main.bundleIdentifier != nil

    /// Why a reload is running — controls reconciliation authority and which
    /// notifications may fire.
    enum ReloadTrigger {
        /// Launch, popover open, or the Claude-config watcher.
        case routine
        /// Store adoption with the user watching or on our own write's echo
        /// (Backups ▸ Restore, store repoint): store wins totally, no
        /// notifications.
        case quietStoreAdoption
        /// The store watcher saw an outside write to mcps.json (sync tool,
        /// another machine): adopt it and announce the consequences.
        case externalStoreAdoption
    }

    init(service: ConfigService? = nil) {
        // Migration must run BEFORE the service reads UserDefaults — a default
        // argument would be evaluated at the call site, ahead of this body.
        AppState.migrateFromLegacyNames()
        let resolved = service ?? AppState.makeService()
        self.service = resolved
        // Sweep the RESOLVED paths (a repointed store lives outside the default
        // dir) so files written before the 600-permissions fix get corrected.
        AppState.sweepPermissionsOnce(paths: resolved.paths)
        configureNotificationActions()
        if AppState.hasAppBundle {
            updaterController.startUpdater()
            updaterRunning = true
        }
        reload()
        armWatchers()
    }

    /// Registers the notification category whose Restart Claude button routes
    /// back into the app. Skipping the confirm-before-restart alert there is
    /// deliberate: clicking the explicit action IS the confirmation. Guarded
    /// like notify() — bare `swift run` has no notification center.
    private func configureNotificationActions() {
        guard AppState.hasAppBundle else { return }
        let center = UNUserNotificationCenter.current()
        let restart = UNNotificationAction(
            identifier: AppState.restartActionID, title: "Restart Claude")
        center.setNotificationCategories([
            UNNotificationCategory(identifier: AppState.restartCategoryID,
                                   actions: [restart], intentIdentifiers: [])])
        let handler = NotificationActionHandler { [weak self] in
            // Stale-click guard: an old notification must not restart a Claude
            // that already picked up the config.
            guard let self, self.needsClaudeRestart else { return }
            self.performRestartClaude()
        }
        center.delegate = handler
        notificationHandler = handler
    }

    /// One-time repair of files written before owner-only permissions were
    /// enforced; gated by a done-flag so launches stay cheap.
    static func sweepPermissionsOnce(paths: AppPaths) {
        let defaults = UserDefaults.standard
        guard !defaults.bool(forKey: "permissionsSweepDone") else { return }
        let fm = FileManager.default
        for root in [paths.storeDirURL, paths.backupsDirURL] {
            try? fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: root.path)
            guard let files = fm.enumerator(
                at: root, includingPropertiesForKeys: [.isDirectoryKey]) else { continue }
            for case let file as URL in files {
                let isDir = (try? file.resourceValues(forKeys: [.isDirectoryKey]))?
                    .isDirectory ?? false
                try? fm.setAttributes(
                    [.posixPermissions: isDir ? 0o700 : 0o600],
                    ofItemAtPath: file.path)
            }
        }
        defaults.set(true, forKey: "permissionsSweepDone")
    }

    /// One-time migration from the app's previous names (newest first).
    static func migrateFromLegacyNames() {
        let fm = FileManager.default
        let appSupport = fm.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support")
        let new = appSupport.appendingPathComponent("Connector Control")
        for oldName in ["Custom Connector Control", "MCP Enabler"] {
            let old = appSupport.appendingPathComponent(oldName)
            if fm.fileExists(atPath: old.path), !fm.fileExists(atPath: new.path) {
                try? fm.moveItem(at: old, to: new)
            }
        }
        // Settings lived under the old bundle ids' defaults domains.
        for oldDomain in ["com.dlaporte.custom-connector-control",
                          "com.dlaporte.mcp-enabler"] {
            guard let oldDefaults = UserDefaults(suiteName: oldDomain) else { continue }
            for key in ["masterStoreDir", "claudeAppPath",
                        "backupKeepCount", "notifyExternalChanges",
                        "confirmBeforeRestart", "lastApplyDate"] {
                if let value = oldDefaults.object(forKey: key),
                   UserDefaults.standard.object(forKey: key) == nil {
                    UserDefaults.standard.set(value, forKey: key)
                }
            }
        }
    }

    nonisolated static func makeService() -> ConfigService {
        let env = ProcessInfo.processInfo.environment
        var paths = AppPaths.live()
        // Env override (dev sandboxing) beats the user setting.
        if env["CONNECTOR_CONTROL_STORE_DIR"] == nil,
           let custom = UserDefaults.standard.string(forKey: "masterStoreDir") {
            // Backups always stay machine-local: a synced store directory must
            // not fill the user's repo/cloud folder with rotating backups.
            paths = AppPaths(
                claudeConfigURL: paths.claudeConfigURL,
                storeDirURL: URL(fileURLWithPath: custom),
                backupsDirURL: AppPaths.live(environment: [:]).backupsDirURL)
        }
        let keep = UserDefaults.standard.object(forKey: "backupKeepCount") as? Int ?? 20
        return ConfigService(paths: paths, keepCount: keep)
    }

    private func armWatchers() {
        watcher = FileWatcher(url: service.paths.claudeConfigURL) { [weak self] in
            self?.reload()
        }
        watcher?.start()
        storeWatcher = FileWatcher(url: service.paths.masterStoreURL) { [weak self] in
            self?.adoptExternalStoreChange()
        }
        storeWatcher?.start()
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

    /// Repoints the master store to a new directory (or back to the default when
    /// `dir` is nil). Seeds the new location from the current store if it has no
    /// mcps.json yet, rebuilds the service, and re-arms both watchers.
    func repointStore(to dir: URL?) {
        let defaults = UserDefaults.standard
        let previousStoreURL = service.paths.masterStoreURL
        if let dir {
            defaults.set(dir.path, forKey: "masterStoreDir")
        } else {
            defaults.removeObject(forKey: "masterStoreDir")
        }
        let rebuilt = AppState.makeService()
        let newStoreURL = rebuilt.paths.masterStoreURL
        if !FileManager.default.fileExists(atPath: newStoreURL.path),
           FileManager.default.fileExists(atPath: previousStoreURL.path) {
            try? FileManager.default.createDirectory(
                at: rebuilt.paths.storeDirURL, withIntermediateDirectories: true)
            try? FileManager.default.copyItem(at: previousStoreURL, to: newStoreURL)
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
    func refreshServiceSettings() {
        service = AppState.makeService()
        armWatchers()
    }

    // MARK: - Tools

    /// Probes `tools` off the main thread and publishes the results; a tool
    /// already in flight is not probed twice. Callers: the Claude tab on
    /// appear (all four), the editor when its required tool is unknown or
    /// changes to a different one.
    func refreshTools(_ tools: [Tool] = Tool.allCases) {
        let wanted = tools.filter { toolsInFlight.insert($0).inserted }
        guard !wanted.isEmpty else { return }
        let probe = toolProbe
        Task.detached(priority: .utility) {
            let results = probe.probe(wanted)
            await self.publishToolStatuses(results, probed: wanted)
        }
    }

    private func publishToolStatuses(_ results: [Tool: ToolStatus], probed: [Tool]) {
        toolStatuses.merge(results) { _, new in new }
        toolsInFlight.subtract(probed)
    }

    var isDirty: Bool {
        store.enabledServers != appliedServers
    }

    var sortedNames: [String] { store.mcps.keys.sorted() }

    var profileNames: [String] { store.profiles.keys.sorted() }
    var activeProfile: String { store.activeProfile }

    /// Claude needs a restart iff it's running on a config older than our last
    /// write. Derived from the process launch date, so it self-clears however
    /// Claude gets restarted — via us, by hand, or by an update.
    func refreshRestartState() {
        guard let lastApply = UserDefaults.standard.object(forKey: "lastApplyDate") as? Date,
              let claude = NSRunningApplication.runningApplications(
                withBundleIdentifier: ClaudeRestarter.bundleID).first,
              let launched = claude.launchDate
        else {
            needsClaudeRestart = false
            return
        }
        needsClaudeRestart = launched < lastApply
    }

    func reload(trigger: ReloadTrigger = .routine) {
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
            if wasLoaded,
               !FileManager.default.fileExists(atPath: service.paths.masterStoreURL.path) {
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
            lastError = result.notes.first
            if !isDirty { applyRetryNeeded = false }

            // The store is the source of truth; Claude's config is downstream.
            // Any divergence from the render — an external edit, a re-added
            // disabled entry, a removal, a wiped file — is regenerated away,
            // arming the same Restart Required footer as a user-made change.
            // Post-ingestion the render includes imported unknowns, and our own
            // applies leave file == render, so divergence here is external by
            // definition. No loop: the regenerating write satisfies the
            // watcher-triggered follow-up reload.
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

            // Fire notifications AFTER all state above has been assigned, and
            // never on first load or for quiet adoptions (restore, repoint —
            // the user is watching the app). At most one per reload, and the
            // success branches only claim what actually happened on disk.
            // Branch 1 claims an external FILE edit, so it also requires the
            // file to have actually moved since the last read — a routine
            // reload can regenerate for a store-side divergence too (a
            // pending apply that failed earlier and succeeds on this retry),
            // and announcing the user's own change as external would be a lie.
            if regenerated && wasLoaded && trigger == .routine
                && claudeConfigChangedExternally {
                notify("Connector Control",
                       "Claude's config was changed outside Connector Control — "
                       + "regenerated from your connector list. "
                       + "Restart Claude to pick it up.")
            } else if regenerated && wasLoaded && trigger == .externalStoreAdoption
                        && needsClaudeRestart {
                // A remote (synced) connector-list change landed while nobody
                // was looking and Claude is running on the older config — the
                // one restart-pending case with no in-app feedback in view.
                notify("Connector Control",
                       "Connector list has changed, restart required.",
                       category: AppState.restartCategoryID)
            } else if regenerationFailed && wasLoaded && trigger != .quietStoreAdoption {
                notify("Connector Control",
                       "The connector configuration changed, but Claude's config "
                       + "could not be updated — open Connector Control to retry.")
            } else if claudeConfigChangedExternally {
                notify("Connector Control", "Claude's config changed outside Connector Control.")
            } else if storeChangedExternally {
                notify("Connector Control",
                       "The connector list changed outside Connector Control — "
                       + "review it before your next change is applied.")
            }
            refreshRestartState()
        } catch {
            lastError = friendly(error)
            refreshRestartState()
        }
    }

    /// Restores Claude's config from a backup and syncs the reconciliation
    /// baseline to the restored contents BEFORE reloading, so the app's own
    /// restore isn't misread as an external change or a re-add.
    func restoreClaudeConfig(from backup: URL) throws {
        let servers = try service.restoreClaudeConfig(from: backup, mergedWith: store)
        appliedServers = servers
        hasLoadedOnce = true
        UserDefaults.standard.set(Date(), forKey: "lastApplyDate")
        // ConfigService already merged and persisted the store; a quiet
        // adoption takes it as-is and suppresses notifications for the
        // user's own restore action.
        reload(trigger: .quietStoreAdoption)
    }

    private func notify(_ title: String, _ body: String, category: String? = nil) {
        guard AppState.hasAppBundle else { return }
        guard UserDefaults.standard.object(forKey: "notifyExternalChanges") as? Bool ?? true
        else { return }
        let center = UNUserNotificationCenter.current()
        center.requestAuthorization(options: [.alert]) { granted, _ in
            guard granted else { return }
            let content = UNMutableNotificationContent()
            content.title = title
            content.body = body
            if let category { content.categoryIdentifier = category }
            center.add(UNNotificationRequest(
                identifier: UUID().uuidString, content: content, trigger: nil))
        }
    }

    func setEnabled(_ name: String, _ on: Bool) {
        store.mcps[name]?.enabled = on
        persistStore()
        // Toggles take effect immediately; the Restart Required button is the
        // only follow-up step.
        performApply()
    }

    func apply() {
        performApply()
    }

    /// Editor-window flow: saving there is a deliberate final act, so apply
    /// immediately. The derived Restart Required footer button handles the
    /// restart nudge afterward.
    func applyInteractively() {
        guard isDirty else { return }
        performApply()
    }

    private func performApply() {
        do {
            try service.apply(store)
            appliedServers = store.enabledServers
            UserDefaults.standard.set(Date(), forKey: "lastApplyDate")
            refreshRestartState()
            lastError = nil
            applyRetryNeeded = false
        } catch {
            lastError = friendly(error)
            applyRetryNeeded = true
        }
    }

    /// Validates and saves an entry. Returns an error message, or nil on success.
    func upsert(name: String, entry: MCPEntry, renamedFrom oldName: String?) -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return "Name must not be empty." }
        if trimmed != oldName, store.mcps[trimmed] != nil {
            return "A connector named “\(trimmed)” already exists."
        }
        if let old = oldName, old != trimmed { store.mcps.removeValue(forKey: old) }
        store.mcps[trimmed] = entry
        persistStore()
        return nil
    }

    func remove(name: String) {
        store.mcps.removeValue(forKey: name)
        persistStore()
    }

    func quitApp() {
        if UserDefaults.standard.object(forKey: "confirmBeforeQuit") as? Bool ?? true {
            NSApp.activate(ignoringOtherApps: true)
            let alert = NSAlert()
            alert.messageText = "Quit Connector Control?"
            alert.addButton(withTitle: "Quit")
            alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        NSApp.terminate(nil)
    }

    func restartClaude() {
        if UserDefaults.standard.object(forKey: "confirmBeforeRestart") as? Bool ?? true {
            NSApp.activate(ignoringOtherApps: true)
            let alert = NSAlert()
            alert.messageText = "Restart Claude Desktop now?"
            alert.informativeText = "Any in-progress Claude conversation will be interrupted."
            alert.addButton(withTitle: "Restart")
            alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        performRestartClaude()
    }

    /// Restart with no confirmation alert — the in-app button after its
    /// confirm, and the notification's Restart Claude action, where the
    /// deliberate action click is the confirmation.
    private func performRestartClaude() {
        let appURL = URL(fileURLWithPath: UserDefaults.standard.string(forKey: "claudeAppPath")
            ?? "/Applications/Claude.app")
        ClaudeRestarter.restart(appURL: appURL) { [weak self] errorMessage in
            self?.lastError = errorMessage
            self?.refreshRestartState()
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in
                self?.refreshRestartState()
            }
        }
    }

    // MARK: - Profiles

    /// Switching profiles applies immediately, like every other change.
    func switchProfile(to name: String) {
        guard store.switchProfile(to: name) == nil else { return }
        persistStore()
        performApply()
    }

    func newProfile() {
        guard let name = promptForName(title: "New Profile", initial: "") else { return }
        guard let error = store.addProfile(named: name, copyingCurrent: true) else {
            persistStore()
            performApply()
            return
        }
        lastError = error
    }

    func renameProfile() {
        guard let name = promptForName(
            title: "Rename Profile", initial: store.activeProfile) else { return }
        guard let error = store.renameActiveProfile(to: name) else {
            persistStore()
            performApply()
            return
        }
        lastError = error
    }

    func deleteProfile() {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Delete Profile \u{201C}\(store.activeProfile)\u{201D}?"
        alert.informativeText =
            "Its connector list is removed; backups keep prior states."
        alert.addButton(withTitle: "Delete")
        alert.addButton(withTitle: "Cancel")
        alert.buttons.first?.hasDestructiveAction = true
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        guard let error = store.deleteActiveProfile() else {
            persistStore()
            performApply()
            return
        }
        lastError = error
    }

    private func promptForName(title: String, initial: String) -> String? {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = title
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 220, height: 24))
        field.stringValue = initial
        alert.accessoryView = field
        alert.window.initialFirstResponder = field
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return nil }
        return field.stringValue
    }

    private func persistStore() {
        do { try service.saveStore(store) } catch { lastError = friendly(error) }
    }

    private func friendly(_ error: Error) -> String {
        if case ClaudeConfigError.malformed(let detail) = error {
            return "Claude's config file is not valid JSON (\(detail)). "
                + "Nothing was written. Use Backups ▸ Restore… to recover it."
        }
        return error.localizedDescription
    }
}

/// NSObject bridge for UNUserNotificationCenter's delegate: routes the
/// Restart Claude notification action back into AppState on the main actor.
private final class NotificationActionHandler: NSObject, UNUserNotificationCenterDelegate {
    private let onRestartAction: @MainActor () -> Void

    init(onRestartAction: @escaping @MainActor () -> Void) {
        self.onRestartAction = onRestartAction
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        if response.actionIdentifier == AppState.restartActionID {
            let action = onRestartAction
            Task { @MainActor in action() }
        }
        completionHandler()
    }

    /// Without this, notifications are silently discarded while Connector
    /// Control is the active app (Settings or the editor focused) — exactly
    /// when a restart-pending banner is still worth showing.
    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler:
            @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler([.banner, .list])
    }
}
