namespace ConnectorControl.Core;

/// <summary>How the mcp-remote bridge is launched. Windows MCP docs use <c>cmd /c npx</c>; the Mac app writes bare <c>npx</c>.</summary>
public enum RemoteLaunchStyle
{
    Npx,
    CmdNpx,
}
