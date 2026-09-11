import Foundation

public struct AppPaths {
    public let claudeConfigURL: URL
    public let storeDirURL: URL
    public let backupsDirURL: URL
    /// Where `AtomicFile` creates its temp files (see `AtomicFile.privateStagingDirectory`).
    /// Like backups, always the app's own machine-local folder, never a chosen store folder.
    public let stagingDirURL: URL

    public var masterStoreURL: URL { storeDirURL.appendingPathComponent("mcps.json") }

    public init(claudeConfigURL: URL, storeDirURL: URL, backupsDirURL: URL? = nil, stagingDirURL: URL? = nil) {
        self.claudeConfigURL = claudeConfigURL
        self.storeDirURL = storeDirURL
        self.backupsDirURL = backupsDirURL ?? storeDirURL.appendingPathComponent("backups")
        self.stagingDirURL = stagingDirURL ?? storeDirURL.appendingPathComponent(".staging")
    }

    /// `appSupport` is `~/Library/Application Support` in the app; the state
    /// tests point it at a temp directory so the whole path rule runs against
    /// a throwaway home (the Windows port injects `KnownFolders` the same way).
    public static func live(
        environment: [String: String] = ProcessInfo.processInfo.environment,
        appSupport: URL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support")
    ) -> AppPaths {
        let claude = environment["CONNECTOR_CONTROL_CLAUDE_CONFIG"].map(URL.init(fileURLWithPath:))
            ?? appSupport.appendingPathComponent("Claude/claude_desktop_config.json")
        let store = environment["CONNECTOR_CONTROL_STORE_DIR"].map(URL.init(fileURLWithPath:))
            ?? appSupport.appendingPathComponent("Connector Control")
        return AppPaths(claudeConfigURL: claude, storeDirURL: store)
    }
}
