import AppKit
import ConnectorControlCore
import ConnectorControlState
import ConnectorControlTransport

/// The composition root: the platform services, then the one AppState, in the
/// order the old AppState.init did it — Sparkle starts only with a bundle — then
/// the control layer Claude and the command-line tool reach.
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
    /// The one ControlTools every connection shares, the server that gives each its own session, and
    /// the socket it listens on — nil when it could not listen: another copy of the app serves, or a
    /// development run's store folder makes too long a path.
    let controlTools: ControlTools
    let controlServer: ControlServer
    private(set) var controlEndpoint: ControlSocketServer?
    /// Puts the tool on PATH for terminal use; the Settings group binds it.
    let commandLineTool: CommandLineTool
    private let controlSettings: SettingsModel

    private init() {
        settings = UserDefaultsSettings()
        dialogs = AlertDialogs()
        notifier = UserNotificationsNotifier()
        claude = LiveClaudeProcess(settings: settings)
        autostart = SMAppServiceAutostart()
        updater = SparkleUpdater()
        updater.startIfBundled()
        let paths = PathContext.live()
        // The tool inside this very bundle, worked out at each launch: an app that was moved or updated
        // writes its new place into Claude's entry when the launch's reload applies.
        let toolPath = ControlEndpoint.bundledToolPath(
            appExecutable: Bundle.main.executableURL ?? URL(fileURLWithPath: CommandLine.arguments[0]))
        // Run from a translocated copy or its disk image, the path is passing: the model never writes it.
        host = AppHost.live(controlToolPath: toolPath,
                            controlToolIsTransient: ControlEndpoint.isTransient(bundle: Bundle.main.bundleURL))
        state = AppState(settings: settings, claude: claude, notifier: notifier, dialogs: dialogs,
                         paths: paths, host: host, toolProbe: ToolProbe.live())
        state.quitRequested = { NSApp.terminate(nil) }
        controlSettings = SettingsModel(state: state, settings: settings, autostart: autostart, updater: updater)
        // Declines every question: until the app can show its confirmation dialog, a change Claude must
        // be asked about is refused rather than made unasked.
        controlTools = ControlTools(state: state, settings: controlSettings, confirmer: DecliningConfirmer(), host: host)
        controlServer = ControlServer(tools: controlTools)
        commandLineTool = SymlinkCommandLineTool(toolPath: toolPath, host: host)
        do {
            controlEndpoint = try ControlSocketServer.start(
                path: ControlEndpoint.socketPath(environment: paths.environment, appSupport: paths.appSupport),
                server: controlServer, host: host)
        } catch {
            // The system keeps NSLog's lines. The Windows counterpart is App.Log, which writes crash.log in the
            // app's data folder, since a tray app there has no console.
            NSLog("Connector Control: the control socket is unavailable: %@", String(describing: error))
            state.reportControlEndpointUnavailable()
        }
        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil,
                                               queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.stopControl() }
        }
    }

    /// Quitting: every connection ends, and no socket file is left behind.
    func stopControl() {
        controlEndpoint?.stop()
        controlEndpoint = nil
        controlServer.closeAll()
    }
}
