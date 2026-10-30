import Foundation
import ConnectorControlCore
import ConnectorControlState
import ConnectorControlTransport

/// Mirror: windows/tests/ConnectorControl.TestHost/Program.cs
///
/// `connector-control-test-host <sandbox>`: the real control layer - AppState, ControlTools, the server
/// and its socket - over a sandbox folder, with no window, no Claude and no updater, for the MCP
/// Inspector and the relay checks. Every path comes from the sandbox argument and none from this
/// process's environment, so it never reaches a person's store or Claude's real config. The
/// integration is on, confirmations are on, and every question is declined: gated changes are refused.
@main
enum TestHost {
    @MainActor
    static func main() {
        let arguments = CommandLine.arguments
        guard arguments.count == 2, arguments[1].hasPrefix("/") else {
            fail("usage: connector-control-test-host <absolute sandbox folder>", code: 2)
        }
        let sandbox = URL(fileURLWithPath: arguments[1], isDirectory: true)
        let store = sandbox.appendingPathComponent("store")
        let claudeConfig = sandbox.appendingPathComponent("claude_desktop_config.json")
        let appSupport = sandbox.appendingPathComponent("Application Support")
        let environment = [AppPaths.storeDirEnv: store.path, AppPaths.claudeConfigEnv: claudeConfig.path]
        do {
            try FileManager.default.createDirectory(at: store, withIntermediateDirectories: true)
            try FileManager.default.createDirectory(at: appSupport, withIntermediateDirectories: true)
            if !FileManager.default.fileExists(atPath: claudeConfig.path) {
                try Data(TestHostServices.seedConfig.utf8).write(to: claudeConfig)
            }
        } catch {
            fail("\(error)", code: 1)
        }
        let settings = TestHostSettings()
        let host = AppHost.live(controlToolPath: sandbox.appendingPathComponent("connector-control").path)
        let state = AppState(settings: settings, claude: TestHostClaude(), notifier: TestHostNotifier(), dialogs: TestHostDialogs(),
                             paths: PathContext(environment: environment, appSupport: appSupport), host: host,
                             toolProbe: TestHostToolProbe())
        let model = SettingsModel(state: state, settings: settings, autostart: TestHostAutostart(), updater: TestHostUpdater())
        let tools = ControlTools(state: state, settings: model, confirmer: DecliningConfirmer(), host: host)
        let server = ControlServer(tools: tools)
        let socket = ControlEndpoint.socketPath(environment: environment, appSupport: appSupport)
        let endpoint: ControlSocketServer
        do {
            endpoint = try ControlSocketServer.start(path: socket, server: server, host: host)
        } catch {
            fail("\(error)", code: 1)
        }
        print("ready \(socket)")
        fflush(stdout)
        // SIGTERM stops it as a quit stops the app; SIGKILL, as a crash or an update would, leaves the
        // socket file for the next start to find stale.
        signal(SIGTERM, SIG_IGN)
        let terminate = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        terminate.setEventHandler {
            endpoint.stop()
            server.closeAll()
            exit(0)
        }
        terminate.resume()
        withExtendedLifetime((state, model, tools, terminate)) {
            // The main run loop, not dispatchMain(): AppHost.live marshals with MainActor.assumeIsolated,
            // which only holds on the thread a run loop drives.
            while true {
                RunLoop.main.run(until: .distantFuture)
            }
        }
    }

    static func fail(_ message: String, code: Int32) -> Never {
        FileHandle.standardError.write(Data("connector-control-test-host: \(message)\n".utf8))
        exit(code)
    }
}
