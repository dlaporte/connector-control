import Combine
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
    /// The shell command as an AppleScript expression, apart from the privileges it runs with: tests run it
    /// on its own, so none of them can ever ask for a password. `umask 022` first: `mkdir -p` may make
    /// /usr/local and /usr/local/bin, as root, and a permissive umask in the elevated shell would leave a
    /// folder on everyone's PATH that anybody can write.
    static let installShell = #""umask 022; if [ -L " & quoted form of (item 2 of argv) & " ]; then /bin/ln -sfn " & quoted form of (item 1 of argv) & " " & quoted form of (item 2 of argv) & "; elif [ -e " & quoted form of (item 2 of argv) & " ]; then echo \#(occupiedMarker) >&2; exit 3; else /bin/mkdir -p " & quoted form of (item 3 of argv) & " && /bin/ln -s " & quoted form of (item 1 of argv) & " " & quoted form of (item 2 of argv) & "; fi""#
    /// The tool, the link, the link's folder and the prompt are the run handler's items 1 to 4. A link is repointed
    /// with `ln -sfn`; where nothing is, a plain `ln -s`, which refuses at once if a file appears there after the
    /// check. Only a file that is swapped in for an existing link between the `-L` test and `ln -sfn` can be
    /// replaced: the shell has no rename that refuses to replace a file, and the folder is the administrator's.
    static let installScript = [
        "on run argv",
        "do shell script \(installShell) with prompt (item 4 of argv) with administrator privileges",
        "end run",
    ]
    /// The shell command as an AppleScript expression, apart from the privileges it runs with: tests run it
    /// on its own, so none of them can ever ask for a password.
    static let removeShell = #""if [ \"$(/usr/bin/readlink " & quoted form of (item 1 of argv) & ")\" = " & quoted form of (item 2 of argv) & " ]; then /bin/rm -f " & quoted form of (item 1 of argv) & "; fi""#
    /// The link, the tool and the prompt are items 1 to 3: the link goes only while `readlink` still gives the tool
    /// exactly as `remove` read it from the link, so the script and `isInstalled` judge the same text. Between
    /// `readlink` and `rm -f` a regular file swapped in for the link would go too: the folder is the
    /// administrator's, as for `ln -sfn` above, and the shell has no remove that refuses a file.
    static let removeScript = [
        "on run argv",
        "do shell script \(removeShell) with prompt (item 3 of argv) with administrator privileges",
        "end run",
    ]

    public let toolPath: String
    public let linkPath: String
    /// The link, whether or not it is there yet.
    public var installedLocation: String? { linkPath }
    /// Only while the administrator prompt's script runs: a link made or taken away directly, with no
    /// prompt, finishes inside the call and is never busy.
    public private(set) var isBusy = false
    public private(set) var lastProblem: String?
    private let changed = PassthroughSubject<Void, Never>()
    public var changes: AnyPublisher<Void, Never> { changed.eraseToAnyPublisher() }
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

    private var destination: String? { try? FileManager.default.destinationOfSymbolicLink(atPath: linkPath) }

    public var isInstalled: Bool {
        guard let destination else { return false }
        let folder = URL(fileURLWithPath: linkPath).deletingLastPathComponent()
        return URL(fileURLWithPath: destination, relativeTo: folder).standardizedFileURL.path
            == URL(fileURLWithPath: toolPath).standardizedFileURL.path
    }

    private var folder: String { (linkPath as NSString).deletingLastPathComponent }

    private var isLink: Bool { (try? FileManager.default.destinationOfSymbolicLink(atPath: linkPath)) != nil }

    public func install(completion: @escaping (String?) -> Void) {
        lastProblem = nil
        let completion = finishing(completion)
        guard !host.controlToolIsTransient else {
            completion(ClaudeIntegration.moveToApplicationsError)
            return
        }
        // Nothing to change, and no password to ask for.
        if isInstalled {
            completion(nil)
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
            if isLink { try SymlinkCommandLineTool.unlink(linkPath) }
            try fileManager.createSymbolicLink(atPath: linkPath, withDestinationPath: toolPath)
            completion(nil)
        } catch {
            completion(CommandLineToolText.installFailedError(AppState.friendly(error)))
        }
    }

    /// Takes away this install's own link and nothing else: a link to another copy, or someone's own
    /// file, stays.
    public func remove(completion: @escaping (String?) -> Void) {
        lastProblem = nil
        let completion = finishing(completion)
        guard let destination, isInstalled else {
            completion(nil)
            return
        }
        guard FileManager.default.isWritableFile(atPath: folder) else {
            run(SymlinkCommandLineTool.removeScript, [linkPath, destination, CommandLineToolText.adminPrompt],
                CommandLineToolText.removeFailedError, completion)
            return
        }
        do {
            try SymlinkCommandLineTool.unlink(linkPath)
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
        isBusy = true
        changed.send()
        host.background {
            let (code, error) = runScript(argv)
            host.marshal {
                // What AppleScript says ends the text: the cancel code, or the marker before the exit status.
                let said = error.trimmingCharacters(in: .whitespacesAndNewlines)
                let reason = SymlinkCommandLineTool.reason(from: said)
                if code == 0 || said.hasSuffix(SymlinkCommandLineTool.userCancelled) {
                    completion(nil)
                } else if reason.hasSuffix(SymlinkCommandLineTool.occupiedMarker) {
                    completion(occupied)
                } else {
                    completion(failure(reason))
                }
            }
        }
    }

    /// `completion`, run once the operation's end is kept and announced: not busy, and why it failed, if it
    /// did. Every path through `install` and `remove` ends through it, inside the call or after the prompt.
    private func finishing(_ completion: @escaping (String?) -> Void) -> (String?) -> Void {
        { problem in
            self.isBusy = false
            self.lastProblem = problem
            self.changed.send()
            completion(problem)
        }
    }

    /// `unlink(2)`, which never removes a directory, unlike `FileManager.removeItem`, which would take one that
    /// was swapped in for the link with everything inside it. A regular file swapped in between `isLink` and
    /// here would still go, from the person's own folder: only the directory case is closed.
    private static func unlink(_ path: String) throws {
        guard Darwin.unlink(path) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
    }

    /// The reason in what `osascript` wrote: without its "7:332: execution error: " lead and its " (1)" exit status.
    static func reason(from said: String) -> String {
        said.trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: #"^\d+:\d+: execution error: "#, with: "", options: .regularExpression)
            .replacingOccurrences(of: #" \(-?\d+\)$"#, with: "", options: .regularExpression)
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
