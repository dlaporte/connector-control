namespace ConnectorControl.Core;

/// <summary>
/// How the mcp-remote bridge is launched. Windows MCP docs use <c>cmd /c npx</c>; the Mac app writes
/// bare <c>npx</c>. Under CmdNpx every argument is re-parsed by cmd.exe, so
/// <see cref="RemotePattern.CmdUnsafeField"/> refuses its metacharacters in the fields that reach
/// <c>args</c> (URL, header name, OAuth client fields); a <c>%</c> pair only earns a caution; values
/// that travel in env are exempt.
/// </summary>
public enum RemoteLaunchStyle
{
    Npx,
    CmdNpx,
}
