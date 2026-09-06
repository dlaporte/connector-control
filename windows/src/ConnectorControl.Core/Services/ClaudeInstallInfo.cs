namespace ConnectorControl.Core.Services;

/// <summary>
/// How Claude Desktop is installed on this PC. <c>LaunchTarget</c> is an
/// AUMID (<c>Claude_pzs8sxrjxfjjc!Claude</c>) for MSIX or an exe path for
/// legacy installs; null when not found. <c>InstallDirectory</c> is the folder
/// Claude's binaries live in (MSIX: the package's installed path under
/// <c>C:\Program Files\WindowsApps</c>; legacy: the exe's folder). It exists to
/// tell Claude Desktop's processes apart from other programs whose image is
/// also called <c>claude</c> (the Claude Code CLI, for one); null means the
/// location is unknown and processes are matched by name alone.
/// </summary>
public sealed record ClaudeInstallInfo(
    ClaudeInstallKind Kind,
    string? PackageFamilyName,
    string? LaunchTarget,
    string ProcessName,
    string? InstallDirectory = null)
{
    public const string DefaultProcessName = "claude";

    public static readonly ClaudeInstallInfo NotFound = new(ClaudeInstallKind.NotFound, null, null, DefaultProcessName);
}
