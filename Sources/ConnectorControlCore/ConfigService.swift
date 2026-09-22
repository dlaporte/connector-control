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
    /// `lastAppliedCollection` is the collection Claude's file was last written from on this
    /// machine, and `lastAppliedNames` the connector names that apply wrote. Once the active
    /// collection has changed elsewhere, the names that collection renders are left where they
    /// are rather than poured into the active one; everything else the file holds is still taken
    /// in (`ingestible(_:lastApplied:lastAppliedNames:corrupt:store:)`). The caller then applies
    /// the active collection over the file.
    public func loadAndReconcile(baseline: [String: JSONValue]? = nil,
                                 storeAuthoritative: Bool = false,
                                 lastAppliedCollection: String? = nil,
                                 lastAppliedNames: Set<String>? = nil) throws
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
            store: loaded.store,
            claudeServers: ConfigService.ingestible(servers, lastApplied: lastAppliedCollection,
                                                    lastAppliedNames: lastAppliedNames,
                                                    corrupt: loaded.corruptFileURL != nil, store: loaded.store),
            baseline: effectiveBaseline)
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

    /// What a load may take out of Claude's file and into the active collection.
    ///
    /// All of it while the record of the last apply is the active collection, is missing — a first
    /// launch — or the store was corrupt and is being rebuilt from the file. Otherwise the file
    /// holds another collection's connectors, and the names that collection renders are left
    /// alone: pouring them into the active collection is what the record is for. Everything else
    /// is genuinely new — an installer's connector, a hand edit — and belongs to the collection the
    /// app is about to apply, whichever that is.
    ///
    /// A record naming a collection the store no longer has — deleted here, or on another machine,
    /// which is how a collection disappears from a store that syncs — has no render to compare
    /// against. The names the last apply wrote are that render, so those are left alone and
    /// everything else comes in: a connector an installer or a hand edit added survives, and a
    /// deleted collection's own connectors are not poured into the active one. Without those names
    /// — a cache written before they were kept — nothing here tells the two apart, and nothing is
    /// taken in rather than all of it.
    static func ingestible(_ servers: [String: JSONValue], lastApplied: String?,
                           lastAppliedNames: Set<String>?, corrupt: Bool,
                           store: MasterStore) -> [String: JSONValue] {
        guard !corrupt, let lastApplied, lastApplied != store.activeCollection else { return servers }
        guard let collection = store.collections[lastApplied] else {
            // Nothing here says what that collection rendered, so nothing in the file can be told
            // from it: taking it all in would pour a deleted collection's connectors, marked paths
            // and all, into the active one. Taking none is the safe half of that trade, and costs
            // only a hand-added connector, in the one state that reaches it — a cache from a build
            // that recorded the collection without the names.
            guard let lastAppliedNames else { return [:] }
            return servers.filter { !lastAppliedNames.contains($0.key) }
        }
        let rendered = Set(collection.mcps.filter { $0.value.enabled }.keys)
        return servers.filter { !rendered.contains($0.key) }
    }

    /// Snapshot original (first run), backup Claude's config, then write the
    /// given servers into it, preserving all other keys.
    ///
    /// `backedUpFrom` is the collection the file being backed up was last applied from, recorded
    /// against the backup (`BackupCollections`) so a restore of it goes back into that collection.
    /// A failed record never fails the apply: the backup then restores as an unrecorded one.
    public func apply(servers: [String: JSONValue], backedUpFrom collection: String? = nil) throws {
        try backups.ensureOriginalSnapshot(of: paths.claudeConfigURL)
        let backup = try backups.backUp(fileAt: paths.claudeConfigURL, series: "claude_desktop_config")
        recordBackup(backup, from: collection)
        try ClaudeConfigIO.write(mcpServers: servers, to: paths.claudeConfigURL,
                                 staging: paths.stagingDirURL)
    }

    private func recordBackup(_ backup: URL?, from collection: String?) {
        guard let backup, let collection else { return }
        try? BackupCollections.record(collection, for: backup, in: paths.backupsDirURL, staging: paths.stagingDirURL)
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
    /// `publishFolder` is the folder this machine publishes the active collection into, and
    /// `earlierFolders` the ones it published into before; a
    /// connector whose store copy renders exactly as the snapshot keeps the store copy
    /// (`Reconciler.adoptSnapshot`).
    ///
    /// The snapshot is adopted into `store`'s active collection; the caller makes that the
    /// collection the backup was taken from, and says `activating` when that is not the collection
    /// the saved store has active. `backedUpFrom`, as `apply` takes it, records the file this
    /// restore overwrites.
    @discardableResult
    public func restoreClaudeConfig(from backup: URL,
                                    mergedWith store: MasterStore,
                                    publishFolder: String? = nil,
                                    earlierFolders: [String] = [],
                                    backedUpFrom collection: String? = nil,
                                    activating: Bool = false) throws
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
        recordBackup(try backups.backUp(fileAt: paths.claudeConfigURL, series: "claude_desktop_config"), from: collection)
        try AtomicFile.write(data, to: paths.claudeConfigURL, staging: paths.stagingDirURL)
        let servers = (root["mcpServers"] as? [String: Any] ?? [:]).mapValues(JSONValue.init(any:))
        let outcome = Reconciler.adoptSnapshot(store: store, servers: servers, publishFolder: publishFolder,
                                               earlierFolders: earlierFolders)
        // A backup restored into a collection other than the active one makes that collection
        // active, so Claude's file and the store agree on where its connectors live — even when
        // the adoption itself changed nothing.
        if outcome.storeChanged || activating { try saveStore(outcome.store) }
        return servers
    }
}
