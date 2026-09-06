namespace ConnectorControl.Core.State;

/// <summary>One Settings ▸ Claude ▸ Tools row (spec §3.5): name, status text, and the install note when there is something to do.</summary>
public sealed record ToolRow(string Name, string StatusText, bool IsProblem, ToolNote? Note)
{
    public bool HasNote => Note is not null;

    public static ToolRow For(Tool tool, ToolStatus? status) =>
        new(ToolInfo.Name(tool), ToolNote.StatusText(status), status is { Found: false }, ToolNote.For(tool, status));
}
