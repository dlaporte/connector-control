namespace ConnectorControl.Core.State;

/// <summary>
/// Mirror: Sources/ConnectorControlState/CommandLineTool.swift
///
/// Putting <c>connector-control</c> on PATH for terminal use, and taking it off again. Claude never needs
/// it — its entry names the tool by its full path — so this is for people and scripts. The Mac links
/// /usr/local/bin/connector-control to the tool inside the app, through an administrator prompt;
/// Windows adds the folder of its copy to the user PATH.
/// </summary>
public interface ICommandLineTool
{
    /// <summary>The tool's full path, which scripts can always use, installed or not.</summary>
    string ToolPath { get; }

    /// <summary>Whether the tool is on PATH as this app put it there.</summary>
    bool IsInstalled { get; }

    /// <summary>Where the tool is on PATH, when that is one place a person can be told of: the Mac's link.
    /// Null on Windows, which puts a folder in the user PATH.</summary>
    string? InstalledLocation { get; }

    /// <summary>Install or Remove is running — an administrator prompt is up, or the PATH's change is being
    /// announced. The seam's, not a window's, so a Settings window opened meanwhile cannot start another.</summary>
    bool IsBusy { get; }

    /// <summary>Why the last Install or Remove failed, in the app's words; null from the moment the next one starts,
    /// and after one that was done or cancelled. The seam's, so any Settings window shows it.</summary>
    string? LastProblem { get; }

    /// <summary>Raised, on the UI thread, after <c>IsBusy</c> or <c>LastProblem</c> changes: as an operation starts and as it
    /// ends, even one that ends within the call. Every Settings window open then repaints, not only the one
    /// that started it.</summary>
    event Action? Changed;

    /// <summary>Puts the tool on PATH. The seam keeps <c>IsBusy</c> and <c>LastProblem</c> itself and announces them on
    /// <c>Changed</c>; a caller keeps no state of its own. <c>completion</c> runs on the UI thread, once, after they
    /// are set, with null when it is done or the person cancelled — <c>IsInstalled</c> says which — or the reason
    /// it failed, in the app's words. It may run before <c>Install</c> returns (nothing to do, or a refusal).</summary>
    void Install(Action<string?> completion);

    /// <summary>Takes the tool off PATH; <c>completion</c> as for <c>Install</c>.</summary>
    void Remove(Action<string?> completion);
}

/// <summary>
/// Mirror: Sources/ConnectorControlState/CommandLineTool.swift
///
/// What Settings' Command-Line Tool row says, and what installing or removing the tool says when it fails.
/// </summary>
public static class CommandLineToolText
{
    /// <summary>Why the Mac's administrator prompt asks. Windows asks for nothing, so never shows it.</summary>
    public const string AdminPrompt = "Connector Control needs your password to change the connector-control command in /usr/local/bin.";
    public const string NotInstalledText = "Not installed";
    public const string InPathText = "In your PATH";
    public const string InstallButton = "Install";
    public const string RemoveButton = "Remove";
    /// <summary>The command the caption sets apart.</summary>
    public const string HelpCommand = "connector-control help";
    // Platform idiom: a link in /usr/local/bin reaches every terminal at once, so the Mac names its Terminal
    // app; on Windows a terminal already open keeps the PATH it started with, so this says "a new terminal".
    public const string RunHelpCaption = "Run connector-control help in a new terminal.";

    public static string InstalledInText(string folder) => $"Installed in {folder}";

    public static string ScriptsCaption(string path) => $"Scripts can always use {path}";

    public static string InstallFailedError(string reason) => $"The command-line tool couldn’t be installed: {reason}";

    public static string RemoveFailedError(string reason) => $"The command-line tool couldn’t be removed: {reason}";

    /// <summary>Why installing failed when the tool's copy, made as the app starts, is not there.</summary>
    // Platform idiom: the Mac's link points at the tool inside the app's own sealed bundle, which cannot be
    // missing while the app runs, so only Windows, whose copy lives outside the install, ever says it.
    public static string MissingToolError(string path) => $"{path} is missing. Quit and reopen Connector Control, then try again.";

    public static string OccupiedError(string path) => $"{path} already exists and isn’t a link to Connector Control. Remove it, then try again.";
}
