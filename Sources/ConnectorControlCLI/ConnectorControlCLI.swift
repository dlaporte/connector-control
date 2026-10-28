import Foundation
import ConnectorControlCore
import ConnectorControlState
import ConnectorControlTransport

/// Mirror: windows/src/ConnectorControl.Cli/Program.cs
///
/// `connector-control`, the command-line tool inside Connector Control: commands are an MCP client of
/// the running app's control socket, and `mcp` is the relay Claude Desktop runs. Every word it prints
/// comes from the shared model, so the Windows tool prints the same.
@main
enum ConnectorControlCLI {
    @MainActor
    static func main() {
        // A reader that has gone answers a write with EPIPE, which the tool handles, not a signal that
        // ends it: a closed pager or pipe in command mode as much as Claude's stream in the relay.
        signal(SIGPIPE, SIG_IGN)
        let executable = ControlEndpoint.executablePath()
        let version = ControlEndpoint.toolVersion(executablePath: executable)
        let context = PathContext.live()
        let socket = ControlEndpoint.socketPath(environment: context.environment, appSupport: context.appSupport)
        let invocation = ControlCommandLine.parse(Array(CommandLine.arguments.dropFirst()),
                                                  workingDirectory: FileManager.default.currentDirectoryPath,
                                                  readFile: { try Data(contentsOf: URL(fileURLWithPath: $0)) })
        // Read here, on the main actor; the command's reading thread prints them.
        let starting = ControlClient.startingText
        let queued = ControlTools.queuedText
        // Starting is allowed only for the tool that came with an app (launchApp refuses any other), and
        // never while CONNECTOR_CONTROL_STORE_DIR names a store the app it would start does not read.
        var launch: (@Sendable () -> Bool)?
        if ControlEndpoint.mayStartApp(environment: context.environment) {
            launch = { ControlEndpoint.launchApp(executablePath: executable) }
        }
        func client(announcing: Bool) -> ControlClient {
            // The relay's stdout is Claude's protocol stream: nothing of ours goes on it.
            var announce: (@Sendable () -> Void)?
            if announcing {
                announce = { printError(starting) }
            }
            return ControlClient(connectOnce: { try? ControlSocketStream.connect(to: socket) },
                                 launch: launch, announce: announce)
        }
        switch invocation {
        case .help:
            emit(ControlOutput(stdout: ControlTools.helpText(), stderr: nil, exitCode: ControlOutput.doneExit))
        case .version:
            emit(ControlOutput(stdout: ControlCommandLine.versionLine(version), stderr: nil, exitCode: ControlOutput.doneExit))
        case .usage(let message):
            emit(.usage(message))
        case .relay:
            exit(ControlRelayRunner(client: client(announcing: false), version: version).run())
        case .call(let tool, let arguments, let json):
            let stream: ControlStream
            do {
                stream = try client(announcing: true).connect()
            } catch {
                emit(.unreachable((error as? ControlClient.Failure)?.message ?? ControlClient.unreachableError))
            }
            emit(ControlCommandRunner.run(tool: tool, arguments: arguments, json: json, version: version, over: stream,
                                          notice: { printError(queued) }))
        }
    }

    static func emit(_ output: ControlOutput) -> Never {
        if let text = output.stdout { write(text, to: .standardOutput) }
        if let text = output.stderr { printError(text) }
        exit(output.exitCode)
    }

    static func printError(_ text: String) {
        write(text, to: .standardError)
    }

    /// One line; a reader that has gone is no reason to fail.
    static func write(_ text: String, to handle: FileHandle) {
        try? handle.write(contentsOf: Data((text + "\n").utf8))
    }
}
