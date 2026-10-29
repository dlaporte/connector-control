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
        // The arguments first: help, --version and a usage error need nothing of this Mac's, so nothing
        // about it can keep them from answering. Only a command that goes to the app looks for its socket.
        let invocation = ControlCommandLine.parse(Array(CommandLine.arguments.dropFirst()),
                                                  workingDirectory: FileManager.default.currentDirectoryPath,
                                                  readFile: { try Data(contentsOf: URL(fileURLWithPath: $0)) })
        let executable = ControlEndpoint.executablePath()
        let version = ControlEndpoint.toolVersion(executablePath: executable)
        switch invocation {
        case .help:
            emit(ControlOutput(stdout: ControlTools.helpText(), stderr: nil, exitCode: ControlOutput.doneExit))
        case .version:
            emit(ControlOutput(stdout: ControlCommandLine.versionLine(version), stderr: nil, exitCode: ControlOutput.doneExit))
        case .usage(let message):
            emit(.usage(message))
        case .relay:
            exit(ControlRelayRunner(client: client(executable: executable, announcing: false), version: version).run())
        case .call(let tool, let arguments, let json):
            // Read on the main actor, where ControlTools' strings live; the notice runs inside the command.
            let queued = ControlTools.queuedText
            let stream: ControlStream
            // Anything connect throws ends as the tool's one other failure does, in a line: Swift has no
            // unhandled exception to catch beyond this, and a terminal needs no code page put back after a
            // Ctrl-C, which is what C#'s catch-all and ConsoleText exist for.
            do {
                stream = try client(executable: executable, announcing: true).connect()
            } catch {
                emit(.unreachable((error as? ControlClient.Failure)?.message ?? ControlClient.unreachableError))
            }
            emit(ControlCommandRunner.run(tool: tool, arguments: arguments, json: json, version: version, over: stream,
                                          notice: { printError(queued) }))
        }
    }

    /// The way to the running app, worked out only for a command that goes to it: its socket, and whether
    /// this tool may start it.
    @MainActor
    static func client(executable: String, announcing: Bool) -> ControlClient {
        let context = PathContext.live()
        let socket = ControlEndpoint.socketPath(environment: context.environment, appSupport: context.appSupport)
        // Starting is allowed only for the tool that came with an app (launchApp refuses any other), and
        // never while CONNECTOR_CONTROL_STORE_DIR names a store the app it would start does not read.
        var launch: (@Sendable () -> Bool)?
        if ControlEndpoint.mayStartApp(environment: context.environment) {
            launch = { ControlEndpoint.launchApp(executablePath: executable) }
        }
        // The relay's stdout is Claude's protocol stream: nothing of ours goes on it.
        var announce: (@Sendable () -> Void)?
        if announcing {
            announce = { printError(ControlClient.startingText) }
        }
        return ControlClient(connectOnce: { try? ControlSocketStream.connect(to: socket) }, launch: launch, announce: announce)
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
