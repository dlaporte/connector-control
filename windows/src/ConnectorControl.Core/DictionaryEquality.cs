namespace ConnectorControl.Core;

/// <summary>Structural equality for string-keyed dictionaries (Swift Dictionary semantics).</summary>
internal static class DictionaryEquality
{
    public static bool Equal<TValue>(IReadOnlyDictionary<string, TValue> a, IReadOnlyDictionary<string, TValue> b)
        where TValue : notnull
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || !value.Equals(other))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The same, for values whose own equality is not <c>Equals</c> — a nested dictionary or a
    /// set, where <c>Equals</c> would compare references.
    /// </summary>
    public static bool Equal<TValue>(
        IReadOnlyDictionary<string, TValue> a,
        IReadOnlyDictionary<string, TValue> b,
        Func<TValue, TValue, bool> valuesEqual)
        where TValue : notnull
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || !valuesEqual(value, other))
            {
                return false;
            }
        }
        return true;
    }

    public static int Hash<TValue>(IReadOnlyDictionary<string, TValue> d)
        where TValue : notnull
    {
        var hash = new HashCode();
        foreach (var (key, value) in d.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value);
        }
        return hash.ToHashCode();
    }

    /// <summary>The <see cref="Hash{TValue}(IReadOnlyDictionary{string, TValue})"/> that goes with the delegate-taking <c>Equal</c>.</summary>
    public static int Hash<TValue>(IReadOnlyDictionary<string, TValue> d, Func<TValue, int> valueHash)
        where TValue : notnull
    {
        var hash = new HashCode();
        foreach (var (key, value) in d.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(valueHash(value));
        }
        return hash.ToHashCode();
    }
}
