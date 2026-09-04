namespace ConnectorControl.Core.Services;

public enum ClaudeInstallKind
{
    /// <summary>MSIX package (current Claude Desktop builds; launched by app identity).</summary>
    Msix,
    /// <summary>Squirrel-style install under %LOCALAPPDATA%\AnthropicClaude (older builds; launched by exe).</summary>
    Legacy,
    NotFound,
}
