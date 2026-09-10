namespace ConnectorControl.Core;

/// <summary>
/// How the mcp-remote bridge is launched. Windows MCP docs use <c>cmd /c npx</c>; the Mac app writes
/// bare <c>npx</c>. Under CmdNpx every argument is re-parsed by cmd.exe (see
/// <see cref="RemotePattern.CmdUnsafeCharacters"/>), so the editor refuses its metacharacters there.
/// </summary>
public enum RemoteLaunchStyle
{
    Npx,
    CmdNpx,
}
