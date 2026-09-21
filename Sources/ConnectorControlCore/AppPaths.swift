import Foundation

public struct AppPaths: Sendable {
    public static let dataDirName = "Connector Control"
    public static let claudeConfigEnv = "CONNECTOR_CONTROL_CLAUDE_CONFIG"
    public static let storeDirEnv = "CONNECTOR_CONTROL_STORE_DIR"

    public let claudeConfigURL: URL
    public let storeDirURL: URL
    public let backupsDirURL: URL
    /// Where `AtomicFile` stages temp files before an atomic rename. Defaults under the
    /// store dir; `AppState.makeService` relocates it to the machine-local folder when a
    /// custom store dir is set — like backups, it must never live in a chosen (synced) folder.
    public let stagingDirURL: URL
    /// The machine-local bindings cache. Like backups, it never follows a chosen (synced)
    /// store dir: the paths in it are true on this machine only.
    public let collectionsCacheURL: URL

    public var masterStoreURL: URL { storeDirURL.appendingPathComponent("mcps.json") }

    /// The sidecar travels with the master list, so it sits beside it wherever that is.
    public var collectionsFileURL: URL { storeDirURL.appendingPathComponent(CollectionsFile.fileName) }

    public init(claudeConfigURL: URL, storeDirURL: URL, backupsDirURL: URL? = nil, stagingDirURL: URL? = nil,
                collectionsCacheURL: URL? = nil) {
        self.claudeConfigURL = claudeConfigURL
        self.storeDirURL = storeDirURL
        self.backupsDirURL = backupsDirURL ?? storeDirURL.appendingPathComponent("backups")
        self.stagingDirURL = stagingDirURL ?? storeDirURL.appendingPathComponent(".staging")
        self.collectionsCacheURL = collectionsCacheURL ?? storeDirURL.appendingPathComponent(CollectionsLocalCache.fileName)
    }

    /// `appSupport` is `~/Library/Application Support` in the app; the state
    /// tests point it at a temp directory so the whole path rule runs against
    /// a throwaway home (the Windows port injects `KnownFolders` the same way).
    /// No defaults: `PathContext` (the State module) is the one place that
    /// computes the live environment/appSupport, via `PathContext.live()`, and
    /// every caller threads its values through explicitly.
    public static func live(environment: [String: String], appSupport: URL) -> AppPaths {
        // An empty override counts as absent — an inherited but unset environment
        // variable must not shadow the real default (AppState.makeService and
        // PermissionsSweep apply the same rule to the stored setting).
        let claude = environment[claudeConfigEnv].flatMap { $0.isEmpty ? nil : $0 }.map(URL.init(fileURLWithPath:))
            ?? appSupport.appendingPathComponent("Claude/claude_desktop_config.json")
        let defaultStore = appSupport.appendingPathComponent(dataDirName)
        let store = environment[storeDirEnv].flatMap { $0.isEmpty ? nil : $0 }.map(URL.init(fileURLWithPath:))
            ?? defaultStore
        // The cache is computed from the default store dir, never the overridden one, so a
        // store dir pointed at a synced folder still leaves the per-machine bindings here.
        return AppPaths(claudeConfigURL: claude, storeDirURL: store,
                        collectionsCacheURL: defaultStore.appendingPathComponent(CollectionsLocalCache.fileName))
    }
}
