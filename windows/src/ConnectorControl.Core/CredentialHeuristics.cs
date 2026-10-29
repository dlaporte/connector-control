namespace ConnectorControl.Core;

/// <summary>
/// Warnings for the publish preview, never edits: the exporter knows env values and the remote
/// auth fields are secrets, but a token typed into an argument is indistinguishable from data.
/// These patterns catch the common shapes; a placeholder marker is by definition not a secret.
/// Prefixes, names, lengths and characters are read one UTF-16 unit at a time on both platforms,
/// so the same text reads the same: a combining mark after a prefix or a name does not hide it,
/// and a character beyond the Basic Plane is two units, neither a letter nor a digit.
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
        // Platform idiom: ToLowerInvariant lowers unit by unit here, one to one, and leaves İ (U+0130),
        // whose lower case is two scalars, as it is; the Mac mirror lowers each unit only where its
        // lower case is one unit, to match it.
        var lowered = name.ToLowerInvariant();
        return SecretNames.Any(lowered.Contains);
    }

    private static readonly string[] SecretNames = ["token", "key", "secret", "pass", "pwd", "pw", "auth", "credential", "bearer"];

    /// <summary>Whether <paramref name="word"/> is an authentication scheme that comes before a credential in a header's
    /// value — <c>Bearer</c>, <c>Basic</c> or <c>Token</c> — whatever its case.</summary>
    public static bool IsSchemeWord(string word) =>
        SchemeWords.Contains(word.ToLowerInvariant(), StringComparer.Ordinal);

    private static readonly string[] SchemeWords = ["bearer", "basic", "token"];

    /// <summary>
    /// Whether an environment variable's value names code to run: a program (<c>GIT_ASKPASS</c>,
    /// <c>GIT_SSH_COMMAND</c>, any <c>*_ASKPASS</c>), git configuration that can name one (<c>GIT_CONFIG_*</c>), a
    /// library or module loaded first (<c>LD_PRELOAD</c>, <c>NODE_OPTIONS</c>, <c>JAVA_TOOL_OPTIONS</c>) or a
    /// script a shell or interpreter runs first (<c>BASH_ENV</c>, <c>PYTHONSTARTUP</c>). Whatever its case,
    /// since Windows reads names so. The confirmation dialog shows such a value in full even when
    /// its name reads as a secret's: it is code that would run, which the person allowing must see.
    /// </summary>
    public static bool NamesAProgramToRun(string name)
    {
        // Platform idiom: ToLowerInvariant lowers unit by unit here, as the Mac mirror lowers to match it.
        var lowered = name.ToLowerInvariant();
        return ProgramNames.Contains(lowered, StringComparer.Ordinal)
            || lowered.StartsWith("git_config_", StringComparison.Ordinal)
            || lowered.EndsWith("_askpass", StringComparison.Ordinal);
    }

    private static readonly string[] ProgramNames =
    [
        "ssh_askpass_require", "git_ssh", "git_ssh_command", "git_exec_path", "node_options", "ld_preload",
        "dyld_insert_libraries", "pythonstartup", "bash_env", "env", "perl5opt", "rubyopt", "java_tool_options",
        "_java_options",
    ];

    /// <summary>
    /// Whether <paramref name="value"/> takes its secret from somewhere else rather than holding it: one
    /// or more <c>${NAME}</c> or <c>$NAME</c> references — an environment variable, a
    /// <c>${CC_NEEDS:NAME}</c> placeholder — with, beside them, at most one word of letters, an
    /// authentication scheme such as <c>Bearer</c>. A <c>$NAME</c> without braces counts only as
    /// <see cref="ShellVariableEnd"/> reads one. Walked one UTF-16 unit at a time, as the Mac walks
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
            else if (value[index] == '$' && ShellVariableEnd(value, index + 1) is { } end)
            {
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
    /// Where the <c>$NAME</c> whose name starts at <paramref name="start"/> ends, or null where the text
    /// there is no such reference. The name is the whole run of letters, digits and <c>_</c>, and counts
    /// only where it is written as a shell's environment variables are, <c>^[A-Z_][A-Z0-9_]*$</c>, and
    /// does not itself look like a credential or a random token: <c>$API_TOKEN</c> refers to a secret,
    /// while <c>$uperSecret99…</c> or a <c>$</c> before a random token is one.
    /// </summary>
    private static int? ShellVariableEnd(string value, int start)
    {
        var end = start;
        while (end < value.Length && (char.IsAsciiLetterOrDigit(value[end]) || value[end] == '_'))
        {
            end++;
        }
        var name = value[start..end];
        if (name.Length == 0 || !(char.IsAsciiLetterUpper(name[0]) || name[0] == '_')
            || !name.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            return null;
        }
        return LooksLikeCredential(name) || LooksLikeRandomToken(name) ? null : end;
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
