namespace ConnectorControl.Core;

/// <summary>Names, families, and the per-family install link and command (spec §3.1, §5).</summary>
public static class ToolInfo
{
    public static readonly IReadOnlyList<Tool> All = [Tool.Npx, Tool.Node, Tool.Uvx, Tool.Uv];

    /// <summary>The basename as typed in a config (<c>npx</c>, never <c>npx.cmd</c>).</summary>
    public static string Name(Tool tool) => tool switch
    {
        Tool.Npx => "npx",
        Tool.Node => "node",
        Tool.Uvx => "uvx",
        Tool.Uv => "uv",
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    public static ToolFamily Family(Tool tool) => tool is Tool.Npx or Tool.Node ? ToolFamily.NodeJs : ToolFamily.Uv;

    /// <summary>Case-insensitive lookup by basename.</summary>
    public static Tool? Parse(string name)
    {
        foreach (var tool in All)
        {
            if (Name(tool).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return tool;
            }
        }
        return null;
    }

    public static string LinkTitle(ToolFamily family) => family == ToolFamily.NodeJs ? "Install Node.js" : "Install uv";

    public static string LinkUrl(ToolFamily family) =>
        family == ToolFamily.NodeJs ? "https://nodejs.org/en/download" : "https://docs.astral.sh/uv/getting-started/installation/";

    /// <summary>The Windows install command (the Mac shows Homebrew's — spec §6 D3).</summary>
    public static string InstallCommand(ToolFamily family) =>
        family == ToolFamily.NodeJs ? "winget install OpenJS.NodeJS.LTS" : "winget install astral-sh.uv";
}
