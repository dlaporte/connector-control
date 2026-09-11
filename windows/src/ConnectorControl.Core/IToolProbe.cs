namespace ConnectorControl.Core;

/// <summary>
/// Resolves tools on the PATH. Implementations never throw and may spawn
/// processes for up to the version timeout per tool — call off the UI thread.
/// </summary>
public interface IToolProbe
{
    IReadOnlyDictionary<Tool, ToolStatus> Probe(IReadOnlyList<Tool> tools);
}
