import Foundation

/// Orchestrates every stateful operation, guaranteeing the backup-before-write
/// invariant. The UI layer calls only this type for file operations.
public struct ConfigService: Sendable {
    public let paths: AppPaths
    public let backups: BackupManager

    public init(paths: AppPaths, keepCount: Int = BackupManager.defaultKeepCount) {
        self.paths = paths
        self.backups = BackupManager(backupsDir: paths.backupsDirURL, keepCount: keepCount,
                                     stagingDir: paths.stagingDirURL)
    }

    /// Load master store (handling corruption), read Claude's servers,
    /// reconcile, persist the store if reconciliation changed it.
    ///
    /// The master store is loaded FIRST so it is always available: if Claude's
    /// config turns out to be malformed, reconciliation is skipped entirely
    /// (nothing is written) and the store just loaded is returned as-is, so the
    /// UI keeps showing the user's MCP list instead of going blank.
    /// With `storeAuthoritative`, the file's own current servers act as the
    /// baseline, so every reconciliation rule resolves store-wins — used when
    /// adopting a pre-existing (e.g. synced) store that must not be overwritten
    /// by this machine's state.
    ///
    /// `collectionDirectory` names, for a collection, the folder `${COLLECTION_DIR}` stands for
    /// on this machine; it is asked about the active collection once the store is loaded, and
    /// what it answers is written back as the token in anything ingested from Claude's file.
    public func loadAndReconcile(baseline: [String: JSONValue]? = nil,
                                 storeAuthoritative: Bool = false,
                                 collectionDirectory: (String) -> String? = { _ in nil }) throws
        -> (store: MasterStore, notes: [String],
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
            return (loaded.store,
                    notes + ["Claude's config file is not valid JSON. Your MCP list is safe; "
                     + "use Backups ▸ Restore… to repair the file."],
                    nil)
        }
        // A corrupt store is rebuilt with fresh-launch (nil-baseline) import
        // semantics: reconciling the empty replacement against a baseline would
        // classify every server as a pending removal, rebuild an empty list,
        // and set up the next apply to wipe Claude's config.
        let effectiveBaseline: [String: JSONValue]?
        if loaded.corruptFileURL != nil {
            effectiveBaseline = nil
        } else if storeAuthoritative {
            // The caller's baseline (last-applied servers) still classifies
            // additions correctly during an adoption: an entry matching it is
            // this machine's own applied state (never imported into the
            // adopted store), one differing from it is a genuine external
            // addition racing the adoption — ingest it rather than letting
            // the regeneration erase it. Without a baseline (adoption of a
            // repointed store before any apply), the file itself is the
            // baseline: nothing is imported, the adopted store wins totally.
            effectiveBaseline = baseline ?? servers
        } else {
            effectiveBaseline = baseline
        }
        let outcome = Reconciler.reconcile(
            store: loaded.store, claudeServers: servers,
            baseline: effectiveBaseline, directory: collectionDirectory(loaded.store.activeCollection))
        if outcome.storeChanged || loaded.corruptFileURL != nil {
            try saveStore(outcome.store)
        }
        return (outcome.store, notes, servers)
    }

    /// Backup mcps.json (if present), then atomically save the store.
    public func saveStore(_ store: MasterStore) throws {
        try backups.backUp(fileAt: paths.masterStoreURL, series: "mcps")
        try MasterStoreIO.save(store, to: paths.masterStoreURL, staging: paths.stagingDirURL)
    }

    /// Snapshot original (first run), backup Claude's config, then write the
    /// given servers into it, preserving all other keys.
    public func apply(servers: [String: JSONValue]) throws {
        try backups.ensureOriginalSnapshot(of: paths.claudeConfigURL)
        try backups.backUp(fileAt: paths.claudeConfigURL, series: "claude_desktop_config")
        try ClaudeConfigIO.write(mcpServers: servers, to: paths.claudeConfigURL,
                                 staging: paths.stagingDirURL)
    }

    /// The active collection's enabled subset — see `apply(servers:)`.
    public func apply(_ store: MasterStore) throws {
        try apply(servers: store.enabledServers)
    }

    /// The sidecar beside the master list; a missing or unreadable file loads as empty (see
    /// `CollectionsFile.load`), so there is nothing else for this method to handle.
    public func loadCollections() -> CollectionsFile {
        CollectionsFile.load(from: paths.collectionsFileURL)
    }

    /// Backup the existing sidecar (skipped when it doesn't exist yet — nothing to protect on
    /// the very first save), then atomically save the new one.
    public func saveCollections(_ file: CollectionsFile) throws {
        try backups.backUp(fileAt: paths.collectionsFileURL, series: "collections")
        try file.save(to: paths.collectionsFileURL, staging: paths.stagingDirURL)
    }

    /// Backup the current file, copy the chosen backup over it, then adopt the
    /// snapshot into the store — a restore is the user deliberately making the
    /// snapshot the truth, and anything less would leave a divergence for the
    /// next reload to regenerate away, silently undoing the restore.
    /// The backup's content is validated BEFORE the live file is touched.
    /// Returns the restored file's servers so the caller can sync its
    /// reconciliation baseline to them.
    /// `collectionDirectory` is the active collection's folder on this machine, written back as
    /// `${COLLECTION_DIR}` in what the snapshot brings into the store (`Reconciler.adoptSnapshot`).
    @discardableResult
    public func restoreClaudeConfig(from backup: URL,
                                    mergedWith store: MasterStore,
                                    collectionDirectory: String? = nil) throws
        -> [String: JSONValue] {
        let data = try Data(contentsOf: backup)
        let root: [String: Any]
        do {
            root = try ClaudeConfigIO.parseRoot(data)
        } catch ClaudeConfigError.malformed(let detail) {
            throw ClaudeConfigError.malformed(
                "backup \(backup.lastPathComponent) is not a valid config file (\(detail))")
        }
        // Validate the section this app depends on BEFORE writing: a wrong-typed
        // mcpServers would otherwise clobber the live file and only then throw
        // from the post-write read.
        if let rawServers = root["mcpServers"], !(rawServers is [String: Any]) {
            throw ClaudeConfigError.malformed(
                "backup \(backup.lastPathComponent) has an invalid mcpServers section")
        }
        try backups.backUp(fileAt: paths.claudeConfigURL, series: "claude_desktop_config")
        try AtomicFile.write(data, to: paths.claudeConfigURL, staging: paths.stagingDirURL)
        let servers = (root["mcpServers"] as? [String: Any] ?? [:]).mapValues(JSONValue.init(any:))
        let outcome = Reconciler.adoptSnapshot(store: store, servers: servers, directory: collectionDirectory)
        if outcome.storeChanged { try saveStore(outcome.store) }
        return servers
    }
}
