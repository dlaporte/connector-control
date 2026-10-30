import Combine
import Foundation
import ConnectorControlCore
import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.TestHost/TestHostServices.cs
///
/// The platform seams of a host with no platform: settings in memory, no Claude, no notifications
/// but a line on stderr, no dialogs, no launchers found, no login item and no updates.
enum TestHostServices {
    static let seedConfig = #"{"mcpServers":{"fetch":{"command":"uvx","args":["mcp-server-fetch"]},"time":{"command":"uvx","args":["mcp-server-time"]}}}"#
    static let noClaude = "The test host has no Claude Desktop to restart."
}

@MainActor
final class TestHostSettings: AppSettings {
    var masterStoreDir: String?
    var claudeAppPath: String?
    var backupKeepCount = BackupManager.defaultKeepCount
    var notifyExternalChanges = false
    var confirmBeforeRestart = true
    var confirmBeforeQuit = true
    var claudeIntegrationEnabled = true
    var confirmClaudeChanges = true
    var claudeIntegrationOfferAnswered = true
    var lastApplyDate: Date?
    var sweepVersion = PermissionsSweep.currentVersion
}

@MainActor
final class TestHostClaude: ClaudeProcess {
    var isRunning: Bool { false }
    var launchDate: Date? { nil }
    func restart(completion: @escaping @Sendable (String?) -> Void) { completion(TestHostServices.noClaude) }
}

@MainActor
final class TestHostNotifier: Notifier {
    var onRestartAction: MainActorAction?
    func notify(title: String, body: String, category: String?) {
        FileHandle.standardError.write(Data("notification: \(title): \(body)\n".utf8))
    }
}

@MainActor
final class TestHostDialogs: Dialogs {
    func confirm(message: String, informative: String?, primary: String, cancel: String, destructive: Bool,
                 cancelIsDefault: Bool) -> Bool { false }
    func promptForName(title: String, initial: String) -> String? { nil }
    func inform(message: String, informative: String?) {}
    func confirmChange(_ dialog: ConfirmationDialog) -> Bool { false }
}

struct TestHostToolProbe: ToolProbing {
    func probe(_ tools: [Tool]) -> [Tool: ToolStatus] { Dictionary(uniqueKeysWithValues: tools.map { ($0, .notFound) }) }
}

@MainActor
final class TestHostAutostart: Autostart {
    var isEnabled: Bool { false }
    var requiresApproval: Bool { false }
    func setEnabled(_ enabled: Bool) throws {}
}

@MainActor
final class TestHostUpdater: Updater {
    var isAvailable: Bool { false }
    var versionDisplay: String { AppVersion.developmentBuild }
    var automaticallyDownloadsUpdates = false
    var automaticallyDownloadsUpdatesPublisher: AnyPublisher<Bool, Never> { Just(false).eraseToAnyPublisher() }
    func checkForUpdates() {}
}
