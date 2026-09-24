using System.Collections.Immutable;
using System.Globalization;

namespace ConnectorControl.Core;

/// <summary>
/// A path into a JsonValue, written and parsed in RFC 6901 form ("/args/1", "~1" for a slash,
/// "~0" for a tilde). Segments are kept as strings; an array decides at lookup time whether a
/// segment is an index, so an object key that happens to be digits still resolves.
/// </summary>
public sealed record JsonPointer(ImmutableArray<string> Segments)
{
    public JsonPointer(IEnumerable<string> segments) : this(segments.ToImmutableArray()) { }

    public static JsonPointer? Parse(string text)
    {
        if (text.Length == 0)
        {
            return new JsonPointer(ImmutableArray<string>.Empty);
        }
        if (text[0] != '/')
        {
            return null;
        }
        var segments = text[1..].Split('/').Select(s => s.Replace("~1", "/").Replace("~0", "~"));
        return new JsonPointer(segments);
    }

    public override string ToString() =>
        string.Concat(Segments.Select(s => "/" + s.Replace("~", "~0").Replace("/", "~1")));

    public JsonPointer Appending(string segment) => new(Segments.Add(segment));

    /// <summary>
    /// A segment read as an array index the way RFC 6901 writes one: ASCII digits and nothing
    /// else, so neither a sign nor a space makes " 1" or "+1" an index — whatever the culture.
    /// </summary>
    internal static int? ArrayIndex(string segment) =>
        int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : null;

    public bool Equals(JsonPointer? other) => other is not null && Segments.SequenceEqual(other.Segments, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var s in Segments)
        {
            hash.Add(s, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}

public static class JsonPointerAccess
{
    public static JsonValue? ValueAt(this JsonValue value, JsonPointer pointer)
    {
        var current = value;
        foreach (var segment in pointer.Segments)
        {
            switch (current.Kind)
            {
                case JsonKind.Object:
                    var next = current[segment];
                    if (next is null)
                    {
                        return null;
                    }
                    current = next;
                    break;
                case JsonKind.Array:
                    if (JsonPointer.ArrayIndex(segment) is not { } index || index >= current.ArrayItems.Length)
                    {
                        return null;
                    }
                    current = current.ArrayItems[index];
                    break;
                default:
                    return null;
            }
        }
        return current;
    }

    /// <summary>A copy with the value at <paramref name="pointer"/> replaced; null when the path does not exist, so a caller never creates structure by accident.</summary>
    public static JsonValue? Replacing(this JsonValue value, JsonPointer pointer, JsonValue newValue)
    {
        if (pointer.Segments.Length == 0)
        {
            return newValue;
        }
        var first = pointer.Segments[0];
        var rest = new JsonPointer(pointer.Segments.RemoveAt(0));
        switch (value.Kind)
        {
            case JsonKind.Object:
                var child = value[first];
                var replacedChild = child?.Replacing(rest, newValue);
                return replacedChild is null ? null : value.With(first, replacedChild);
            case JsonKind.Array:
                if (JsonPointer.ArrayIndex(first) is not { } index || index >= value.ArrayItems.Length)
                {
                    return null;
                }
                var replacedItem = value.ArrayItems[index].Replacing(rest, newValue);
                return replacedItem is null ? null : JsonValue.Array(value.ArrayItems.SetItem(index, replacedItem));
            default:
                return null;
        }
    }

    /// <summary>Every string leaf with its pointer, depth first, object keys in ordinal order (which is how ObjectProperties is already sorted), so two platforms walking the same value produce the same list.</summary>
    public static IReadOnlyList<(JsonPointer Pointer, string Value)> StringLeaves(this JsonValue value)
    {
        var leaves = new List<(JsonPointer, string)>();
        Walk(value, new JsonPointer(ImmutableArray<string>.Empty), leaves);
        return leaves;
    }

    private static void Walk(JsonValue value, JsonPointer pointer, List<(JsonPointer, string)> leaves)
    {
        switch (value.Kind)
        {
            case JsonKind.String:
                leaves.Add((pointer, value.StringValue));
                break;
            case JsonKind.Array:
                for (var i = 0; i < value.ArrayItems.Length; i++)
                {
                    Walk(value.ArrayItems[i], pointer.Appending(i.ToString()), leaves);
                }
                break;
            case JsonKind.Object:
                foreach (var (key, child) in value.ObjectProperties)
                {
                    Walk(child, pointer.Appending(key), leaves);
                }
                break;
        }
    }
}
