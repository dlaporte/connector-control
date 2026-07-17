import AppKit
import Foundation
@preconcurrency import UserNotifications
import ConnectorControlCore

@MainActor
final class AppState: ObservableObject {
    @Published var store: MasterStore = .empty
    @Published var missingEnabled: [String] = []
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

    init(service: ConfigService? = nil) {
        // Migration must run BEFORE the service reads UserDefaults — a default
        // argument would be evaluated at the call site, ahead of this body.
        AppState.migrateFromLegacyNames()
        let resolved = service ?? AppState.makeService()
        self.service = resolved
        // Sweep the RESOLVED paths (a repointed store lives outside the default
        // dir) so files written before the 600-permissions fix get corrected.
        AppState.sweepPermissionsOnce(paths: resolved.paths)
        reload()
        armWatchers()
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
            self?.reload()
        }
        storeWatcher?.start()
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
        reload(storeAuthoritative: true)
    }

    /// Rebuilds the service from current settings (e.g. after backup retention
    /// changes) without moving the store directory or resetting the
    /// reconciliation baseline.
    func refreshServiceSettings() {
        service = AppState.makeService()
        armWatchers()
    }

    var isDirty: Bool {
        store.mcps.filter(\.value.enabled).mapValues(\.config) != appliedServers
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

    func reload(storeAuthoritative: Bool = false) {
        do {
            // Capture "before" state for the notification rules below, computed
            // BEFORE any state is overwritten.
            let wasLoaded = hasLoadedOnce
            let previousMissingWasEmpty = missingEnabled.isEmpty
            let previousApplied = appliedServers
            let previousStoreMcps = store.mcps

            let result = try service.loadAndReconcile(
                baseline: hasLoadedOnce ? appliedServers : nil,
                storeAuthoritative: storeAuthoritative)
            store = result.store
            missingEnabled = result.missingEnabled
            var firedMissingNotification = false
            var claudeConfigChangedExternally = false
            if let servers = result.claudeServers {
                firedMissingNotification =
                    wasLoaded && previousMissingWasEmpty && !result.missingEnabled.isEmpty
                claudeConfigChangedExternally = wasLoaded && servers != previousApplied
                appliedServers = servers
                hasLoadedOnce = true
            }
            // Store-side external change (e.g. a synced mcps.json edited by a
            // sync tool): our own persistStore writes leave the in-memory store
            // already equal, so a mismatch here means an outside writer.
            // Authoritative reloads are user-initiated adoptions (repoint,
            // restore) — never notify for those.
            let storeChangedExternally =
                !storeAuthoritative
                && wasLoaded && result.store.mcps != previousStoreMcps
                && !claudeConfigChangedExternally
            lastError = result.notes.first
            if !isDirty { applyRetryNeeded = false }

            // Fire notifications AFTER all state above has been assigned, and
            // never on first load. At most one notification per reload.
            if firedMissingNotification {
                let names = result.missingEnabled.joined(separator: ", ")
                notify("Connector Control",
                       "Claude's config is missing \(result.missingEnabled.count) "
                       + "connector(s): \(names) — open the menu bar item to restore.")
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
        // ConfigService already merged and persisted the store; an authoritative
        // reload adopts it as-is and suppresses the external-change notification
        // for the user's own restore action.
        reload(storeAuthoritative: true)
    }

    private func notify(_ title: String, _ body: String) {
        // UNUserNotificationCenter.current() crashes under bare `swift run` (no
        // app bundle), so bail out first when there is none.
        guard Bundle.main.bundleIdentifier != nil else { return }
        guard UserDefaults.standard.object(forKey: "notifyExternalChanges") as? Bool ?? true
        else { return }
        let center = UNUserNotificationCenter.current()
        center.requestAuthorization(options: [.alert]) { granted, _ in
            guard granted else { return }
            let content = UNMutableNotificationContent()
            content.title = title
            content.body = body
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
            appliedServers = store.mcps.filter(\.value.enabled).mapValues(\.config)
            missingEnabled = []
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

    /// Recovery for externally removed MCPs: rewrite Claude's config from the store.
    func restoreMissing() { apply() }

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

    func markMissingDisabled() {
        for name in missingEnabled { store.mcps[name]?.enabled = false }
        missingEnabled = []
        persistStore()
        performApply()
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
