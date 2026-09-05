namespace ConnectorControl.Core;

/// <summary>
/// The four launchers a connector's command can name — what <c>npx mcp-remote</c> and most
/// local servers run through. When one is missing from the PATH Claude Desktop uses, Claude
/// shows only "server disconnected" (spec 2026-09-05-tool-probe §3.1). Declaration order is
/// the Settings order.
/// </summary>
public enum Tool
{
    Npx,
    Node,
    Uvx,
    Uv,
}
