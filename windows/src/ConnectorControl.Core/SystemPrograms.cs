namespace ConnectorControl.Core;

/// <summary>
/// Windows only: the Mac starts Finder, open and every tool by absolute path already.
///
/// The Windows programs the app starts, by full path. A bare <c>cmd.exe</c> or <c>explorer.exe</c> is looked for
/// in the app's own folder before System32, so a file of that name dropped beside the app would run in its place.
/// </summary>
public static class SystemPrograms
{
    /// <summary>The command interpreter in System32.</summary>
    public static string CommandInterpreter => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>File Explorer in the Windows folder.</summary>
    public static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
}
