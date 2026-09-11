namespace ConnectorControl.Core.State;

/// <summary>
/// What a regenerated Claude config now runs that it did not before, by
/// connector name (Swift <c>ServerDelta</c>). An mcpServers
/// entry is a command Claude executes, so a notification about an adopted
/// synced change names the entries rather than saying only that "the list changed".
/// </summary>
public sealed record ServerDelta(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed)
{
    public static ServerDelta Between(IReadOnlyDictionary<string, JsonValue> before, IReadOnlyDictionary<string, JsonValue> after)
    {
        var added = after.Keys.Where(k => !before.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var removed = before.Keys.Where(k => !after.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var changed = after.Keys.Where(k => before.TryGetValue(k, out var was) && !was.Equals(after[k])).Order(StringComparer.Ordinal).ToList();
        return new ServerDelta(added, removed, changed);
    }

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;

    /// <summary>"adds a, b; removes c; changes d" — at most <paramref name="limit"/> names per part, then "and N more".</summary>
    public string Summary(int limit = 4)
    {
        var parts = new List<string>();
        if (Added.Count > 0)
        {
            parts.Add("adds " + List(Added, limit));
        }
        if (Removed.Count > 0)
        {
            parts.Add("removes " + List(Removed, limit));
        }
        if (Changed.Count > 0)
        {
            parts.Add("changes " + List(Changed, limit));
        }
        return string.Join("; ", parts);
    }

    private static string List(IReadOnlyList<string> names, int limit) =>
        names.Count <= limit
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(limit)) + $" and {names.Count - limit} more";
}
