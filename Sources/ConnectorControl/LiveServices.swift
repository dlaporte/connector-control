import AppKit
import ConnectorControlCore
import ConnectorControlState

/// The composition root (catalog §0, §1.2): the platform services, then the
/// one AppState, in the order the old AppState.init did it — the legacy
/// migration before any defaults read, Sparkle started only with a bundle.
@MainActor
final class LiveServices {
    static let shared = LiveServices()

    let settings: UserDefaultsSettings
    let dialogs: AlertDialogs
    let notifier: UserNotificationsNotifier
    let claude: LiveClaudeProcess
    let autostart: SMAppServiceAutostart
    let updater: SparkleUpdater
    let host: AppHost
    let state: AppState

    private init() {
        LegacyMigration.run()
        settings = UserDefaultsSettings()
        dialogs = AlertDialogs()
        notifier = UserNotificationsNotifier()
        claude = LiveClaudeProcess(settings: settings)
        autostart = SMAppServiceAutostart()
        updater = SparkleUpdater()
        updater.startIfBundled()
        host = AppHost.live()
        state = AppState(settings: settings, claude: claude, notifier: notifier, dialogs: dialogs,
                         paths: .live(), host: host, toolProbe: ToolProbe.live())
        state.quitRequested = { NSApp.terminate(nil) }
    }
}
