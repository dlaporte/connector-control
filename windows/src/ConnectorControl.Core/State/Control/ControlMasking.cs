using System.Text;

namespace ConnectorControl.Core.State;

/// <summary>
/// What a connector looks like to anyone but its owner: the command line and URL in full, with
/// every secret value replaced. Environment and header values are never shown, only whether they
/// are set, and nor is a value under a key named for a secret; an argument that reads as a
/// credential, the value after a flag named for a secret, a <c>NAME=value</c> word's value when the
/// name is a secret's, a header's value, a URL's password and query and fragment values, and a
/// URL's user name or path segment that reads as a credential, read "•••". Placeholders —
/// <c>${CC_NEEDS:…}</c> and <c>${COLLECTION_DIR}</c> — name what is missing rather than what is secret, and
/// show as written when they are the whole value. Every rule is plain string work, with no URL
/// parser, so both platforms mask the same text the same way. <c>reveal</c> (the command-line tool's
/// <c>show --reveal</c>, never Claude's) turns it all off.
/// The Mac mirror is main-actor only; C# has no isolation to declare.
///
/// Mirror: Sources/ConnectorControlState/Control/ControlMasking.swift
/// </summary>
public static class ControlMasking
{
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

    // MARK: Values

    /// <summary>A value that is wholly a reference — a placeholder, <c>${COLLECTION_DIR}</c>, or a <c>${NAME}</c> /
    /// <c>$NAME</c> — takes its secret from elsewhere and holds none itself. One with a placeholder
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
        var rest = scheme.After;
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
        var result = scheme.Before + "://" + authority;
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

    /// <summary>One argument on its own: a URL through <see cref="Url"/>, a value that is wholly a reference as
    /// written, <c>name=value</c> with the value masked when the name is a secret's (<c>--api-key=…</c>,
    /// Docker's <c>-e API_KEY=…</c>), and "•••" for anything that reads as a credential or a random
    /// token — a "name" that does is part of the token, so the whole word is masked.</summary>
    public static string Argument(string arg, bool reveal = false)
    {
        if (reveal)
        {
            return arg;
        }
        if (Cut(arg, "://") is not null)
        {
            return Url(arg);
        }
        if (RefersElsewhere(arg))
        {
            return arg;
        }
        if (Cut(arg, "=") is { } piece && !LooksSecret(piece.Before))
        {
            if (CredentialHeuristics.NamesASecret(piece.Before) && !RefersElsewhere(piece.After))
            {
                return piece.Before + "=" + Mask;
            }
            return piece.Before + "=" + Argument(piece.After);
        }
        return LooksSecret(arg) ? Mask : arg;
    }

    /// <summary>An argument list: each through <see cref="Argument"/>, and also the value after a flag named for a
    /// secret (<c>--api-key x</c>, <c>--static-oauth-client-info {…}</c>) unless it is itself a flag, and a
    /// header after <c>--header</c> or <c>-H</c>.</summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string> args, bool reveal = false)
    {
        if (reveal)
        {
            return args;
        }
        return args.Select((arg, index) =>
        {
            if (index > 0)
            {
                var previous = args[index - 1];
                if (previous == "--header" || previous == "-H")
                {
                    return Header(arg);
                }
                if (IsFlag(previous) && Cut(previous, "=") is null && CredentialHeuristics.NamesASecret(previous)
                    && !IsFlag(arg) && !RefersElsewhere(arg))
                {
                    return Mask;
                }
            }
            return Argument(arg);
        }).ToList();
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

    /// <summary>The keys whose values are secrets by name: every value under them is.</summary>
    internal static readonly IReadOnlySet<string> SecretKeys = new HashSet<string>(StringComparer.Ordinal) { "env", "headers" };

    /// <summary>The config as structured output carries it, in its own shape: <c>env</c> and <c>headers</c> values
    /// become "set" or "needs value", and so does every string or number under a key named for a
    /// secret; an array is an argument list through <see cref="Arguments"/>, and every other string goes
    /// through <see cref="Argument"/>, a URL through <see cref="Url"/>.</summary>
    public static JsonValue MaskedConfig(JsonValue config, bool reveal = false)
    {
        if (reveal)
        {
            return config;
        }
        if (config.Kind != JsonKind.Object)
        {
            return MaskedLeaves(config, secret: false);
        }
        var result = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var (key, value) in config.ObjectProperties)
        {
            if (SecretKeys.Contains(key) && value.Kind == JsonKind.Object)
            {
                result[key] = JsonValue.Object(value.ObjectProperties.Select(item => new KeyValuePair<string, JsonValue>(
                    item.Key, JsonValue.String(item.Value.Kind == JsonKind.String ? SecretState(item.Value.StringValue) : SetState))));
            }
            else
            {
                result[key] = MaskedLeaves(value, secret: SecretKeys.Contains(key) || CredentialHeuristics.NamesASecret(key));
            }
        }
        return JsonValue.Object(result);
    }

    /// <summary><c>value</c> masked: under a key named for a secret (<c>secret</c>) every string or number reads
    /// "set" or "needs value"; elsewhere an array is an argument list, its strings masked as
    /// <see cref="Arguments"/> masks them, and every other string goes through <see cref="Argument"/>.</summary>
    internal static JsonValue MaskedLeaves(JsonValue value, bool secret)
    {
        switch (value.Kind)
        {
            case JsonKind.String:
                return JsonValue.String(secret ? SecretState(value.StringValue) : Argument(value.StringValue));
            case JsonKind.Int:
            case JsonKind.Double:
                return secret ? JsonValue.String(SetState) : value;
            case JsonKind.Array:
                if (secret)
                {
                    return JsonValue.Array(value.ArrayItems.Select(item => MaskedLeaves(item, secret: true)));
                }
                var shown = Arguments(value.ArrayItems.Select(item => item.Kind == JsonKind.String ? item.StringValue : "").ToList());
                return JsonValue.Array(value.ArrayItems.Zip(shown, (item, text) =>
                    item.Kind == JsonKind.String ? JsonValue.String(text) : MaskedLeaves(item, secret: false)));
            case JsonKind.Object:
                return JsonValue.Object(value.ObjectProperties.Select(item => new KeyValuePair<string, JsonValue>(
                    item.Key, MaskedLeaves(item.Value, secret || CredentialHeuristics.NamesASecret(item.Key)))));
            default:
                return value;
        }
    }

    /// <summary>One field of a config as a person reads it: its label, and each value as written (<c>Raw</c>,
    /// which tells a changed secret from an unchanged one) and as shown.</summary>
    private sealed record Field(string Label, List<(string Raw, string Shown)> Values);

    /// <summary>What a connector runs, one field per line, for <c>get_connector</c>'s text and the confirmation
    /// dialogs: a connector reached through mcp-remote by its URL and how it signs in; one reached
    /// by URL alone by its URL and headers; anything else by its command, arguments and environment.</summary>
    public static IReadOnlyList<string> CodeLines(JsonValue config, bool reveal = false) =>
        ControlText.Labelled(Fields(config, reveal).SelectMany(field =>
            field.Values.Select((value, index) => (index == 0 ? field.Label : "", value.Shown))).ToList());

    /// <summary>The fields an edit changes, for the change confirmation: each changed field's label, then
    /// "− " before each value it loses and "+ " before each it gains. A secret that changed shows
    /// as changed although both sides read "••• (set)".</summary>
    public static IReadOnlyList<string> ChangeLines(JsonValue before, JsonValue after)
    {
        var old = Fields(before, false);
        var @new = Fields(after, false);
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
            var was = old.FirstOrDefault(field => field.Label == label)?.Values ?? [];
            var now = @new.FirstOrDefault(field => field.Label == label)?.Values ?? [];
            var lost = was.Where(item => !now.Any(other => other.Raw == item.Raw && other.Shown == item.Shown)).ToList();
            var gained = now.Where(item => !was.Any(other => other.Raw == item.Raw && other.Shown == item.Shown)).ToList();
            if (lost.Count == 0 && gained.Count == 0)
            {
                continue;
            }
            lines.Add(label);
            lines.AddRange(lost.Select(item => RemovedPrefix + item.Shown));
            lines.AddRange(gained.Select(item => AddedPrefix + item.Shown));
        }
        return lines;
    }

    private static List<Field> Fields(JsonValue config, bool reveal)
    {
        // Decodes with RemotePattern.Decode, which also reads `cmd /c npx …`, so a Windows connector shows the same lines.
        if (RemotePattern.Decode(config) is { } remote)
        {
            var fields = new List<Field>
            {
                new(TypeLabel, [(remote.Package, RemoteBridgeType(remote.Package))]),
                new(UrlLabel, [(remote.Url, Url(remote.Url, reveal))]),
            };
            switch (remote.Auth)
            {
                case RemoteAuth.Bearer bearer:
                    fields.Add(new Field(HeadersLabel, [(bearer.Token, Assignment("Authorization", SecretText(bearer.Token, reveal)))]));
                    break;
                case RemoteAuth.Header header:
                    fields.Add(new Field(HeadersLabel, [(header.Name + ":" + header.Value, Assignment(header.Name, SecretText(header.Value, reveal)))]));
                    break;
                case RemoteAuth.OAuthClient client:
                    fields.Add(new Field(OauthLabel, [(client.ClientId + ":" + client.ClientSecret,
                        OauthClientText(client.ClientId, SecretText(client.ClientSecret, reveal)))]));
                    if (client.Scopes.Length > 0)
                    {
                        fields.Add(new Field(ScopesLabel, [(client.Scopes, client.Scopes)]));
                    }
                    break;
            }
            if (remote.ExtraArgs.Count > 0)
            {
                fields.Add(new Field(ArgsLabel, [(string.Join(" ", remote.ExtraArgs), string.Join(" ", Arguments(remote.ExtraArgs, reveal)))]));
            }
            return fields.Concat(EnvFields(remote.PassthroughEnv, reveal)).ToList();
        }
        if (config.Kind != JsonKind.Object)
        {
            return [];
        }
        var properties = config.ObjectProperties;
        if (!properties.ContainsKey("command") && properties.TryGetValue("url", out var urlValue) && urlValue.Kind == JsonKind.String)
        {
            var address = urlValue.StringValue;
            var fields = new List<Field>
            {
                new(TypeLabel, [("", CollectionsModel.RemoteType)]),
                new(UrlLabel, [(address, Url(address, reveal))]),
            };
            if (properties.TryGetValue("headers", out var headersValue) && headersValue.Kind == JsonKind.Object)
            {
                var headers = headersValue.ObjectProperties;
                var values = headers.Keys.Order(StringComparer.Ordinal).Select(name =>
                {
                    var text = headers[name].Kind == JsonKind.String ? headers[name].StringValue : "";
                    return (Raw: name + ":" + text, Shown: Assignment(name, SecretText(text, reveal)));
                }).ToList();
                fields.Add(new Field(HeadersLabel, values));
            }
            return fields;
        }
        var model = FormMapper.Analyze(config).Model;
        var local = new List<Field> { new(CommandLabel, [(model.Command, Argument(model.Command, reveal))]) };
        if (model.Args.Count > 0)
        {
            local.Add(new Field(ArgsLabel, [(string.Join(" ", model.Args), string.Join(" ", Arguments(model.Args, reveal)))]));
        }
        local.AddRange(EnvFields(model.Env, reveal));
        if (model.Additional.Count > 0)
        {
            var keys = string.Join(", ", model.Additional.Keys.Order(StringComparer.Ordinal));
            local.Add(new Field(OtherLabel, [(keys, keys)]));
        }
        return local;
    }

    private static List<Field> EnvFields(IReadOnlyDictionary<string, string> env, bool reveal)
    {
        if (env.Count == 0)
        {
            return [];
        }
        var values = env.Keys.Order(StringComparer.Ordinal).Select(name =>
        {
            var value = env[name];
            return (Raw: name + "=" + value, Shown: Assignment(name, SecretText(value, reveal)));
        }).ToList();
        return [new Field(EnvLabel, values)];
    }
}
