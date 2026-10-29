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

    /// <summary>Puts the tool on PATH. <c>completion</c> runs on the UI thread, once, with null when it is done or the
    /// person cancelled — <c>IsInstalled</c> says which — or the reason it failed, in the app's words.
    /// It may run before <c>Install</c> returns (nothing to do, or a refusal), so a caller sets its busy state before
    /// calling, never after.</summary>
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

    public static string InstalledAtText(string path) => $"Installed at {path}";

    public static string ScriptsCaption(string path) => $"Scripts can always use {path}";

    public static string InstallFailedError(string reason) => $"The command-line tool couldn’t be installed: {reason}";

    public static string RemoveFailedError(string reason) => $"The command-line tool couldn’t be removed: {reason}";

    public static string OccupiedError(string path) => $"{path} already exists and isn’t a link to Connector Control. Remove it, then try again.";
}
