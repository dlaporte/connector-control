namespace ConnectorControl.Core;

/// <summary>
/// The marker a shared collection leaves where a secret or a machine-specific path was:
/// <c>${CC_NEEDS:&lt;name&gt;}</c>. A string that contains one is "unfilled"; filling replaces the
/// whole string. <c>${COLLECTION_DIR}</c> is the one other token, expanded per machine to the
/// folder the collection document lives in.
/// </summary>
public static class Placeholder
{
    private const string MarkerPrefix = "${CC_NEEDS:";
    private const string MarkerSuffix = "}";
    public const string DirectoryToken = "${COLLECTION_DIR}";

    public static string Marker(string name) => MarkerPrefix + name + MarkerSuffix;

    public static bool IsValidName(string name) =>
        name.Length > 0 && name.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_');

    /// <summary>Marker names in order of first appearance; a malformed marker (bad name, no closing brace) is plain text.</summary>
    public static IReadOnlyList<string> NamesIn(string text)
    {
        var names = new List<string>();
        var rest = text.AsSpan();
        while (true)
        {
            var start = rest.IndexOf(MarkerPrefix, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }
            var afterPrefix = rest[(start + MarkerPrefix.Length)..];
            var close = afterPrefix.IndexOf('}');
            if (close < 0)
            {
                break;
            }
            var name = afterPrefix[..close].ToString();
            if (IsValidName(name) && !names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
            rest = afterPrefix[(close + 1)..];
        }
        return names;
    }

    public static bool ContainsMarker(string text) => NamesIn(text).Count > 0;

    public static IReadOnlyList<(JsonPointer Pointer, IReadOnlyList<string> Names)> MarkersIn(JsonValue config) =>
        config.StringLeaves()
            .Select(leaf => (leaf.Pointer, Names: NamesIn(leaf.Value)))
            .Where(m => m.Names.Count > 0)
            .ToList();

    public static bool UsesDirectoryToken(JsonValue config) =>
        config.StringLeaves().Any(leaf => leaf.Value.Contains(DirectoryToken, StringComparison.Ordinal));

    public static JsonValue ExpandDirectoryToken(JsonValue config, string directory)
    {
        var result = config;
        foreach (var leaf in config.StringLeaves())
        {
            if (!leaf.Value.Contains(DirectoryToken, StringComparison.Ordinal))
            {
                continue;
            }
            var expanded = leaf.Value.Replace(DirectoryToken, directory, StringComparison.Ordinal);
            result = result.Replacing(leaf.Pointer, JsonValue.String(expanded)) ?? result;
        }
        return result;
    }
}
