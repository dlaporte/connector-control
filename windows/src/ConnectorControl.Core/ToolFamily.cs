namespace ConnectorControl.Core;

/// <summary>What installs a tool: Node.js brings node and npx; uv brings uv and uvx.</summary>
public enum ToolFamily
{
    NodeJs,
    Uv,
}
