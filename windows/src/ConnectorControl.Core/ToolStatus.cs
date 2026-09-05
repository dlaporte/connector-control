namespace ConnectorControl.Core;

/// <summary>
/// Where a probe found a tool, if anywhere. Windows has two states (spec §6 D1): a GUI app and
/// a console see the same user PATH, so there is no "only the shell can see it" here.
/// </summary>
public sealed record ToolStatus(string? Path, string? Version)
{
    public static readonly ToolStatus NotFound = new(null, null);

    public bool Found => Path is not null;
}
