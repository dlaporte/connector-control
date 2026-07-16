import Foundation
@preconcurrency import UserNotifications
import MCPEnablerCore

@MainActor
final class AppState: ObservableObject {
    @Published var store: MasterStore = .empty
    @Published var missingEnabled: [String] = []
    @Published var lastError: String?
    @Published var showRestartPrompt = false
    /// mcpServers as last read from / written to Claude's file, for dirty tracking.
    @Published private(set) var appliedServers: [String: JSONValue] = [:]

    @Published private(set) var service: ConfigService
    private var watcher: FileWatcher?
    private var storeWatcher: FileWatcher?
    private var hasLoadedOnce = false

    init(service: ConfigService = AppState.makeService()) {
        self.service = service
        reload()
        armWatchers()
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
                notify("MCP Enabler",
                       "Claude's config is missing \(result.missingEnabled.count) "
                       + "MCP(s): \(names) — open the menu bar item to restore.")
            } else if claudeConfigChangedExternally {
                notify("MCP Enabler", "Claude's config changed outside MCP Enabler.")
            }
        } catch {
            lastError = friendly(error)
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
        showRestartPrompt = false
        store.mcps[name]?.enabled = on
        persistStore()
    }

    func apply() {
        do {
            try service.apply(store)
            appliedServers = store.mcps.filter(\.value.enabled).mapValues(\.config)
            missingEnabled = []
            switch UserDefaults.standard.string(forKey: "restartBehavior") ?? "ask" {
            case "auto": restartClaude()
            case "never": break
            default: showRestartPrompt = true
            }
            lastError = nil
        } catch {
            lastError = friendly(error)
        }
    }

    /// Validates and saves an entry. Returns an error message, or nil on success.
    func upsert(name: String, entry: MCPEntry, renamedFrom oldName: String?) -> String? {
        showRestartPrompt = false
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
        showRestartPrompt = false
        store.mcps.removeValue(forKey: name)
        persistStore()
    }

    /// Recovery for externally wiped MCPs: rewrite Claude's config from the store.
    func restoreMissing() { apply() }

    func restartClaude() {
        showRestartPrompt = false
        let appURL = URL(fileURLWithPath: UserDefaults.standard.string(forKey: "claudeAppPath")
            ?? "/Applications/Claude.app")
        ClaudeRestarter.restart(appURL: appURL) { [weak self] errorMessage in
            self?.lastError = errorMessage
            if errorMessage != nil { self?.showRestartPrompt = true }
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
