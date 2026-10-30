using System.Text;
using System.Text.Json;

namespace ConnectorControl.Core.State;

/// <summary>
/// What a connector looks like to anyone but its owner: the command line and URL in full, with
/// every secret value replaced. Environment and header values are never shown, only whether they
/// are set, and nor is a value under a key named for a secret; an argument that reads as a
/// credential, the value after a flag named for a secret, a <c>NAME=value</c> word's value when the
/// name is a secret's, a header's value, a URL's password and query and fragment values, and a
/// URL's user name or path segment that reads as a credential, read "•••". Placeholders -
/// <c>${CC_NEEDS:…}</c> and <c>${COLLECTION_DIR}</c> - name what is missing rather than what is secret, and
/// show as written when they are the whole value. Every rule is plain string work, with no URL
/// parser, so both platforms mask the same text the same way. Structured output is masked or, for
/// the command-line tool's <c>show --reveal</c> (never Claude's), revealed; the line renderers take an
/// <see cref="Exposure"/>, which adds the confirmation dialog's, where an environment value shows unless its
/// variable is named for a secret and names no program to run.
/// The Mac mirror is main-actor only; C# has no isolation to declare.
///
/// Mirror: Sources/ConnectorControlState/Control/ControlMasking.swift
/// </summary>
public static class ControlMasking
{
    /// <summary>How much a line renderer shows. <c>Masked</c>: what Claude and the command-line tool see.
    /// <c>Dialog</c>: the confirmation dialogs, whose text never goes back to Claude - the command,
    /// arguments, URL and environment values in full, with a variable named for a secret (unless it
    /// names a program to run), header values, the OAuth client secret and every value under a key
    /// named for a secret still masked.
    /// <c>Revealed</c>: everything as written, for the command-line tool's <c>show --reveal</c>.</summary>
    public enum Exposure { Masked, Dialog, Revealed }

    public const string Mask = "•••";
    /// <summary>A secret's state in structured output.</summary>
    public const string SetState = "set";
    public const string NeedsValueState = "needs value";
    /// <summary>A set secret on a code line; an unset one reads <see cref="EditorModel.NeedsValue"/>.</summary>
    public const string SetText = "••• (set)";
    public const string TypeLabel = "type";
    public const string UrlLabel = "url";
    public const string HeadersLabel = "headers";
    public const string OauthLabel = "oauth";
    public const string ScopesLabel = "scopes";
    public const string CommandLabel = "command";
    public const string ArgsLabel = "args";
    public const string EnvLabel = "env";
    public const string OtherLabel = "other";
    public const string NameLabel = "name";
    public const string RemovedPrefix = "  − ";
    public const string AddedPrefix = "  + ";

    public static string RemoteBridgeType(string package) => $"remote, through {package}";

    public static string OauthClientText(string clientId, string secret) => $"client {clientId}, secret {secret}";

    public static string Assignment(string name, string value) => $"{name} = {value}";

    /// <summary>One value on a line as <paramref name="exposure"/> shows it: in the dialog, quoted as an argument is when it is
    /// not plain (<see cref="ControlText.Word"/>), so a newline and the two characters \n read apart and a run
    /// of spaces cannot pass for a row of its own; elsewhere as it is.</summary>
    internal static string Single(string text, Exposure exposure) => exposure == Exposure.Dialog ? ControlText.Word(text) : text;

    // MARK: Values

    /// <summary>A value that is wholly a reference - a placeholder, <c>${COLLECTION_DIR}</c>, or a <c>${NAME}</c> /
    /// <c>$NAME</c> - takes its secret from elsewhere and holds none itself. One with a placeholder
    /// inside other text can still hold a secret beside it, so it is masked like any other.</summary>
    internal static bool RefersElsewhere(string value) =>
        CredentialHeuristics.IsReference(value);

    /// <summary>Whether <c>text</c> reads as a credential or a random token, as written or with every <c>${…}</c>
    /// taken out: the heuristics take a placeholder for a value still to come, so
    /// <c>sk-live-…${CC_NEEDS:x}</c> would otherwise pass.</summary>
    internal static bool LooksSecret(string text) =>
        new[] { text, WithoutReferences(text) }.Any(candidate =>
            CredentialHeuristics.LooksLikeCredential(candidate) || CredentialHeuristics.LooksLikeRandomToken(candidate));

    /// <summary>Whether a piece of a URL is a secret: it reads as one and is not wholly a reference.</summary>
    internal static bool SecretShaped(string text) =>
        !RefersElsewhere(text) && LooksSecret(text);

    /// <summary><c>text</c> with every <c>${…}</c> taken out.</summary>
    internal static string WithoutReferences(string text)
    {
        var kept = new StringBuilder();
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '$' && index + 1 < text.Length && text[index + 1] == '{'
                && text.IndexOf('}', index + 2) is var close and >= 0)
            {
                index = close + 1;
            }
            else
            {
                kept.Append(text[index]);
                index += 1;
            }
        }
        return kept.ToString();
    }

    /// <summary>"needs value" for an empty value or one still holding a <c>${CC_NEEDS:…}</c> marker; else "set".</summary>
    public static string SecretState(string value) =>
        value.Length == 0 || Placeholder.ContainsMarker(value) ? NeedsValueState : SetState;

    /// <summary>A secret on a code line: its value with <c>reveal</c>, else "••• (set)" or "needs your value".</summary>
    public static string SecretText(string value, bool reveal = false)
    {
        if (reveal)
        {
            return value;
        }
        return SecretState(value) == SetState ? SetText : EditorModel.NeedsValue;
    }

    // MARK: Cutting text

    // Platform idiom: text is cut scalar by scalar on the Mac and by UTF-16 unit here, which cut
    // in the same places; a Swift `Character` would carry a combining mark after a delimiter
    // along with it and cut elsewhere.

    /// <summary>The text before and after the first <c>delimiter</c>, or null when there is none.</summary>
    internal static (string Before, string After)? Cut(string text, string delimiter)
    {
        var index = text.IndexOf(delimiter, StringComparison.Ordinal);
        return index < 0 ? null : (text[..index], text[(index + delimiter.Length)..]);
    }

    /// <summary>The text before and after the last <c>delimiter</c>, or null when there is none.</summary>
    internal static (string Before, string After)? CutLast(string text, char delimiter)
    {
        var index = text.LastIndexOf(delimiter);
        return index < 0 ? null : (text[..index], text[(index + 1)..]);
    }

    /// <summary><c>text</c> in the pieces between its <c>delimiter</c>s, empty ones included.</summary>
    internal static string[] Pieces(string text, char delimiter) =>
        text.Split(delimiter);

    /// <summary>Whether <c>text</c> starts with "-".</summary>
    internal static bool IsFlag(string text) =>
        text.StartsWith('-');

    // MARK: Arguments and URLs

    /// <summary><c>text</c> with a URL's password and every query and fragment value replaced by "•••", and
    /// so its user name and any path segment that reads as a credential or a random token;
    /// anything that is not <c>scheme://…</c> comes back as it is, and so does a value that is wholly
    /// a reference.</summary>
    public static string Url(string text, bool reveal = false)
    {
        if (reveal || Cut(text, "://") is not { } scheme)
        {
            return text;
        }
        // A user name or password may hold an unencoded `#`, `?` or `/` (`postgres://u:P#ss@db`),
        // which would end the authority below before its `@` and show the rest of it as a path,
        // a query or a fragment. Where such a user info ends cannot be told from where a path
        // holding an `@` starts, so everything up to the last `@` is masked: never more shown.
        if (CutLast(scheme.After, '@') is { } user && new[] { "#", "?", "/" }.Any(delimiter => Cut(user.Before, delimiter) is not null))
        {
            return scheme.Before + "://" + Mask + "@" + Location(user.After);
        }
        return scheme.Before + "://" + Location(scheme.After);
    }

    /// <summary>A URL past its <c>scheme://</c>, as <see cref="Url"/> shows it.</summary>
    internal static string Location(string text)
    {
        var rest = text;
        string? fragment = null;
        if (Cut(rest, "#") is { } fragmentPiece)
        {
            rest = fragmentPiece.Before;
            fragment = fragmentPiece.After;
        }
        string? query = null;
        if (Cut(rest, "?") is { } queryPiece)
        {
            rest = queryPiece.Before;
            query = queryPiece.After;
        }
        var authority = rest;
        string? path = null;
        if (Cut(rest, "/") is { } pathPiece)
        {
            authority = pathPiece.Before;
            path = pathPiece.After;
        }
        if (CutLast(authority, '@') is { } userPiece)
        {
            authority = UserInfo(userPiece.Before) + "@" + userPiece.After;
        }
        var result = authority;
        if (path is not null)
        {
            result += "/" + string.Join("/", Pieces(path, '/').Select(segment => SecretShaped(segment) ? Mask : segment));
        }
        if (query is not null)
        {
            result += "?" + Pairs(query);
        }
        if (fragment is not null)
        {
            result += "#" + Pairs(fragment);
        }
        return result;
    }

    /// <summary>A URL's <c>user:password</c>: the password masked unless it is empty or a reference, and the
    /// user name when it reads as a credential or a random token.</summary>
    internal static string UserInfo(string text)
    {
        if (Cut(text, ":") is not { } piece)
        {
            return SecretShaped(text) ? Mask : text;
        }
        var password = piece.After.Length == 0 || RefersElsewhere(piece.After) ? piece.After : Mask;
        return (SecretShaped(piece.Before) ? Mask : piece.Before) + ":" + password;
    }

    /// <summary>A query's or a fragment's <c>name=value&amp;…</c>: each value masked unless it is empty or a
    /// reference, and a piece with no "=" when it reads as a credential or a random token.</summary>
    internal static string Pairs(string text) =>
        string.Join("&", Pieces(text, '&').Select(pair =>
        {
            if (Cut(pair, "=") is not { } piece)
            {
                return SecretShaped(pair) ? Mask : pair;
            }
            return piece.After.Length == 0 || RefersElsewhere(piece.After) ? pair : piece.Before + "=" + Mask;
        }));

    /// <summary>One argument on its own: JSON through <see cref="JsonArgument"/>, <c>--header=…</c> through
    /// <see cref="Header"/>, and anything else through <see cref="Word"/>, or word by word through <see cref="ShellLine"/>
    /// when that hides what the one word's reading hides.</summary>
    public static string Argument(string arg, bool reveal = false)
    {
        if (reveal)
        {
            return arg;
        }
        if (JsonArgument(arg) is { } json)
        {
            return json;
        }
        if (Cut(arg, "=") is { } assignment && HeaderFlags.Contains(assignment.Before))
        {
            return assignment.Before + "=" + Header(assignment.After);
        }
        var whole = Word(arg);
        if (ShellLine(arg) is not { } line)
        {
            return whole;
        }
        // The shell line reading never shows what the one-word reading hid: it stands in when the
        // one word hid nothing, and when both hid something different, which only a URL's rules
        // do (`https://u:p@h/x --token t`), the argument is hidden whole. Anything else the one
        // word hid (`--password=correct horse`, `Bearer x`) it hid whole.
        if (Same(whole, arg))
        {
            return line;
        }
        return IsUrl(arg) ? Mask : whole;
    }

    /// <summary>Whether <see cref="Word"/> reads <paramref name="arg"/> by the URL's rules: it holds <c>://</c> and is not a
    /// <c>name=value</c> whose name holds none.</summary>
    internal static bool IsUrl(string arg)
    {
        if (Cut(arg, "://") is null)
        {
            return false;
        }
        return Cut(arg, "=") is not { } piece || Cut(piece.Before, "://") is not null;
    }

    /// <summary>An argument as one word, with a shell default for a secret masked first
    /// (<see cref="DefaultsMasked"/>): a URL through <see cref="Url"/>, a value that is wholly a reference as
    /// written, a header through <see cref="Header"/> (<c>X-Api-Key: …</c> after a flag of any name) when a whole
    /// word of its name is a secret's (<see cref="HeaderNamesASecret"/>) and its value is not a scope word or
    /// a path (<c>token:read</c>, Docker's <c>-v secrets:/run/secrets</c>), <c>name=value</c> with the value masked
    /// when the name is a secret's (<c>--api-key=…</c>, Docker's <c>-e API_KEY=…</c>) or a pair flag's
    /// (<c>--user=admin:…</c>) - read before a URL in the value, so the URL's rules never pass the value's
    /// start through - curl's <c>-uadmin:…</c> glued, <c>user:password@host</c> with no scheme and no
    /// whitespace as a URL's user info, and "•••" for anything that reads as a credential or a
    /// random token - a "name" that does is part of the token, so the whole word is masked.</summary>
    internal static string Word(string arg)
    {
        var text = DefaultsMasked(arg);
        if (IsUrl(text))
        {
            return Url(text);
        }
        if (RefersElsewhere(text))
        {
            return text;
        }
        if (Cut(text, ":") is { } named && IsHeaderName(named.Before) && HeaderNamesASecret(named.Before)
            && !IsPlainValue(named.After, named.Before))
        {
            return Header(text);
        }
        if (text.StartsWith("-u", StringComparison.Ordinal) && GluedPair(text[2..]) is { } glued)
        {
            return "-u" + glued;
        }
        if (Cut(text, "=") is { } piece && !LooksSecret(piece.Before))
        {
            if (CredentialHeuristics.NamesASecret(piece.Before) && !RefersElsewhere(piece.After))
            {
                return piece.Before + "=" + Mask;
            }
            if (PairFlags.Contains(piece.Before) && Pair(piece.After) is { } shown)
            {
                return piece.Before + "=" + shown;
            }
            return piece.Before + "=" + Argument(piece.After);
        }
        if (CutLast(text, '@') is { } at && Cut(at.Before, ":") is { } user && IsUserName(user.Before)
            && user.After.Length > 0 && !user.After.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\')
            && !at.After.Any(char.IsWhiteSpace))
        {
            return UserInfo(at.Before) + "@" + Location(at.After);
        }
        return LooksSecret(text) ? Mask : text;
    }

    /// <summary>Whether <paramref name="text"/> is a header's or a user's name: ASCII letters, digits, <c>-</c> and <c>_</c>, and for a
    /// user's also <c>.</c>; never empty.</summary>
    internal static bool IsHeaderName(string text) =>
        text.Length > 0 && text.All(c => IsAsciiWordCharacter(c) || c == '-');

    internal static bool IsUserName(string text) =>
        text.Length > 0 && text.All(c => IsAsciiWordCharacter(c) || c is '-' or '.');

    /// <summary>Whether <paramref name="c"/> is an ASCII letter or digit, or <c>_</c>.</summary>
    internal static bool IsAsciiWordCharacter(char c) =>
        char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>Whether a header named <paramref name="name"/> carries a secret: it is named for one or signs in.</summary>
    internal static bool SignsIn(string name) =>
        CredentialHeuristics.NamesASecret(name) || SignInHeaders.Contains(name.ToLowerInvariant());

    /// <summary>The words of a header's name that name a secret or sign in.</summary>
    internal static readonly IReadOnlySet<string> SecretHeaderWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "token", "key", "apikey", "secret", "password", "passwd", "pass", "pwd", "pw", "auth", "authorization",
        "credential", "credentials", "bearer", "cookie", "passphrase",
    };

    /// <summary>The words of the ASCII name <paramref name="name"/>, lowered: split at <c>-</c>, <c>_</c>, <c>.</c>, where a lower-case letter
    /// meets an upper-case one and where a letter meets a digit (<c>apiKey2</c> is api, key, 2).</summary>
    internal static List<string> NameWords(string name)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var lowerBefore = false;
        var letterBefore = false;
        foreach (var c in name)
        {
            if (c is '-' or '_' or '.')
            {
                words.Add(word.ToString());
                word.Clear();
                lowerBefore = false;
                letterBefore = false;
                continue;
            }
            var upper = char.IsAsciiLetterUpper(c);
            if ((upper && lowerBefore) || (char.IsAsciiDigit(c) && letterBefore))
            {
                words.Add(word.ToString());
                word.Clear();
            }
            word.Append(upper ? (char)(c + 32) : c);
            lowerBefore = char.IsAsciiLetterLower(c);
            letterBefore = upper || lowerBefore;
        }
        words.Add(word.ToString());
        return words;
    }

    /// <summary>Whether a whole word of the ASCII name <paramref name="name"/> (<see cref="NameWords"/>) names a secret or signs in:
    /// <c>X-Api-Key</c>, <c>apiKey</c>, <c>token1</c>, <c>Authorization</c>, <c>Cookie</c>, but not <c>keycloak</c> or <c>ssh-keys</c>.</summary>
    internal static bool HeaderNamesASecret(string name) =>
        NameWords(name).Any(SecretHeaderWords.Contains);

    /// <summary>Values after a <c>name:</c> that are an access scope, not a secret (<c>token:read</c>).</summary>
    internal static readonly string[] ScopeWords = ["read", "write", "admin", "readonly", "read-only", "rw", "ro", "all", "none"];

    /// <summary>The words of a name that make its value a password, whatever word it is (<c>password:admin</c>).</summary>
    internal static readonly IReadOnlySet<string> PasswordWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "password", "passwd", "passphrase", "pass", "pwd", "pw",
    };

    /// <summary>Whether a header-shaped word's value reads as written, under a name with no password word: a
    /// path that does not read as a secret (<c>secrets:/run/secrets</c>, but not <c>X-Api-Key:/Abc9…</c> or
    /// <c>password:/x</c>), or a scope word (<c>token:read</c>, but not <c>password:admin</c>).</summary>
    internal static bool IsPlainValue(string value, string name)
    {
        var trimmed = value.TrimSpaces();
        if (NameWords(name).Any(PasswordWords.Contains))
        {
            return false;
        }
        if (trimmed.StartsWith('/'))
        {
            return !LooksSecret(trimmed);
        }
        return ScopeWords.Contains(trimmed.ToLowerInvariant());
    }

    /// <summary>Values a single-dash option such as <c>-update:</c> or <c>-useLegacy:</c> takes, which no password is.</summary>
    internal static readonly string[] SwitchWords = ["true", "false", "yes", "no", "on", "off"];

    /// <summary>curl's <c>-u</c> with its <c>user:password</c> glued on (<c>-uadmin:…</c>), as <see cref="Pair"/> shows it; null
    /// unless a user's name comes before the ":" - a Go-style flag such as <c>-url=…</c> or <c>-user=…</c>
    /// reads as <c>name=value</c> - and for a switch's value (<c>-update:true</c>).</summary>
    internal static string? GluedPair(string text)
    {
        if (Cut(text, ":") is not { } piece || !IsUserName(piece.Before) || SwitchWords.Contains(piece.After.ToLowerInvariant()))
        {
            return null;
        }
        return Pair(text);
    }

    /// <summary>curl's flags whose value is <c>user:password</c>.</summary>
    internal static readonly string[] PairFlags = ["-u", "--user", "--proxy-user"];

    /// <summary>A pair flag's <c>user:password</c> as a URL's user info shows it; null for a value with no ":", a
    /// URL, which reads as one, a reference (<c>${CC_NEEDS:creds}</c>), or a user and group as Docker's
    /// <c>--user</c> takes them - a uid and a gid or a group's name (<c>1000:1000</c>, <c>1000:node</c>), or a group
    /// named as the user (<c>node:node</c>) - which read as any argument does. A uid before anything else
    /// (<c>123456:…</c>, an account number and its key) is masked.</summary>
    internal static string? Pair(string text)
    {
        if (RefersElsewhere(text) || IsUrl(text) || Cut(text, ":") is not { } piece)
        {
            return null;
        }
        static bool Digits(string part) => part.Length > 0 && part.All(char.IsAsciiDigit);
        if ((Digits(piece.Before) && (Digits(piece.After) || IsGroupName(piece.After))) || Same(piece.Before, piece.After))
        {
            return null;
        }
        return UserInfo(text);
    }

    /// <summary>Whether <paramref name="text"/> is shaped as a Unix group's name, <c>[a-z_][a-z_-]*</c>: no digits or capitals.</summary>
    internal static bool IsGroupName(string text) =>
        text.Length > 0 && (char.IsAsciiLetterLower(text[0]) || text[0] == '_')
        && text.All(c => char.IsAsciiLetterLower(c) || c is '_' or '-');

    /// <summary><c>text</c> with the default of each <c>${NAME:-default}</c> or <c>${NAME-default}</c> replaced by "•••" when
    /// <c>NAME</c> is named for a secret or the default reads as one, unless the default is empty or a
    /// reference: a shell passes the default when the variable is unset, so it can be the secret.
    /// The default runs to the brace that closes its own, past any <c>${…}</c> inside it.</summary>
    internal static string DefaultsMasked(string text)
    {
        var kept = new StringBuilder();
        var index = 0;
        while (index < text.Length)
        {
            if (ShellDefault(text, index) is not { } found)
            {
                kept.Append(text[index]);
                index += 1;
                continue;
            }
            var (name, dash, close) = found;
            var value = text[(dash + 1)..close];
            kept.Append(text, index, dash + 1 - index);
            if (value.Length > 0 && !RefersElsewhere(value)
                && (CredentialHeuristics.NamesASecret(text[(index + 2)..name]) || LooksSecret(value)))
            {
                kept.Append(Mask);
            }
            else
            {
                kept.Append(value);
            }
            kept.Append('}');
            index = close + 1;
        }
        return kept.ToString();
    }

    /// <summary>Where the <c>${NAME:-default}</c> or <c>${NAME-default}</c> starting at <paramref name="start"/> ends its name, has its
    /// <c>-</c> and closes; null where none starts there.</summary>
    internal static (int Name, int Dash, int Close)? ShellDefault(string text, int start)
    {
        if (text[start] != '$' || start + 1 >= text.Length || text[start + 1] != '{')
        {
            return null;
        }
        var name = start + 2;
        while (name < text.Length && IsAsciiWordCharacter(text[name]))
        {
            name += 1;
        }
        var dash = name;
        if (dash < text.Length && text[dash] == ':')
        {
            dash += 1;
        }
        if (name == start + 2 || dash >= text.Length || text[dash] != '-')
        {
            return null;
        }
        var depth = 1;
        for (var close = dash + 1; close < text.Length; close++)
        {
            if (text[close] == '{')
            {
                depth += 1;
            }
            if (text[close] == '}')
            {
                depth -= 1;
            }
            if (depth == 0)
            {
                return (name, dash, close);
            }
        }
        return null;
    }

    /// <summary>An argument that is a JSON object or array - Smithery's <c>--config '{"apiKey":"…"}'</c> - with
    /// each leaf masked by its key, as a config's are, on one line; as written when nothing in it
    /// changed. JSON nested deeper than <see cref="JsonValue.Parse(string)"/> reads is "•••", since what it holds cannot
    /// be read, and so is text starting with <c>{</c> or <c>[</c> that is not JSON (JSON5's <c>{apiKey: '…'}</c>,
    /// broken JSON) but names a secret as a key (<see cref="KeyedSecret"/>); other such text - an IPv6
    /// literal, a glob, a template - is null. Null for anything else. Before the shell line, which
    /// would split JSON at its spaces.</summary>
    internal static string? JsonArgument(string arg)
    {
        var first = arg.FirstOrDefault(c => !char.IsWhiteSpace(c));
        if (first is not ('{' or '['))
        {
            return null;
        }
        if (!McpSession.Nests(McpSession.MaxNestingDepth, arg))
        {
            return Mask;
        }
        JsonValue value;
        try
        {
            value = JsonValue.Parse(arg);
        }
        catch (JsonException)
        {
            return KeyedSecret(arg) ? Mask : null;
        }
        if (value.Kind is not (JsonKind.Object or JsonKind.Array))
        {
            return null;
        }
        var shown = CompactText(Leaves(value, Exposure.Masked, secret: false));
        return Same(shown, CompactText(value)) ? arg : shown;
    }

    /// <summary>Whether <paramref name="text"/> gives a value under a key named for a secret, or under <c>env</c> or <c>headers</c>
    /// (<see cref="NamesASecret"/>): a name of ASCII letters, digits,
    /// <c>_</c>, <c>-</c>, <c>$</c> and <c>.</c>, in quotes or not, right before a <c>:</c> or <c>=</c> (<c>apiKey: …</c>, <c>"token": …</c>,
    /// <c>password=…</c>). A <c>:</c> after a bracket or another <c>:</c> (<c>[::1]</c>, <c>[Net.X]::Y</c>) has no name.</summary>
    internal static bool KeyedSecret(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not (':' or '='))
            {
                continue;
            }
            var end = index;
            while (end > 0 && char.IsWhiteSpace(text[end - 1]))
            {
                end -= 1;
            }
            if (end > 0 && text[end - 1] is '"' or '\'')
            {
                end -= 1;
            }
            var start = end;
            while (start > 0 && (IsAsciiWordCharacter(text[start - 1]) || text[start - 1] is '-' or '$' or '.'))
            {
                start -= 1;
            }
            if (start < end && NamesASecret(text[start..end]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>An argument holding whitespace - a shell line for <c>sh -c</c> or <c>cmd /c</c>, or a whole command
    /// line written as the command - masked as <see cref="Arguments(IReadOnlyList{string}, bool)"/> masks the words a shell would pass,
    /// so a flag named for a secret among them hides the word after it as it would in <c>args</c>. The
    /// split drops the line's quotes, so a line is rejoined, with each word quoted as the dialog
    /// quotes an argument, only when one of its words changed. A word that changed and ends in a
    /// backslash escaping the space after it (<c>--api-key a\ b</c>) runs on into the next, which is
    /// hidden too: the split reads a backslash as itself, as a Windows path needs. Null for an
    /// argument with no whitespace, or none of whose words changed.</summary>
    internal static string? ShellLine(string arg)
    {
        if (!arg.Any(char.IsWhiteSpace))
        {
            return null;
        }
        var words = ConnectorTarget.ShellWords(arg);
        var shown = Arguments(words, Exposure.Masked).ToList();
        for (var index = 0; index < words.Count - 1; index++)
        {
            if (!Same(shown[index], words[index]) && (words[index].Length - words[index].TrimEnd('\\').Length) % 2 == 1)
            {
                shown[index + 1] = Mask;
            }
        }
        return words.Zip(shown).All(pair => Same(pair.First, pair.Second)) ? null : ControlText.Words(shown);
    }

    /// <summary>An argument list: each through <see cref="Argument"/>, and also the value after a flag named for a
    /// secret (<c>--api-key x</c>, <c>--static-oauth-client-info {…}</c>) unless it is itself a flag, a
    /// header after <c>--header</c>, <c>--headers</c> or <c>-H</c>, and a header's value given as a word of its own
    /// (<see cref="HeaderValues"/>).</summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string> args, bool reveal = false) =>
        Arguments(args, reveal ? Exposure.Revealed : Exposure.Masked);

    /// <summary>An argument list as <c>exposure</c> shows it. The dialog keeps every argument as written but a
    /// header's value and the OAuth client secret.</summary>
    internal static IReadOnlyList<string> Arguments(IReadOnlyList<string> args, Exposure exposure)
    {
        if (exposure == Exposure.Revealed)
        {
            return args;
        }
        var values = HeaderValues(args);
        return args.Select((arg, index) =>
        {
            if (values.Contains(index))
            {
                return Mask;
            }
            if (index > 0)
            {
                var previous = args[index - 1];
                if (HeaderFlags.Contains(previous))
                {
                    return Header(arg);
                }
                if (exposure == Exposure.Masked && PairFlags.Contains(previous) && Pair(arg) is { } pair)
                {
                    return pair;
                }
                if (HidesAfter(previous, exposure) && !IsFlag(arg) && !RefersElsewhere(arg))
                {
                    return exposure == Exposure.Dialog ? ClientInfo(arg) : Mask;
                }
            }
            if (AfterScheme(args, index, exposure))
            {
                return Mask;
            }
            return exposure == Exposure.Masked ? Argument(arg) : DialogArgument(arg);
        }).ToList();
    }

    /// <summary>Whether <c>args[index]</c> is the value after one or more scheme words that follow a flag named
    /// for a secret (<c>--token Bearer x</c>, <c>--auth Token Token x</c>, and curl's <c>-H'Authorization: Bearer
    /// x'</c> as a shell splits it, <c>-HAuthorization:</c> <c>Bearer</c> <c>x</c>): the flag hides the scheme word, so
    /// the word after it is hidden too, as <see cref="HeaderValues"/> hides a header's value past its scheme
    /// word, unless it is a flag or a reference.</summary>
    internal static bool AfterScheme(IReadOnlyList<string> args, int index, Exposure exposure)
    {
        if (exposure != Exposure.Masked || index <= 1 || IsFlag(args[index]) || RefersElsewhere(args[index]))
        {
            return false;
        }
        var flag = index - 1;
        while (flag > 0 && CredentialHeuristics.IsSchemeWord(args[flag]))
        {
            flag -= 1;
        }
        return flag < index - 1 && HidesAfter(args[flag], exposure);
    }

    /// <summary>Whether the word after <paramref name="word"/> is a secret: after a flag <see cref="HidesNext"/> accepts, and, when
    /// masked, after a flag with <c>=</c> whose glued value leaves the secret to the next word - nothing
    /// or only a scheme word after a flag named for a secret (<c>--token=</c> <c>x</c>, <c>--token=Bearer</c> <c>x</c>),
    /// or after a header flag a header named for a secret or that signs in, with nothing or only a
    /// scheme word after its ":" (<c>--header=Authorization:</c> <c>Bearer</c> <c>x</c>, <c>--header=Authorization:
    /// Bearer</c> <c>x</c>), as <see cref="HeaderValues"/> reads a header after a bare <c>--header</c>.</summary>
    internal static bool HidesAfter(string word, Exposure exposure)
    {
        if (HidesNext(word, exposure))
        {
            return true;
        }
        if (exposure != Exposure.Masked || Cut(word, "=") is not { } piece || !IsFlag(piece.Before))
        {
            return false;
        }
        if (HidesNext(piece.Before, exposure))
        {
            return piece.After.Length == 0 || CredentialHeuristics.IsSchemeWord(piece.After);
        }
        if (!HeaderFlags.Contains(piece.Before) || HeaderName(piece.After) is not { } named)
        {
            return false;
        }
        return SignsIn(named.Name);
    }

    /// <summary>One argument as the dialog shows it: as written, but for the value of
    /// <c>--static-oauth-client-info=…</c>, which holds the client secret, and a header's after
    /// <c>--header=…</c>.</summary>
    internal static string DialogArgument(string arg)
    {
        if (Cut(arg, "=") is not { } piece)
        {
            return arg;
        }
        if (HeaderFlags.Contains(piece.Before))
        {
            return piece.Before + "=" + Header(piece.After);
        }
        if (!HidesNext(piece.Before, Exposure.Dialog) || RefersElsewhere(piece.After))
        {
            return arg;
        }
        return piece.Before + "=" + ClientInfo(piece.After);
    }

    /// <summary>The OAuth client info as the dialog shows it: its JSON on one line with the client secret,
    /// and any value under a key named for a secret, masked; "•••" for text that is not a JSON
    /// object, which could be the secret itself.</summary>
    internal static string ClientInfo(string text)
    {
        JsonValue value;
        try
        {
            value = JsonValue.Parse(text);
        }
        catch (JsonException)
        {
            return Mask;
        }
        return value.Kind == JsonKind.Object ? CompactText(Leaves(value, Exposure.Dialog, secret: false)) : Mask;
    }

    /// <summary>The flag whose value holds an OAuth client's secret.</summary>
    internal const string OauthClientInfoFlag = "--static-oauth-client-info";

    /// <summary>Whether the value after <c>flag</c> is a secret: after any flag named for one when masked, and
    /// only after the OAuth client info in the dialog.</summary>
    internal static bool HidesNext(string flag, Exposure exposure)
    {
        if (exposure == Exposure.Dialog)
        {
            return flag == OauthClientInfoFlag;
        }
        return IsFlag(flag) && Cut(flag, "=") is null && CredentialHeuristics.NamesASecret(flag);
    }

    /// <summary>The flags a header follows: mcp-remote's and supergateway's <c>--header</c>, mcp-proxy's
    /// <c>--headers</c>, and curl's <c>-H</c>.</summary>
    internal static readonly string[] HeaderFlags = ["--header", "--headers", "-H"];

    /// <summary>Sign-in headers whose names hold no word <see cref="CredentialHeuristics.NamesASecret"/> reads.</summary>
    internal static readonly string[] SignInHeaders = ["cookie"];

    /// <summary>The indexes of the header values among <paramref name="args"/> given as words of their own: after a header
    /// flag, a word with no ":", or with nothing or only a scheme word after its ":", is the
    /// header's name and the next word its value (<c>--headers Authorization x</c>, <c>Authorization:
    /// Bearer x</c> as a shell splits it, <c>-H Authorization:Bearer x</c>), past a scheme word such as
    /// <c>Bearer</c>. A value is hidden when its header is named for a secret or signs in, and is not
    /// empty, a flag or a reference, as <see cref="Header"/> shows those; another header's name and value
    /// read as any argument does, since <c>-H</c> is also a host.</summary>
    internal static HashSet<int> HeaderValues(IReadOnlyList<string> args)
    {
        var values = new HashSet<int>();
        for (var index = 0; index < args.Count; index++)
        {
            if (!HeaderFlags.Contains(args[index]) || index + 2 >= args.Count)
            {
                continue;
            }
            if (HeaderName(args[index + 1]) is not { } named || !SignsIn(named.Name))
            {
                continue;
            }
            var value = index + 2;
            if (!named.Scheme && CredentialHeuristics.IsSchemeWord(args[value]) && value + 1 < args.Count)
            {
                value += 1;
            }
            var text = args[value];
            if (text.Length > 0 && !IsFlag(text) && !RefersElsewhere(text))
            {
                values.Add(value);
            }
        }
        return values;
    }

    /// <summary>A header's name when <paramref name="word"/> gives no value with it - the whole word with no ":", or what is
    /// before a ":" with nothing after it - and whether a scheme word follows its ":" instead
    /// (<c>Authorization:Bearer</c>). Null for a word with a value.</summary>
    internal static (string Name, bool Scheme)? HeaderName(string word)
    {
        if (Cut(word, ":") is not { } piece)
        {
            return (word, false);
        }
        var value = piece.After.TrimSpaces();
        if (value.Length == 0)
        {
            return (piece.Before, false);
        }
        return CredentialHeuristics.IsSchemeWord(value) ? (piece.Before, true) : null;
    }

    /// <summary>"Name: value" with the value masked unless it is empty or a reference.</summary>
    internal static string Header(string text)
    {
        if (Cut(text, ":") is not { } piece)
        {
            return Argument(text);
        }
        var value = piece.After.TrimSpaces();
        if (value.Length == 0 || RefersElsewhere(value))
        {
            return text;
        }
        return piece.Before + ": " + Mask;
    }

    // MARK: Whole configs

    /// <summary>The keys whose values are secrets by name, in lower case: every value under them is.</summary>
    internal static readonly IReadOnlySet<string> SecretKeys = new HashSet<string>(StringComparer.Ordinal) { "env", "envs", "headers" };

    /// <summary>Whether <paramref name="key"/> is one of <see cref="SecretKeys"/>, whatever its case (<c>Env</c>, <c>HEADERS</c>).</summary>
    internal static bool IsSecretKey(string key) =>
        SecretKeys.Contains(key.ToLowerInvariant());

    /// <summary>Whether a key's values are secrets: <c>env</c>, <c>envs</c> and <c>headers</c> in any case wherever they
    /// sit, and any key named for a secret.</summary>
    internal static bool NamesASecret(string key) =>
        IsSecretKey(key) || CredentialHeuristics.NamesASecret(key);

    /// <summary>The config as structured output carries it, in its own shape: <c>env</c> and <c>headers</c> values
    /// become "set" or "needs value", and so does every string or number under a key named for a
    /// secret; an array is an argument list through <see cref="Arguments(IReadOnlyList{string}, bool)"/>, and every other string goes
    /// through <see cref="Argument"/>, a URL through <see cref="Url"/>.</summary>
    public static JsonValue MaskedConfig(JsonValue config, bool reveal = false)
    {
        if (reveal)
        {
            return config;
        }
        if (config.Kind != JsonKind.Object)
        {
            return Leaves(config, Exposure.Masked, secret: false);
        }
        var result = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var (key, value) in config.ObjectProperties)
        {
            if (IsSecretKey(key) && value.Kind == JsonKind.Object)
            {
                result[key] = JsonValue.Object(value.ObjectProperties.Select(item => new KeyValuePair<string, JsonValue>(
                    item.Key, JsonValue.String(item.Value.Kind == JsonKind.String ? SecretState(item.Value.StringValue) : SetState))));
            }
            else
            {
                result[key] = Leaves(value, Exposure.Masked, secret: NamesASecret(key));
            }
        }
        return JsonValue.Object(result);
    }

    /// <summary><c>value</c> as <c>exposure</c> shows it: under a key named for a secret (<c>secret</c>) every string or
    /// number reads "set" or "needs value"; masked, an array is an argument list, its strings
    /// masked as <see cref="Arguments(IReadOnlyList{string}, bool)"/> masks them and anything after a flag named for a secret too, and
    /// every other string goes through <see cref="Argument"/>; in the dialog an array is an argument list as
    /// the dialog shows one, and the rest is as written.</summary>
    internal static JsonValue Leaves(JsonValue value, Exposure exposure, bool secret)
    {
        if (exposure == Exposure.Revealed)
        {
            return value;
        }
        switch (value.Kind)
        {
            case JsonKind.String:
                if (secret)
                {
                    return JsonValue.String(SecretState(value.StringValue));
                }
                return exposure == Exposure.Masked ? JsonValue.String(Argument(value.StringValue)) : value;
            case JsonKind.Int:
            case JsonKind.Double:
                return secret ? JsonValue.String(SetState) : value;
            case JsonKind.Array:
                if (secret)
                {
                    return JsonValue.Array(value.ArrayItems.Select(item => Leaves(item, exposure, secret: true)));
                }
                var shown = Arguments(value.ArrayItems.Select(item => item.Kind == JsonKind.String ? item.StringValue : "").ToList(), exposure);
                return JsonValue.Array(value.ArrayItems.Zip(shown, (item, text) =>
                    item.Kind == JsonKind.String ? JsonValue.String(text) : Leaves(item, exposure, secret: text == Mask)));
            case JsonKind.Object:
                return JsonValue.Object(value.ObjectProperties.Select(item => new KeyValuePair<string, JsonValue>(
                    item.Key, Leaves(item.Value, exposure, secret || NamesASecret(item.Key)))));
            default:
                return value;
        }
    }

    // MARK: Lines

    /// <summary>One field of a config as a person reads it: its label, and each value as written (<c>Raw</c>,
    /// which tells a changed secret from an unchanged one) and as shown.</summary>
    private sealed record Field(string Label, List<(string Raw, string Shown)> Values);

    /// <summary>What a connector runs, one field per line, for <c>get_connector</c>'s text and the confirmation
    /// dialogs: a connector reached through mcp-remote by its URL and how it signs in; one reached
    /// by URL alone by its URL and headers; anything else by its command, arguments and environment.
    /// Every top-level key those do not show in full follows on a line of its own, so the add
    /// dialog shows everything that would run. Fields that share a label share its line.</summary>
    public static IReadOnlyList<string> CodeLines(JsonValue config, Exposure exposure = Exposure.Masked)
    {
        var all = Fields(config, exposure);
        var labels = new List<string>();
        foreach (var field in all)
        {
            if (!labels.Contains(field.Label))
            {
                labels.Add(field.Label);
            }
        }
        return ControlText.Labelled(labels.SelectMany(label =>
            all.Where(field => field.Label == label).SelectMany(field => field.Values).Select((value, index) =>
                (index == 0 ? label : "", value.Shown))).ToList());
    }

    /// <summary>The fields an edit changes, for the change confirmation: each changed field's label, then
    /// "− " before each value it loses and "+ " before each it gains. A secret that changed shows
    /// as changed although both sides read "••• (set)". Every difference between the two configs
    /// shows, as every key shows on the code lines, and labels and values go through
    /// <see cref="ControlText.Escaped(string)"/> as the code lines' do.</summary>
    public static IReadOnlyList<string> ChangeLines(JsonValue before, JsonValue after, Exposure exposure = Exposure.Masked)
    {
        var old = Fields(before, exposure);
        var @new = Fields(after, exposure);
        var labels = new List<string>();
        foreach (var field in @new.Concat(old))
        {
            if (!labels.Contains(field.Label))
            {
                labels.Add(field.Label);
            }
        }
        var lines = new List<string>();
        foreach (var label in labels)
        {
            var was = old.Where(field => field.Label == label).SelectMany(field => field.Values).ToList();
            var now = @new.Where(field => field.Label == label).SelectMany(field => field.Values).ToList();
            var lost = was.Where(item => !now.Any(other => Same(other, item))).ToList();
            var gained = now.Where(item => !was.Any(other => Same(other, item))).ToList();
            if (lost.Count == 0 && gained.Count == 0)
            {
                continue;
            }
            lines.Add(ControlText.Escaped(label));
            lines.AddRange(lost.Select(item => RemovedPrefix + ControlText.Escaped(item.Shown)));
            lines.AddRange(gained.Select(item => AddedPrefix + ControlText.Escaped(item.Shown)));
        }
        return lines;
    }

    // Platform idiom: values are compared scalar by scalar on the Mac, as they are compared
    // ordinally here; Swift's == holds canonically equivalent text equal, which would hide a change.
    private static bool Same((string Raw, string Shown) one, (string Raw, string Shown) other) =>
        Same(one.Raw, other.Raw) && Same(one.Shown, other.Shown);

    private static bool Same(string one, string other) =>
        string.Equals(one, other, StringComparison.Ordinal);

    /// <summary>The fields the code lines and change lines show: the shape's own, then a field for each
    /// top-level key those do not show in full.</summary>
    private static List<Field> Fields(JsonValue config, Exposure exposure)
    {
        if (config.Kind != JsonKind.Object)
        {
            return [new Field(OtherLabel, [(Raw(JsonValue.Array([config])), ValueText("", config, exposure))])];
        }
        var (fields, shown) = View(config, exposure);
        var properties = config.ObjectProperties;
        return fields.Concat(properties.Keys.Where(key => !shown.Contains(key))
            .Order(StringComparer.Ordinal)
            .Select(key =>
                // Raw with its key, so it never matches a field's raw under the same label (a
                // `url` key beside mcp-remote's URL).
                new Field(Single(key, exposure), [(Raw(JsonValue.Array([JsonValue.String(key), properties[key]])), ValueText(key, properties[key], exposure))]))).ToList();
    }

    /// <summary>A top-level key's value on a line, after <see cref="Leaves"/>: a string as itself, anything else as one
    /// line of JSON; a string under a key named for a secret as <see cref="SecretText"/> shows it.</summary>
    private static string ValueText(string key, JsonValue value, Exposure exposure)
    {
        if (value.Kind == JsonKind.String)
        {
            if (NamesASecret(key))
            {
                return SecretText(value.StringValue, exposure == Exposure.Revealed);
            }
            return exposure == Exposure.Masked ? Argument(value.StringValue) : Single(value.StringValue, exposure);
        }
        return CompactText(Leaves(value, exposure, NamesASecret(key)));
    }

    /// <summary>A value as written, for telling one from another: every part of it, nothing shown.</summary>
    private static string Raw(JsonValue value) =>
        CompactText(value);

    // Platform idiom: JSONEncoder with sorted keys and unescaped slashes on the Mac, which this
    // writes byte for byte as AppleJsonFormat.EditorText without its line breaks.
    /// <summary>One line of JSON.</summary>
    private static string CompactText(JsonValue value) =>
        AppleJsonWriter.Write(value, AppleJsonFormat.EditorText with { Pretty = false });

    /// <summary>The fields of <c>config</c> as <c>exposure</c> shows them, and the top-level keys they show in full.</summary>
    private static (List<Field> Fields, HashSet<string> Shown) View(JsonValue config, Exposure exposure)
    {
        if (config.Kind != JsonKind.Object)
        {
            return ([], []);
        }
        // Platform idiom: RemotePattern.Decode here also reads `cmd /c npx …`, so a Windows
        // connector shows the same lines; the Mac has no such launcher.
        if (RemotePattern.Decode(config) is { } remote)
        {
            return RemoteView(config, remote, exposure);
        }
        if (config["command"] is null && config["url"] is { Kind: JsonKind.String } address)
        {
            return UrlView(config, address.StringValue, exposure);
        }
        return LocalView(config, exposure);
    }

    /// <summary>A connector reached through mcp-remote: its bridge, URL and sign-in, every argument those
    /// do not show, and its environment but the variable a header reads. The sign-in shows only
    /// the flags <see cref="RemotePattern.Encode"/> writes for it; one written any other way (spacing, a second
    /// copy, a field the decoder drops) shows among the arguments. The bridge's value as written
    /// holds the launcher, so dropping <c>-y</c> shows as a change.</summary>
    private static (List<Field> Fields, HashSet<string> Shown) RemoteView(JsonValue config, RemoteConfig remote, Exposure exposure)
    {
        var args = Strings(config["args"]);
        var found = args.FindIndex(arg => Same(arg, remote.Package));
        var bridge = found < 0 ? 0 : found;
        var launcher = new[] { config["command"] ?? JsonValue.Null }.Concat(args.Take(bridge + 1).Select(JsonValue.String));
        var fields = new List<Field>
        {
            new(TypeLabel, [(Raw(JsonValue.Array(launcher)), RemoteBridgeType(remote.Package))]),
            new(UrlLabel, [(Raw(JsonValue.String(remote.Url)), Single(Url(remote.Url, exposure != Exposure.Masked), exposure))]),
        };
        var reveal = exposure == Exposure.Revealed;
        switch (remote.Auth)
        {
            case RemoteAuth.Bearer bearer:
                fields.Add(new Field(HeadersLabel,
                    [(Raw(JsonValue.Array([JsonValue.String(bearer.Token)])), Assignment("Authorization", SecretText(bearer.Token, reveal)))]));
                break;
            case RemoteAuth.Header header:
                fields.Add(new Field(HeadersLabel,
                    [(Raw(JsonValue.Array([JsonValue.String(header.Name), JsonValue.String(header.Value)])), Assignment(Single(header.Name, exposure), SecretText(header.Value, reveal)))]));
                break;
            case RemoteAuth.OAuthClient client:
                fields.Add(new Field(OauthLabel,
                    [(Raw(JsonValue.Array([JsonValue.String(client.ClientId), JsonValue.String(client.ClientSecret)])),
                        OauthClientText(Single(client.ClientId, exposure), SecretText(client.ClientSecret, reveal)))]));
                if (client.Scopes.Length > 0)
                {
                    fields.Add(new Field(ScopesLabel, [(Raw(JsonValue.String(client.Scopes)), Single(client.Scopes, exposure))]));
                }
                break;
        }
        var signIn = SignInArguments(remote.Auth);
        var rest = Unmodelled(args.Skip(bridge + 2).ToList(), signIn.Pairs);
        var kept = rest.OfType<string>().ToList();
        if (kept.Count > 0)
        {
            fields.Add(new Field(ArgsLabel, [(Raw(JsonValue.Array(rest.Select(item => item is null ? JsonValue.Null : JsonValue.String(item)))),
                ControlText.Words(Arguments(kept, exposure)))]));
        }
        var shown = new HashSet<string>(StringComparer.Ordinal) { "command", "args" };
        if (config["env"] is { Kind: JsonKind.Object } env && env.ObjectProperties.Count > 0)
        {
            var passthrough = env.ObjectProperties.Where(item => !signIn.Variables.Contains(item.Key)).ToDictionary(StringComparer.Ordinal);
            if (passthrough.Count > 0)
            {
                fields.Add(SecretField(EnvLabel, passthrough, exposure, shownInDialog: true));
            }
            shown.Add("env");
        }
        return (fields, shown);
    }

    /// <summary>The sign-in flags and values <see cref="RemotePattern.Encode"/> writes for <c>auth</c>, in pairs, and the
    /// variables it sets for them.</summary>
    private static (List<List<string>> Pairs, HashSet<string> Variables) SignInArguments(RemoteAuth auth)
    {
        // Platform idiom: encoded with the plain npx launcher, the Mac mirror's only one, named here so
        // a decoded `cmd /c npx` launcher does not move where the pairs start.
        var encoded = RemotePattern.Encode(new RemoteConfig("", auth, RemoteLaunchStyle.Npx, [],
            new Dictionary<string, string>(), RemotePattern.DefaultPackage));
        var args = Strings(encoded["args"]);
        var variables = new HashSet<string>(StringComparer.Ordinal);
        if (encoded["env"] is { Kind: JsonKind.Object } env)
        {
            variables.UnionWith(env.ObjectProperties.Keys);
        }
        // Past "-y", the bridge and the URL, encode writes nothing but sign-in pairs.
        var pairs = new List<List<string>>();
        for (var index = 3; index < args.Count - 1; index += 2)
        {
            pairs.Add([args[index], args[index + 1]]);
        }
        return (pairs, variables);
    }

    /// <summary>The arguments after the URL with each of <c>signIn</c>'s pairs that they hold, once, replaced by
    /// null, where the sign-in fields show it, so where a pair stands among the other arguments
    /// shows but not the order of two pairs, which mcp-remote reads by name. A pair is a flag and
    /// its value as the decoder pairs them: <c>--header</c>, <c>--static-oauth-client-info</c> or
    /// <c>--static-oauth-client-metadata</c> before another argument.</summary>
    private static List<string?> Unmodelled(List<string> args, List<List<string>> signIn)
    {
        var expected = signIn.ToList();
        var kept = new List<string?>();
        var index = 0;
        while (index < args.Count)
        {
            if (SignInFlags.Contains(args[index]) && index + 1 < args.Count)
            {
                var pair = new List<string> { args[index], args[index + 1] };
                var match = expected.FindIndex(item => item.Count == 2 && Same(item[0], pair[0]) && Same(item[1], pair[1]));
                if (match >= 0)
                {
                    expected.RemoveAt(match);
                    kept.Add(null);
                }
                else
                {
                    kept.AddRange(pair);
                }
                index += 2;
            }
            else
            {
                kept.Add(args[index]);
                index += 1;
            }
        }
        return kept;
    }

    private static readonly string[] SignInFlags = ["--header", OauthClientInfoFlag, "--static-oauth-client-metadata"];

    /// <summary>A connector reached by URL alone: its URL and headers.</summary>
    private static (List<Field> Fields, HashSet<string> Shown) UrlView(JsonValue config, string address, Exposure exposure)
    {
        var fields = new List<Field>
        {
            new(TypeLabel, [(Raw(JsonValue.Null), ConnectorTarget.RemoteType)]),
            new(UrlLabel, [(Raw(JsonValue.String(address)), Single(Url(address, exposure != Exposure.Masked), exposure))]),
        };
        var shown = new HashSet<string>(StringComparer.Ordinal) { "url" };
        if (config["headers"] is { Kind: JsonKind.Object } headers && headers.ObjectProperties.Count > 0)
        {
            fields.Add(SecretField(HeadersLabel, headers.ObjectProperties, exposure));
            shown.Add("headers");
        }
        return (fields, shown);
    }

    /// <summary>Anything else: its command, arguments and environment.</summary>
    private static (List<Field> Fields, HashSet<string> Shown) LocalView(JsonValue config, Exposure exposure)
    {
        var model = FormMapper.Analyze(config).Model;
        var fields = new List<Field>
        {
            new(CommandLabel, [(Raw(config["command"] ?? JsonValue.Null), exposure == Exposure.Masked ? Argument(model.Command) : Single(model.Command, exposure))]),
        };
        var shown = new HashSet<string>(StringComparer.Ordinal);
        if (config["command"] is { Kind: JsonKind.String })
        {
            shown.Add("command");
        }
        if (model.Args.Count > 0)
        {
            fields.Add(new Field(ArgsLabel, [(Raw(JsonValue.Array(model.Args.Select(JsonValue.String))),
                ControlText.Words(Arguments(model.Args, exposure)))]));
        }
        if (config["args"] is { Kind: JsonKind.Array } items && items.ArrayItems.Length > 0 && items.ArrayItems.Length == model.Args.Count)
        {
            shown.Add("args");
        }
        if (config["env"] is { Kind: JsonKind.Object } env && env.ObjectProperties.Count > 0)
        {
            fields.Add(SecretField(EnvLabel, env.ObjectProperties, exposure, shownInDialog: true));
            shown.Add("env");
        }
        return (fields, shown);
    }

    /// <summary>Environment variables or headers by name, each value as <see cref="SecretText"/> shows it; one that is
    /// not a string is set, and shows as JSON only when revealed. The dialog shows a value of
    /// <paramref name="shownInDialog"/> values - the environment's, since a variable can change what runs
    /// (<c>NODE_OPTIONS</c>, <c>PATH</c>, <c>LD_PRELOAD</c>) - as written, a string quoted as an argument is and
    /// anything else as JSON, unless its variable is named for a secret and names no program to
    /// run; a header's stays masked.</summary>
    private static Field SecretField(string label, IReadOnlyDictionary<string, JsonValue> values, Exposure exposure,
                                     bool shownInDialog = false) =>
        new(label, values.Keys.Order(StringComparer.Ordinal).Select(name =>
        {
            var value = values[name];
            var shown = exposure == Exposure.Revealed ? CompactText(value) : SetText;
            if (value.Kind == JsonKind.String)
            {
                shown = SecretText(value.StringValue, exposure == Exposure.Revealed);
            }
            // A variable that names code to run shows although its name reads as a secret's
            // (`GIT_ASKPASS` holds "pass"): the person allowing must see what would run.
            if (exposure == Exposure.Dialog && shownInDialog
                && (!CredentialHeuristics.NamesASecret(name) || CredentialHeuristics.NamesAProgramToRun(name)))
            {
                shown = CompactText(value);
                if (value.Kind == JsonKind.String)
                {
                    shown = ControlText.Word(value.StringValue);
                }
            }
            return (Raw(JsonValue.Array([JsonValue.String(name), value])), Assignment(Single(name, exposure), shown));
        }).ToList());

    /// <summary>The strings of an array value; nothing else.</summary>
    private static List<string> Strings(JsonValue? value) =>
        value is { Kind: JsonKind.Array } ? value.ArrayItems.Where(item => item.Kind == JsonKind.String).Select(item => item.StringValue).ToList() : [];
}
