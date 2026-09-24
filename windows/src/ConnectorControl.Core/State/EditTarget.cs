namespace ConnectorControl.Core.State;

/// <summary>
/// What an editor window edits. An existing connector's id is its collection, <c>\u001F</c>, then
/// its name, so there is one window per connector per collection; a new one's is a fresh GUID.
///
/// Mirror: Sources/ConnectorControlState/EditTarget.swift
/// </summary>
/// <param name="Collection">
/// Which collection this window edits. Always named: a window that followed whichever collection
/// is active would move its read-only state, its header and its save target whenever the active
/// collection changed under it. It sits before <paramref name="ForcesRemote"/>, unlike the
/// Mac's initializer, because C# puts a required parameter ahead of an optional one.
/// </param>
public sealed record EditTarget(string Id, string Name, McpEntry Entry, bool IsNew, string Collection, bool ForcesRemote = false)
{
    public static EditTarget Existing(string name, McpEntry entry, string collection) =>
        new(Identifier(name, collection), name, entry, IsNew: false, Collection: collection);

    /// <summary>Add-Remote flow: the template has an empty URL that Detect() can't classify, so the remote form is forced explicitly.</summary>
    public static EditTarget NewRemote(RemoteLaunchStyle style, string collection) =>
        new(Guid.NewGuid().ToString(), "", new McpEntry(RemotePattern.Make("", style)), IsNew: true, ForcesRemote: true,
            Collection: collection);

    /// <summary>
    /// The same connector in two collections is two windows, so the collection is part of the
    /// identity. The separator is a control character rather than a slash or a dot, neither of
    /// which a collection or a connector name is stopped from containing.
    /// </summary>
    internal static string Identifier(string name, string collection) => collection + '\u001F' + name;

    public const string AddTitle = "Add Connector";

    public static string EditTitle(string name) => $"Edit “{name}”";

    /// <summary>Fixed at open time.</summary>
    public string WindowTitle => IsNew ? AddTitle : EditTitle(Name);

    /// <summary>
    /// One window per connector per collection: two targets are the same window when their ids
    /// match, whatever the entry held when each was made. WindowRegistry keys its editors by the
    /// id, so a connector switched on or off since its editor opened still brings that editor
    /// forward rather than opening a second. The Mac's editor window group finds its window by
    /// this same equality.
    /// </summary>
    public bool Equals(EditTarget? other) => other is not null && string.Equals(Id, other.Id, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);
}
