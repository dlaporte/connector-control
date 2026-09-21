namespace ConnectorControl.Core;

/// <summary>
/// What a synced collection's source would change, and how the change lands.
///
/// Mirror: Sources/ConnectorControlCore/CollectionDiff.swift
/// </summary>
public sealed record CollectionDiff(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed)
{
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;

    /// <summary>
    /// <paramref name="rendered"/> vs <paramref name="current"/>, ignoring enabled flags; a
    /// rendered string leaf that contains a marker matches whatever string the current config has
    /// at that pointer. A connector this platform excludes never appears in
    /// <see cref="RenderedCollection.Connectors"/>, so it never shows as added.
    /// </summary>
    public static CollectionDiff Pending(RenderedCollection rendered, IReadOnlyDictionary<string, McpEntry> current)
    {
        var renderedNames = new HashSet<string>(rendered.Connectors.Keys, StringComparer.Ordinal);
        var currentNames = new HashSet<string>(current.Keys, StringComparer.Ordinal);
        var changed = new List<string>();
        foreach (var name in renderedNames.Intersect(currentNames))
        {
            if (!Matches(rendered.Connectors[name].Config, current[name].Config))
            {
                changed.Add(name);
            }
        }
        return new CollectionDiff(
            renderedNames.Except(currentNames).Order(StringComparer.Ordinal).ToList(),
            currentNames.Except(renderedNames).Order(StringComparer.Ordinal).ToList(),
            changed.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Equal after every marker-bearing leaf of <paramref name="rendered"/> is replaced by
    /// whatever string <paramref name="current"/> holds at the same pointer; a missing leaf on the
    /// current side is a change.
    /// </summary>
    internal static bool Matches(JsonValue rendered, JsonValue current)
    {
        var normalized = rendered;
        foreach (var (pointer, _) in Placeholder.MarkersIn(rendered))
        {
            if (current.ValueAt(pointer) is not { Kind: JsonKind.String } filled)
            {
                return false;
            }
            normalized = normalized.Replacing(pointer, filled) ?? normalized;
        }
        return normalized.Equals(current);
    }

    /// <summary>
    /// "adds a, b; removes c; changes d" — same shape as <c>ServerDelta.Summary</c>, kept as its
    /// own copy here since this type lives beside the document Core builds, not the state layer.
    /// </summary>
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

public sealed record CollectionApplyResult(
    IReadOnlyDictionary<string, McpEntry> Entries,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, CollectionsFile.Need>> Needs)
{
    public bool Equals(CollectionApplyResult? other) =>
        other is not null
        && DictionaryEquality.Equal(Entries, other.Entries)
        && DictionaryEquality.Equal(Needs, other.Needs, static (x, y) => DictionaryEquality.Equal(x, y));

    public override int GetHashCode() => HashCode.Combine(
        DictionaryEquality.Hash(Entries),
        DictionaryEquality.Hash(Needs, static n => DictionaryEquality.Hash(n)));
}

/// <summary>
/// The new content of a synced collection: rendered configs with the user's filled values carried
/// by marker name (found through the previous needs' pointers), enabled flags kept, added
/// connectors disabled.
/// </summary>
public static class CollectionApply
{
    public static CollectionApplyResult Apply(
        RenderedCollection rendered,
        IReadOnlyDictionary<string, McpEntry> current,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, CollectionsFile.Need>> previousNeeds)
    {
        var entries = new Dictionary<string, McpEntry>(StringComparer.Ordinal);
        var needs = new Dictionary<string, IReadOnlyDictionary<string, CollectionsFile.Need>>(StringComparer.Ordinal);
        foreach (var (name, connector) in rendered.Connectors)
        {
            var config = connector.Config;
            var existingEntry = current.GetValueOrDefault(name);
            var connectorNeeds = new Dictionary<string, CollectionsFile.Need>(StringComparer.Ordinal);
            foreach (var (needName, need) in connector.Needs)
            {
                connectorNeeds[needName] = new CollectionsFile.Need(need.Hint, need.Pointer);
                // A value the user filled in under this name stays filled, wherever the marker
                // moved to: the previous needs record where it was.
                var previousPointer = previousNeeds.GetValueOrDefault(name)?.GetValueOrDefault(needName)?.Pointer;
                if (previousPointer is not null
                    && existingEntry?.Config.ValueAt(previousPointer) is { Kind: JsonKind.String } filled
                    && !Placeholder.ContainsMarker(filled.StringValue))
                {
                    config = config.Replacing(need.Pointer, filled) ?? config;
                }
            }
            entries[name] = new McpEntry(existingEntry?.Enabled ?? false, config, existingEntry?.LastEditView ?? EditView.Form);
            if (connectorNeeds.Count > 0)
            {
                needs[name] = connectorNeeds;
            }
        }
        return new CollectionApplyResult(entries, needs);
    }
}
