import Foundation
import ConnectorControlCore

/// One-time migration from the app's previous names (catalog §1.14). Runs on
/// every launch; idempotent. The directory move is separate from the defaults
/// move so it can be tested against a temp directory.
public enum LegacyMigration {
    public static let currentDirectoryName = AppPaths.dataDirName
    /// Newest first: only the first existing old directory is moved.
    public static let legacyDirectoryNames = ["Custom Connector Control", "MCP Enabler"]
    public static let legacyDefaultsDomains = ["com.dlaporte.custom-connector-control", "com.dlaporte.mcp-enabler"]

    public static func run(
        appSupport: URL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support"),
        defaults: UserDefaults = .standard
    ) {
        moveLegacyDirectory(appSupport: appSupport)
        for domain in legacyDefaultsDomains {
            guard let legacy = UserDefaults(suiteName: domain) else { continue }
            DefaultsKey.migrate(from: legacy, into: defaults)
        }
    }

    public static func moveLegacyDirectory(appSupport: URL, fileManager fm: FileManager = .default) {
        let new = appSupport.appendingPathComponent(currentDirectoryName)
        for oldName in legacyDirectoryNames {
            let old = appSupport.appendingPathComponent(oldName)
            if fm.fileExists(atPath: old.path), !fm.fileExists(atPath: new.path) {
                try? fm.moveItem(at: old, to: new)
            }
        }
    }
}
