using System.Text.Json;

namespace ConnectorControl.Core;

/// <summary>
/// Turns text a user pastes into the JSON editor into a (name?, config) pair,
/// tolerating a plain config object, a full mcpServers wrapper, a single-entry
/// name wrapper, a bare "NAME": {…} fragment, and that fragment with a stray
/// trailing brace; curly quotes are normalized as a fallback.
/// </summary>
public static class PasteRecovery
{
    /// <summary>Keys that mark an object as a connector CONFIG rather than a {name: config} wrapper.</summary>
    private static readonly HashSet<string> ConfigKeys =
        new(["command", "args", "env", "url", "type", "headers"], StringComparer.Ordinal);

    public static PasteResult? Recover(string text)
    {
        var value = ParseTolerant(text);
        return value is null ? null : Unwrap(value);
    }

    private static JsonValue? ParseTolerant(string text)
    {
        var t = text.Trim();
        if (t.Length == 0)
        {
            return null;
        }
        foreach (var candidate in Candidates(t))
        {
            try
            {
                return JsonValue.Parse(candidate);
            }
            catch (JsonException)
            {
                // try the next candidate
            }
        }
        return null;
    }

    private static List<string> Candidates(string t)
    {
        var candidates = new List<string> { t };
        // A leading quote means a bare `"key": value` fragment. Wrap it, and also
        // try after trimming the stray trailing brace(s) left over from copying
        // one entry out of a block.
        if (t.StartsWith('"'))
        {
            candidates.Add("{" + t + "}");
            var balanced = TrimStrayTrailingBraces(t);
            if (balanced != t)
            {
                candidates.Add("{" + balanced + "}");
            }
        }
        // Curled quotes (Slack/Notion/Notes) break JSON — retry with them normalized.
        var normalized = candidates.Select(NormalizeQuotes).Where(n => !candidates.Contains(n, StringComparer.Ordinal)).ToList();
        candidates.AddRange(normalized);
        return candidates;
    }

    private static string NormalizeQuotes(string s) =>
        s.Replace('“', '"').Replace('”', '"').Replace('‘', '\'').Replace('’', '\'');

    /// <summary>
    /// Drops closing braces at the end of a fragment that exceed its own opening
    /// count (string/escape-aware). Leaves a fragment that, wrapped in one { }, balances.
    /// </summary>
    private static string TrimStrayTrailingBraces(string s)
    {
        int depth = 0;
        bool inString = false, escaped = false;
        foreach (char ch in s)
        {
            if (escaped) { escaped = false; continue; }
            if (inString)
            {
                if (ch == '\\') { escaped = true; }
                else if (ch == '"') { inString = false; }
                continue;
            }
            switch (ch)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}': depth--; break;
            }
        }
        if (depth >= 0)
        {
            return s;
        }
        var chars = new List<char>(s);
        while (depth < 0)
        {
            while (chars.Count > 0 && char.IsWhiteSpace(chars[^1]))
            {
                chars.RemoveAt(chars.Count - 1);
            }
            if (chars.Count == 0 || chars[^1] != '}')
            {
                break;
            }
            chars.RemoveAt(chars.Count - 1);
            depth++;
        }
        return new string(chars.ToArray());
    }

    private static PasteResult Unwrap(JsonValue value)
    {
        if (value.Kind == JsonKind.Object && value.ObjectProperties.Count == 1)
        {
            var (key, inner) = value.ObjectProperties.First();
            // {"mcpServers": {"NAME": {…}}} — single entry.
            if (key == "mcpServers" && inner.Kind == JsonKind.Object && inner.ObjectProperties.Count == 1)
            {
                var (name, config) = inner.ObjectProperties.First();
                return new PasteResult(name, config);
            }
            // {"NAME": {config}} where NAME is neither a config field nor the wrapper key.
            if (inner.Kind == JsonKind.Object && key != "mcpServers" && !ConfigKeys.Contains(key))
            {
                return new PasteResult(key, inner);
            }
        }
        return new PasteResult(null, value);
    }
}
