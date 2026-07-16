import Foundation

/// Orchestrates every stateful operation, guaranteeing the backup-before-write
/// invariant. The UI layer calls only this type for file operations.
public struct ConfigService {
    public let paths: AppPaths
    public let backups: BackupManager

    public init(paths: AppPaths) {
        self.paths = paths
        self.backups = BackupManager(backupsDir: paths.backupsDirURL)
    }

    /// Load master store (handling corruption), read Claude's servers,
    /// reconcile, persist the store if reconciliation changed it.
    ///
    /// The master store is loaded FIRST so it is always available: if Claude's
    /// config turns out to be malformed, reconciliation is skipped entirely
    /// (nothing is written) and the store just loaded is returned as-is, so the
    /// UI keeps showing the user's MCP list instead of going blank.
    public func loadAndReconcile(baseline: [String: JSONValue]? = nil) throws
        -> (store: MasterStore, missingEnabled: [String], notes: [String],
            claudeServers: [String: JSONValue]?) {
        var notes: [String] = []
        let loaded = MasterStoreIO.load(from: paths.masterStoreURL)
        if let corrupt = loaded.corruptFileURL {
            notes.append(
                "The MCP list file was unreadable; it was preserved as "
                + "\(corrupt.lastPathComponent) and rebuilt from Claude's config.")
        }
        let servers: [String: JSONValue]
        do {
            servers = try ClaudeConfigIO.readMCPServers(at: paths.claudeConfigURL)
        } catch is ClaudeConfigError {
            return (loaded.store, [],
                    notes + ["Claude's config file is not valid JSON. Your MCP list is safe; "
                     + "use Backups ▸ Restore… to repair the file."],
                    nil)
        }
        let outcome = Reconciler.reconcile(
            store: loaded.store, claudeServers: servers, baseline: baseline)
        if outcome.storeChanged || loaded.corruptFileURL != nil {
            try saveStore(outcome.store)
        }
        return (outcome.store, outcome.missingEnabled, notes, servers)
    }

    /// Backup mcps.json (if present), then atomically save the store.
    public func saveStore(_ store: MasterStore) throws {
        try backups.backUp(fileAt: paths.masterStoreURL, series: "mcps")
        try MasterStoreIO.save(store, to: paths.masterStoreURL)
    }

    /// Snapshot original (first run), backup Claude's config, then write the
    /// enabled subset into it, preserving all other keys.
    public func apply(_ store: MasterStore) throws {
        try backups.ensureOriginalSnapshot(of: paths.claudeConfigURL)
        try backups.backUp(fileAt: paths.claudeConfigURL, series: "claude_desktop_config")
        let enabled = store.mcps.filter(\.value.enabled).mapValues(\.config)
        try ClaudeConfigIO.write(mcpServers: enabled, to: paths.claudeConfigURL)
    }

    /// Backup the current file, copy the chosen backup over it, then persist a
    /// freshly reconciled store so the UI reflects the restored contents.
    /// The backup's content is validated BEFORE the live file is touched.
    public func restoreClaudeConfig(from backup: URL, mergedWith store: MasterStore) throws {
        let data = try Data(contentsOf: backup)
        guard let parsed = try? JSONSerialization.jsonObject(with: data),
              parsed is [String: Any] else {
            throw ClaudeConfigError.malformed(
                "backup \(backup.lastPathComponent) is not a valid config file")
        }
        try backups.backUp(fileAt: paths.claudeConfigURL, series: "claude_desktop_config")
        try AtomicFile.write(data, to: paths.claudeConfigURL)
        let servers = try ClaudeConfigIO.readMCPServers(at: paths.claudeConfigURL)
        let outcome = Reconciler.reconcile(store: store, claudeServers: servers)
        if outcome.storeChanged { try saveStore(outcome.store) }
    }
}
