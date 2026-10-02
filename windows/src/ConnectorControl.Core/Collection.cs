namespace ConnectorControl.Core;

/// <summary>A full, independent snapshot of connectors: its own configs + enabled flags.</summary>
public sealed class Collection : IEquatable<Collection>
{
    public Dictionary<string, McpEntry> Mcps { get; }

    public Collection()
    {
        Mcps = new Dictionary<string, McpEntry>(StringComparer.Ordinal);
    }

    public Collection(IEnumerable<KeyValuePair<string, McpEntry>> mcps)
    {
        Mcps = new Dictionary<string, McpEntry>(mcps, StringComparer.Ordinal);
    }

    public Collection Clone() => new(Mcps);

    public bool Equals(Collection? other) => other is not null && DictionaryEquality.Equal(Mcps, other.Mcps);

    public override bool Equals(object? obj) => Equals(obj as Collection);

    public override int GetHashCode() => DictionaryEquality.Hash(Mcps);
}
