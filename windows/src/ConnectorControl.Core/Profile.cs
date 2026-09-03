namespace ConnectorControl.Core;

/// <summary>A full, independent snapshot of connectors: its own configs + enabled flags.</summary>
public sealed class Profile : IEquatable<Profile>
{
    public Dictionary<string, McpEntry> Mcps { get; }

    public Profile()
    {
        Mcps = new Dictionary<string, McpEntry>(StringComparer.Ordinal);
    }

    public Profile(IEnumerable<KeyValuePair<string, McpEntry>> mcps)
    {
        Mcps = new Dictionary<string, McpEntry>(mcps, StringComparer.Ordinal);
    }

    public Profile Clone() => new(Mcps);

    public bool Equals(Profile? other) => other is not null && DictionaryEquality.Equal(Mcps, other.Mcps);

    public override bool Equals(object? obj) => Equals(obj as Profile);

    public override int GetHashCode() => DictionaryEquality.Hash(Mcps);
}
