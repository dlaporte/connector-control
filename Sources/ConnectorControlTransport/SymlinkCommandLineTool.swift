import Foundation
import ConnectorControlState

/// Mac only: the Windows counterpart is windows/src/ConnectorControl.Core/Transport/UserPathCommandLineTool.cs.
///
/// The command-line tool on PATH as a link at /usr/local/bin/connector-control to the tool inside the
/// app. Where the person can write that folder (Homebrew on Intel makes it theirs) the link is made
/// directly; elsewhere `osascript` makes it with administrator privileges, the paths and the prompt
/// passed as the run handler's arguments and quoted with `quoted form of`, so no path is ever read as
/// script or shell. A file at the link's path that is not a link is never replaced — the script checks
/// again under the password, in case one appeared — and Remove takes away only this install's own link.
/// An app run from where it will not stay installs nothing.
@MainActor
public final class SymlinkCommandLineTool: CommandLineTool {
    public nonisolated static let defaultLinkPath = "/usr/local/bin/connector-control"
    /// What AppleScript says when the person presses Cancel in the administrator prompt.
    static let userCancelled = "(-128)"
    /// What the install script says when a file that is not a link holds the link's path.
    static let occupiedMarker = "connector-control-occupied"
    /// The tool, the link, the link's folder and the prompt are the run handler's items 1 to 4.
    static let installScript = [
        "on run argv",
        #"do shell script "if [ -e " & quoted form of (item 2 of argv) & " ] && [ ! -L " & quoted form of (item 2 of argv) & " ]; then echo connector-control-occupied >&2; exit 3; fi; /bin/mkdir -p " & quoted form of (item 3 of argv) & " && /bin/ln -sfn " & quoted form of (item 1 of argv) & " " & quoted form of (item 2 of argv) with prompt (item 4 of argv) with administrator privileges"#,
        "end run",
    ]
    /// The link, the tool and the prompt are items 1 to 3: the link goes only while it points at the tool.
    static let removeScript = [
        "on run argv",
        #"do shell script "if [ \"$(/usr/bin/readlink " & quoted form of (item 1 of argv) & ")\" = " & quoted form of (item 2 of argv) & " ]; then /bin/rm -f " & quoted form of (item 1 of argv) & "; fi" with prompt (item 3 of argv) with administrator privileges"#,
        "end run",
    ]

    public let toolPath: String
    public let linkPath: String
    private let host: AppHost
    private let runScript: @Sendable ([String]) -> (Int32, String)

    /// `runScript` runs `osascript` with its arguments, off the main actor, and gives back its exit status and
    /// what it wrote on standard error; tests replace it.
    public init(toolPath: String, linkPath: String = SymlinkCommandLineTool.defaultLinkPath, host: AppHost,
                runScript: @escaping @Sendable ([String]) -> (Int32, String) = { SymlinkCommandLineTool.osascript($0) }) {
        self.toolPath = toolPath
        self.linkPath = linkPath
        self.host = host
        self.runScript = runScript
    }

    public var isInstalled: Bool {
        guard let destination = try? FileManager.default.destinationOfSymbolicLink(atPath: linkPath) else { return false }
        let folder = URL(fileURLWithPath: linkPath).deletingLastPathComponent()
        return URL(fileURLWithPath: destination, relativeTo: folder).standardizedFileURL.path
            == URL(fileURLWithPath: toolPath).standardizedFileURL.path
    }

    private var folder: String { (linkPath as NSString).deletingLastPathComponent }

    private var isLink: Bool { (try? FileManager.default.destinationOfSymbolicLink(atPath: linkPath)) != nil }

    public func install(completion: @escaping (String?) -> Void) {
        guard !host.controlToolIsTransient else {
            completion(ClaudeIntegration.moveToApplicationsError)
            return
        }
        let fileManager = FileManager.default
        // attributesOfItem looks at the link itself, not where it points.
        if (try? fileManager.attributesOfItem(atPath: linkPath)) != nil, !isLink {
            completion(CommandLineToolText.occupiedError(linkPath))
            return
        }
        guard fileManager.isWritableFile(atPath: folder) else {
            run(SymlinkCommandLineTool.installScript, [toolPath, linkPath, folder, CommandLineToolText.adminPrompt],
                CommandLineToolText.installFailedError, completion)
            return
        }
        do {
            if isLink { try fileManager.removeItem(atPath: linkPath) }
            try fileManager.createSymbolicLink(atPath: linkPath, withDestinationPath: toolPath)
            completion(nil)
        } catch {
            completion(CommandLineToolText.installFailedError(AppState.friendly(error)))
        }
    }

    /// Takes away this install's own link and nothing else: a link to another copy, or someone's own
    /// file, stays.
    public func remove(completion: @escaping (String?) -> Void) {
        guard isInstalled else {
            completion(nil)
            return
        }
        guard FileManager.default.isWritableFile(atPath: folder) else {
            run(SymlinkCommandLineTool.removeScript, [linkPath, toolPath, CommandLineToolText.adminPrompt],
                CommandLineToolText.removeFailedError, completion)
            return
        }
        do {
            try FileManager.default.removeItem(atPath: linkPath)
            completion(nil)
        } catch {
            completion(CommandLineToolText.removeFailedError(AppState.friendly(error)))
        }
    }

    private func run(_ script: [String], _ arguments: [String], _ failure: @escaping (String) -> String,
                     _ completion: @escaping (String?) -> Void) {
        let argv = script.flatMap { ["-e", $0] } + arguments
        let runScript = self.runScript
        let host = self.host
        let occupied = CommandLineToolText.occupiedError(linkPath)
        host.background {
            let (code, error) = runScript(argv)
            host.marshal {
                if code == 0 || error.contains(SymlinkCommandLineTool.userCancelled) {
                    completion(nil)
                } else if error.contains(SymlinkCommandLineTool.occupiedMarker) {
                    completion(occupied)
                } else {
                    completion(failure(error.trimmingCharacters(in: .whitespacesAndNewlines)))
                }
            }
        }
    }

    /// `/usr/bin/osascript` with `arguments`: its exit status and what it wrote on standard error.
    public nonisolated static func osascript(_ arguments: [String]) -> (Int32, String) {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        process.arguments = arguments
        let errors = Pipe()
        process.standardError = errors
        process.standardOutput = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            return (-1, error.localizedDescription)
        }
        let said = errors.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return (process.terminationStatus, String(decoding: said, as: UTF8.self))
    }
}
