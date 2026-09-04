namespace ConnectorControl.Core.Services;

/// <summary>
/// How Claude Desktop is installed on this PC. <c>LaunchTarget</c> is an
/// AUMID (<c>Claude_pzs8sxrjxfjjc!Claude</c>) for MSIX or an exe path for
/// legacy installs; null when not found.
/// </summary>
public sealed record ClaudeInstallInfo(
    ClaudeInstallKind Kind,
    string? PackageFamilyName,
    string? LaunchTarget,
    string ProcessName)
{
    public const string DefaultProcessName = "claude";

    public static readonly ClaudeInstallInfo NotFound = new(ClaudeInstallKind.NotFound, null, null, DefaultProcessName);
}
