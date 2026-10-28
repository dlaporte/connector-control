import Foundation

/// Mirror: windows/src/ConnectorControl.Core/State/CommandLineTool.cs
///
/// Putting `connector-control` on PATH for terminal use, and taking it off again. Claude never needs
/// it — its entry names the tool by its full path — so this is for people and scripts. The Mac links
/// /usr/local/bin/connector-control to the tool inside the app, through an administrator prompt;
/// Windows adds the folder of its copy to the user PATH.
@MainActor
public protocol CommandLineTool: AnyObject {
    /// The tool's full path, which scripts can always use, installed or not.
    var toolPath: String { get }
    /// Whether the tool is on PATH as this app put it there.
    var isInstalled: Bool { get }
    /// Puts the tool on PATH. `completion` runs on the main actor, once, with nil when it is done or the
    /// person cancelled — `isInstalled` says which — or the reason it failed, in the app's words.
    func install(completion: @escaping (String?) -> Void)
    /// Takes the tool off PATH; `completion` as for `install`.
    func remove(completion: @escaping (String?) -> Void)
}

/// Mirror: windows/src/ConnectorControl.Core/State/CommandLineTool.cs
///
/// What installing or removing the command-line tool says when it fails.
public enum CommandLineToolText {
    /// Why the Mac's administrator prompt asks. Windows asks for nothing, so never shows it.
    public static let adminPrompt = "Connector Control needs your password to change the connector-control command in /usr/local/bin."

    public static func installFailedError(_ reason: String) -> String { "The command-line tool couldn’t be installed: \(reason)" }

    public static func removeFailedError(_ reason: String) -> String { "The command-line tool couldn’t be removed: \(reason)" }

    public static func occupiedError(_ path: String) -> String { "\(path) already exists and isn’t a link to Connector Control. Remove it, then try again." }
}
