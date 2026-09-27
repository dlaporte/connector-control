namespace ConnectorControl.Core;

/// <summary>
/// Warnings for the publish preview, never edits: the exporter knows env values and the remote
/// auth fields are secrets, but a token typed into an argument is indistinguishable from data.
/// These patterns catch the common shapes; a placeholder marker is by definition not a secret.
/// </summary>
public static class CredentialHeuristics
{
    private static readonly string[] Prefixes = ["sk-", "ghp_", "xox", "Bearer "];

    public static bool LooksLikeCredential(string value)
    {
        if (Placeholder.ContainsMarker(value))
        {
            return false;
        }
        if (Prefixes.Any(p => value.StartsWith(p, StringComparison.Ordinal)))
        {
            return true;
        }
        if (value.Length < 32 || value.Contains(' ') || value.Contains('/'))
        {
            return false;
        }
        // By general category: a letter is L*, a digit is a decimal digit (Nd), as the Mac reads them.
        return value.Any(char.IsLetter) && value.Any(char.IsDigit);
    }

    /// <summary>
    /// Whether a name says what it names is a secret, whatever its case: a flag's (<c>--api-key</c>), a
    /// header's (<c>Authorization</c>, <c>X-Api-Key</c>) or a URL parameter's (<c>access_token</c>). The
    /// Collections window's target column leaves out whatever follows a flag named so, and the publish
    /// preview flags a header or a URL parameter named so.
    /// </summary>
    public static bool NamesASecret(string name)
    {
        var lowered = name.ToLowerInvariant();
        return SecretNames.Any(lowered.Contains);
    }

    private static readonly string[] SecretNames = ["token", "key", "secret", "pass", "pwd", "pw", "auth", "credential", "bearer"];

    /// <summary>
    /// Whether <paramref name="value"/> takes its secret from somewhere else rather than holding it: one
    /// or more <c>${NAME}</c> or <c>$NAME</c> references — an environment variable, a
    /// <c>${CC_NEEDS:NAME}</c> placeholder — with, beside them, at most one word of letters, an
    /// authentication scheme such as <c>Bearer</c>. Walked one UTF-16 unit at a time, as the Mac walks
    /// Unicode scalars.
    /// </summary>
    public static bool IsReference(string value)
    {
        var rest = new System.Text.StringBuilder();
        var references = 0;
        var index = 0;
        while (index < value.Length)
        {
            var close = index + 1 < value.Length && value[index] == '$' && value[index + 1] == '{'
                ? value.IndexOf('}', index + 2)
                : -1;
            if (close > index + 2 && value[(index + 2)..close].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or ':'))
            {
                references++;
                index = close + 1;
            }
            else if (value[index] == '$' && index + 1 < value.Length && (char.IsAsciiLetter(value[index + 1]) || value[index + 1] == '_'))
            {
                // $NAME, as a shell writes a variable: a letter or _, then letters, digits and _.
                var end = index + 2;
                while (end < value.Length && (char.IsAsciiLetterOrDigit(value[end]) || value[end] == '_'))
                {
                    end++;
                }
                references++;
                index = end;
            }
            else
            {
                rest.Append(value[index]);
                index++;
            }
        }
        var words = rest.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return references > 0 && words.Length <= 1 && words.All(word => word.All(char.IsAsciiLetter));
    }

    /// <summary>
    /// A random token rather than a name: with its slashes removed, at least 20 characters, with
    /// an upper-case letter, a lower-case letter and a digit, and none of <c>.</c>, <c>-</c> or
    /// <c>_</c>. An AWS secret key has this shape and a <c>/</c>, which <see cref="LooksLikeCredential"/>
    /// refuses to consider; a real path or package name nearly always has a dot, a hyphen or no digits.
    /// The Collections window's target column leaves such a word out, and the publish preview flags
    /// one in a URL.
    /// </summary>
    public static bool LooksLikeRandomToken(string text)
    {
        var body = text.Where(c => c != '/' && c != '\\').ToList();
        return body.Count >= 20
            && body.Any(char.IsAsciiLetterUpper) && body.Any(char.IsAsciiLetterLower)
            && body.Any(char.IsAsciiDigit) && !body.Any(c => c is '.' or '-' or '_');
    }
}
