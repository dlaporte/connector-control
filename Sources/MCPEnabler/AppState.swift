import AppKit
import Foundation
@preconcurrency import UserNotifications
import MCPEnablerCore

@MainActor
final class AppState: ObservableObject {
    @Published var store: MasterStore = .empty
    @Published var missingEnabled: [String] = []
    @Published var lastError: String?
    @Published var needsClaudeRestart = false
    /// mcpServers as last read from / written to Claude's file, for dirty tracking.
    @Published private(set) var appliedServers: [String: JSONValue] = [:]
    @Published private(set) var service: ConfigService
    private var watcher: FileWatcher?
    private var storeWatcher: FileWatcher?
    private var hasLoadedOnce = false

    init(service: ConfigService = AppState.makeService()) {
        AppState.migrateFromMCPEnabler()
        self.service = service
        reload()
        armWatchers()
    }

    /// One-time migration from the app's previous names (newest first).
    static func migrateFromMCPEnabler() {
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
        if env["MCP_ENABLER_STORE_DIR"] == nil,
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
        hasLoadedOnce = false
        armWatchers()
        reload()
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

    func reload() {
        do {
            // Capture "before" state for the notification rules below, computed
            // BEFORE any state is overwritten.
            let wasLoaded = hasLoadedOnce
            let previousMissingWasEmpty = missingEnabled.isEmpty
            let previousApplied = appliedServers

            let result = try service.loadAndReconcile(
                baseline: hasLoadedOnce ? appliedServers : nil)
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
            lastError = result.notes.first

            // Fire notifications AFTER all state above has been assigned, and
            // never on first load. At most one notification per reload.
            if firedMissingNotification {
                let names = result.missingEnabled.joined(separator: ", ")
                notify("Connector Control",
                       "Claude's config is missing \(result.missingEnabled.count) "
                       + "MCP(s): \(names) — open the menu bar item to restore.")
            } else if claudeConfigChangedExternally {
                notify("Connector Control", "Claude's config changed outside Connector Control.")
            }
            refreshRestartState()
        } catch {
            lastError = friendly(error)
            refreshRestartState()
        }
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
        } catch {
            lastError = friendly(error)
        }
    }

    /// Validates and saves an entry. Returns an error message, or nil on success.
    func upsert(name: String, entry: MCPEntry, renamedFrom oldName: String?) -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return "Name must not be empty." }
        if trimmed != oldName, store.mcps[trimmed] != nil {
            return "An MCP named “\(trimmed)” already exists."
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

    /// Recovery for externally wiped MCPs: rewrite Claude's config from the store.
    func restoreMissing() { apply() }

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
