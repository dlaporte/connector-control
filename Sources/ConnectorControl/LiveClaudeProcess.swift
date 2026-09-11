import AppKit
import ConnectorControlState

/// Catalog §1.11 and §6.1 over NSRunningApplication and ClaudeRestarter.
@MainActor
final class LiveClaudeProcess: ClaudeProcess {
    private let settings: AppSettings

    init(settings: AppSettings) {
        self.settings = settings
    }

    private var running: NSRunningApplication? {
        NSRunningApplication.runningApplications(withBundleIdentifier: ClaudeRestarter.bundleID).first
    }

    var isRunning: Bool { running != nil }

    var launchDate: Date? { running?.launchDate }

    func restart(completion: @escaping @Sendable (String?) -> Void) {
        let appURL = URL(fileURLWithPath: settings.claudeAppPath ?? AppState.defaultClaudeAppPath)
        ClaudeRestarter.restart(appURL: appURL, completion: completion)
    }
}
