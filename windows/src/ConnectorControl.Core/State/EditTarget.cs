namespace ConnectorControl.Core.State;

/// <summary>What an editor window edits. Existing connectors use id == name (one window each); new ones a fresh GUID.</summary>
/// <param name="Collection">
/// Which collection this window edits; null is "whatever is active", which is what every editor
/// opened from the flyout has always meant.
/// </param>
public sealed record EditTarget(string Id, string Name, McpEntry Entry, bool IsNew, bool ForcesRemote = false, string? Collection = null)
{
    public static EditTarget Existing(string name, McpEntry entry, string? collection = null) =>
        new(Identifier(name, collection), name, entry, IsNew: false, Collection: collection);

    public static EditTarget New(JsonValue template) => new(Guid.NewGuid().ToString(), "", new McpEntry(template), IsNew: true);

    /// <summary>Add-Remote flow: the template has an empty URL that Detect() can't classify, so the remote form is forced explicitly.</summary>
    public static EditTarget NewRemote(RemoteLaunchStyle style, string? collection = null) =>
        new(Guid.NewGuid().ToString(), "", new McpEntry(RemotePattern.Make("", style)), IsNew: true, ForcesRemote: true,
            Collection: collection);

    /// <summary>
    /// The same connector in two collections is two windows, so the collection is part of the
    /// identity. A null collection keeps the bare name the id has always been. The separator is a
    /// control character rather than a slash or a dot, neither of which a collection or a
    /// connector name is stopped from containing.
    /// </summary>
    internal static string Identifier(string name, string? collection) =>
        collection is null ? name : collection + '\u001F' + name;

    public const string AddTitle = "Add Connector";

    public static string EditTitle(string name) => $"Edit “{name}”";

    /// <summary>Fixed at open time.</summary>
    public string WindowTitle => IsNew ? AddTitle : EditTitle(Name);
}
