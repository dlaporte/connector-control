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

    /// <summary>
    /// Every marker name the config still asks for, ordered by first appearance and
    /// de-duplicated across leaves, so a sentence built from them is stable between two reads of
    /// the same config and the same wherever it is built.
    /// </summary>
    public static IReadOnlyList<string> UnfilledNamesIn(JsonValue config) =>
        MarkersIn(config).SelectMany(m => m.Names).Distinct(StringComparer.Ordinal).ToList();

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

    /// <summary>
    /// <paramref name="config"/> with <paramref name="directory"/> written back as
    /// <c>${COLLECTION_DIR}</c> wherever it stands as a folder of its own: not inside a longer
    /// name, and followed by a separator or the end of the string. The way back from
    /// <see cref="ExpandDirectoryToken"/> for a config that comes out of Claude's file, which holds
    /// this machine's folder where the store holds the token.
    /// </summary>
    public static JsonValue CollapseDirectory(JsonValue config, string directory)
    {
        if (directory.Length == 0)
        {
            return config;
        }
        var result = config;
        foreach (var leaf in config.StringLeaves())
        {
            if (leaf.Value.Contains(directory, StringComparison.Ordinal))
            {
                result = result.Replacing(leaf.Pointer, JsonValue.String(Collapse(leaf.Value, directory))) ?? result;
            }
        }
        return result;
    }

    private static string Collapse(string text, string directory)
    {
        var output = new System.Text.StringBuilder();
        var start = 0;
        int found;
        while ((found = text.IndexOf(directory, start, StringComparison.Ordinal)) >= 0)
        {
            var end = found + directory.Length;
            var standsAlone = (found == 0 || !IsPathCharacter(text[found - 1]))
                && (end == text.Length || text[end] == '/' || text[end] == '\\');
            output.Append(text, start, found - start);
            output.Append(standsAlone ? DirectoryToken : directory);
            start = end;
        }
        return output.Append(text, start, text.Length - start).ToString();
    }

    /// <summary>A character that continues a path segment, so a folder found right after one is part of a longer name.</summary>
    private static bool IsPathCharacter(char c) => char.IsLetterOrDigit(c) || "/\\._-~".Contains(c);
}
