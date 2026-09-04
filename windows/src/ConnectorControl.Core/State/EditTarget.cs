namespace ConnectorControl.Core.State;

/// <summary>Catalog §3.1: what an editor window edits. Existing connectors use id == name (one window each); new ones a fresh GUID.</summary>
public sealed record EditTarget(string Id, string Name, McpEntry Entry, bool IsNew, bool ForcesRemote = false)
{
    public static EditTarget Existing(string name, McpEntry entry) => new(name, name, entry, IsNew: false);

    public static EditTarget New(JsonValue template) => new(Guid.NewGuid().ToString(), "", new McpEntry(template), IsNew: true);

    /// <summary>Add-Remote flow: the template has an empty URL that Detect() can't classify, so the remote form is forced explicitly.</summary>
    public static EditTarget NewRemote(RemoteLaunchStyle style) =>
        new(Guid.NewGuid().ToString(), "", new McpEntry(RemotePattern.Make("", style)), IsNew: true, ForcesRemote: true);

    /// <summary>Catalog §3.12: fixed at open time.</summary>
    public string WindowTitle => IsNew ? "Add Connector" : $"Edit “{Name}”";
}
