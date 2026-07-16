import Foundation
import MCPEnablerCore

@MainActor
final class AppState: ObservableObject {
    @Published var store: MasterStore = .empty
    @Published var missingEnabled: [String] = []
    @Published var lastError: String?
    @Published var showRestartPrompt = false
    /// mcpServers as last read from / written to Claude's file, for dirty tracking.
    @Published private(set) var appliedServers: [String: JSONValue] = [:]

    let service: ConfigService
    private var watcher: FileWatcher?
    private var hasLoadedOnce = false

    init(service: ConfigService = ConfigService(paths: .live())) {
        self.service = service
        reload()
        watcher = FileWatcher(url: service.paths.claudeConfigURL) { [weak self] in
            self?.reload()
        }
        watcher?.start()
    }

    var isDirty: Bool {
        store.mcps.filter(\.value.enabled).mapValues(\.config) != appliedServers
    }

    var sortedNames: [String] { store.mcps.keys.sorted() }

    func reload() {
        do {
            let result = try service.loadAndReconcile(
                baseline: hasLoadedOnce ? appliedServers : nil)
            store = result.store
            missingEnabled = result.missingEnabled
            appliedServers = result.claudeServers
            lastError = result.notes.first
            hasLoadedOnce = true
        } catch {
            lastError = friendly(error)
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
            showRestartPrompt = true
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
        ClaudeRestarter.restart { [weak self] errorMessage in
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
