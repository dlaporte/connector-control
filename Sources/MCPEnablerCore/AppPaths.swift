import Foundation

public struct AppPaths {
    public let claudeConfigURL: URL
    public let storeDirURL: URL
    public let backupsDirURL: URL

    public var masterStoreURL: URL { storeDirURL.appendingPathComponent("mcps.json") }

    public init(claudeConfigURL: URL, storeDirURL: URL, backupsDirURL: URL? = nil) {
        self.claudeConfigURL = claudeConfigURL
        self.storeDirURL = storeDirURL
        self.backupsDirURL = backupsDirURL ?? storeDirURL.appendingPathComponent("backups")
    }

    public static func live(
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> AppPaths {
        let home = FileManager.default.homeDirectoryForCurrentUser
        let appSupport = home.appendingPathComponent("Library/Application Support")
        let claude = environment["MCP_ENABLER_CLAUDE_CONFIG"].map(URL.init(fileURLWithPath:))
            ?? appSupport.appendingPathComponent("Claude/claude_desktop_config.json")
        let store = environment["MCP_ENABLER_STORE_DIR"].map(URL.init(fileURLWithPath:))
            ?? appSupport.appendingPathComponent("Connector Control")
        return AppPaths(claudeConfigURL: claude, storeDirURL: store)
    }
}
