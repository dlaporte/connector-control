using System.Text;

namespace ConnectorControl.Core;

/// <summary>A full, independent snapshot of connectors: its own configs + enabled flags.</summary>
public sealed class Collection : IEquatable<Collection>
{
    /// <summary>
    /// How connector names are told apart: two spellings Unicode counts as the same text, such as an é
    /// composed and decomposed, are one name. Platform idiom: Swift's dictionary keys work this way by
    /// themselves, and Claude's config refuses a file naming a connector in both
    /// (<see cref="ClaudeConfigIO"/>), so a collection never holds the two at once. A config's own
    /// text is still compared ordinally: a change only in how a character is composed is a change.
    /// </summary>
    public static readonly IEqualityComparer<string> NameComparer = new CanonicalNameComparer();

    public Dictionary<string, McpEntry> Mcps { get; }

    public Collection()
    {
        Mcps = new Dictionary<string, McpEntry>(NameComparer);
    }

    /// <summary>
    /// Where <paramref name="mcps"/> names one connector in two spellings, which only a store written by
    /// hand does, the first is kept, as a Swift dictionary keeps one.
    /// </summary>
    public Collection(IEnumerable<KeyValuePair<string, McpEntry>> mcps)
    {
        Mcps = new Dictionary<string, McpEntry>(NameComparer);
        foreach (var (name, entry) in mcps)
        {
            Mcps.TryAdd(name, entry);
        }
    }

    /// <summary>
    /// The name <paramref name="name"/> is held under, in the spelling first stored, or null when no
    /// connector has it. Whatever is keyed by a connector's name beside the store (its path marks, its
    /// review) takes this spelling, so a name typed another way never leaves a second key behind.
    /// </summary>
    public string? StoredName(string name) =>
        Mcps.ContainsKey(name) ? Mcps.Keys.First(k => NameComparer.Equals(k, name)) : null;

    public Collection Clone() => new(Mcps);

    public bool Equals(Collection? other) => other is not null && DictionaryEquality.Equal(Mcps, other.Mcps);

    public override bool Equals(object? obj) => Equals(obj as Collection);

    // Hashed by the names composed, so two collections equal under NameComparer hash alike.
    public override int GetHashCode() =>
        DictionaryEquality.Hash(Mcps.ToDictionary(p => Composed(p.Key), p => p.Value, StringComparer.Ordinal));

    /// <summary><paramref name="name"/> composed; text that is not valid UTF-16, which has no composed form, as it stands.</summary>
    private static string Composed(string name)
    {
        try
        {
            return name.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return name;
        }
    }

    private sealed class CanonicalNameComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) =>
            x is null || y is null
                ? x is null && y is null
                : string.Equals(x, y, StringComparison.Ordinal)
                  || string.Equals(Composed(x), Composed(y), StringComparison.Ordinal);

        public int GetHashCode(string name) => StringComparer.Ordinal.GetHashCode(Composed(name));
    }
}
