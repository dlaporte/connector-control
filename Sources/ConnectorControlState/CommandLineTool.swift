import Combine
import Foundation

/// Mirror: windows/src/ConnectorControl.Core/State/CommandLineTool.cs
///
/// Putting `connector-control` on PATH for terminal use, and taking it off again. Claude never needs
/// it - its entry names the tool by its full path - so this is for people and scripts. The Mac links
/// /usr/local/bin/connector-control to the tool inside the app, through an administrator prompt;
/// Windows adds the folder of its copy to the user PATH.
@MainActor
public protocol CommandLineTool: AnyObject {
    /// The tool's full path, which scripts can always use, installed or not.
    var toolPath: String { get }
    /// Whether the tool is on PATH as this app put it there.
    var isInstalled: Bool { get }
    /// Where the tool is on PATH, when that is one place a person can be told of: the Mac's link.
    /// nil on Windows, which puts a folder in the user PATH.
    var installedLocation: String? { get }
    /// Install or Remove is running - an administrator prompt is up, or the PATH's change is being
    /// announced. The seam's, not a window's, so a Settings window opened meanwhile cannot start another.
    var isBusy: Bool { get }
    /// Why the last Install or Remove failed, in the app's words; nil from the moment the next one starts,
    /// and after one that was done or cancelled. The seam's, so any Settings window shows it.
    var lastProblem: String? { get }
    /// Sends, on the main actor, after `isBusy` or `lastProblem` changes: as an operation starts and as it
    /// ends, even one that ends within the call. Every Settings window open then repaints, not only the one
    /// that started it.
    var changes: AnyPublisher<Void, Never> { get }
    /// Puts the tool on PATH. The seam keeps `isBusy` and `lastProblem` itself and announces them on
    /// `changes`; a caller keeps no state of its own. `completion` runs on the main actor, once, after they
    /// are set, with nil when it is done or the person cancelled - `isInstalled` says which - or the reason
    /// it failed, in the app's words. It may run before `install` returns (nothing to do, or a refusal).
    func install(completion: @escaping (String?) -> Void)
    /// Takes the tool off PATH; `completion` as for `install`.
    func remove(completion: @escaping (String?) -> Void)
}

/// Mirror: windows/src/ConnectorControl.Core/State/CommandLineTool.cs
///
/// What Settings' Command-Line Tool row says, and what installing or removing the tool says when it fails.
public enum CommandLineToolText {
    /// Why the Mac's administrator prompt asks. Windows asks for nothing, so never shows it.
    public static let adminPrompt = "Connector Control needs your password to change the connector-control command in /usr/local/bin."
    public static let notInstalledText = "Not installed"
    public static let inPathText = "In your PATH"
    public static let installButton = "Install"
    public static let removeButton = "Remove"
    /// The command the caption sets apart.
    public static let helpCommand = "connector-control help"
    // Platform idiom: a link in /usr/local/bin reaches every terminal at once, so the Mac names its Terminal
    // app; on Windows a terminal already open keeps the PATH it started with, so the mirror says "a new terminal".
    public static let runHelpCaption = "Run connector-control help in Terminal."

    public static func installedInText(_ folder: String) -> String { "Installed in \(folder)" }

    public static func scriptsCaption(_ path: String) -> String { "Scripts can always use \(path)" }

    public static func installFailedError(_ reason: String) -> String { "The command-line tool couldn’t be installed: \(reason)" }

    public static func removeFailedError(_ reason: String) -> String { "The command-line tool couldn’t be removed: \(reason)" }

    /// Why installing failed when the tool's copy, made as the app starts, is not there.
    // Platform idiom: the Mac's link points at the tool inside the app's own sealed bundle, which cannot be
    // missing while the app runs, so only Windows, whose copy lives outside the install, ever says it.
    public static func missingToolError(_ path: String) -> String { "\(path) is missing. Quit and reopen Connector Control, then try again." }

    public static func occupiedError(_ path: String) -> String { "\(path) already exists and isn’t a link. Remove it, then try again." }
}
