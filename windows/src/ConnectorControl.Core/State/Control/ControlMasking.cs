namespace ConnectorControl.Core.State;

/// <summary>
/// What a connector looks like to anyone but its owner: the command line and URL in full, with
/// every secret value replaced. Environment and header values are never shown, only whether they
/// are set; an argument that reads as a credential, the value after a flag named for a secret, a
/// header's value and a URL's password and query values read "•••". Placeholders — <c>${CC_NEEDS:…}</c>
/// and <c>${COLLECTION_DIR}</c> — name what is missing rather than what is secret, and show as written.
/// Every rule is plain string work, with no URL parser, so both platforms mask the same text the
/// same way. <c>reveal</c> (the command-line tool's <c>show --reveal</c>, never Claude's) turns it all off.
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

    /// <summary>A placeholder names what is missing, never what is secret.</summary>
    internal static bool IsPlaceholder(string value) =>
        Placeholder.ContainsMarker(value) || value.Contains(Placeholder.DirectoryToken, StringComparison.Ordinal);

    /// <summary>A value that takes its secret from elsewhere — a placeholder, or a <c>${NAME}</c> / <c>$NAME</c>
    /// reference — holds none itself.</summary>
    internal static bool RefersElsewhere(string value) =>
        IsPlaceholder(value) || CredentialHeuristics.IsReference(value);

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

    /// <summary><c>text</c> with a URL's password and every query value replaced by "•••"; anything that is not
    /// <c>scheme://…</c> comes back as it is, and so does a value that is a placeholder or a reference.
    /// The fragment is kept as written.</summary>
    public static string Url(string text, bool reveal = false)
    {
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (reveal || schemeEnd < 0)
        {
            return text;
        }
        var afterScheme = schemeEnd + "://".Length;
        var rest = text[afterScheme..];
        var fragment = "";
        var hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            fragment = rest[hash..];
            rest = rest[..hash];
        }
        string? query = null;
        var question = rest.IndexOf('?');
        if (question >= 0)
        {
            query = rest[(question + 1)..];
            rest = rest[..question];
        }
        var slash = rest.IndexOf('/');
        if (slash < 0)
        {
            slash = rest.Length;
        }
        var authority = rest[..slash];
        var path = rest[slash..];
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            var userInfo = authority[..at];
            var colon = userInfo.IndexOf(':');
            if (colon >= 0)
            {
                var password = userInfo[(colon + 1)..];
                if (password.Length > 0 && !RefersElsewhere(password))
                {
                    authority = userInfo[..(colon + 1)] + Mask + authority[at..];
                }
            }
        }
        var result = text[..afterScheme] + authority + path;
        if (query is not null)
        {
            var pairs = query.Split('&').Select(pair =>
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2)
                {
                    var value = parts[1];
                    return value.Length == 0 || RefersElsewhere(value) ? pair : parts[0] + "=" + Mask;
                }
                return CredentialHeuristics.LooksLikeCredential(pair) || CredentialHeuristics.LooksLikeRandomToken(pair) ? Mask : pair;
            });
            result += "?" + string.Join("&", pairs);
        }
        return result + fragment;
    }

    /// <summary>One argument on its own: a placeholder as written, a URL through <see cref="Url"/>, <c>--flag=value</c>
    /// with the value masked when the flag is named for a secret, and "•••" for anything that
    /// reads as a credential or a random token.</summary>
    public static string Argument(string arg, bool reveal = false)
    {
        if (reveal || IsPlaceholder(arg))
        {
            return arg;
        }
        if (arg.Contains("://", StringComparison.Ordinal))
        {
            return Url(arg);
        }
        var equals = arg.IndexOf('=');
        if (arg.StartsWith('-') && equals >= 0)
        {
            var flag = arg[..equals];
            var value = arg[(equals + 1)..];
            if (CredentialHeuristics.NamesASecret(flag) && !RefersElsewhere(value))
            {
                return flag + "=" + Mask;
            }
            return flag + "=" + Argument(value);
        }
        if (CredentialHeuristics.LooksLikeCredential(arg) || CredentialHeuristics.LooksLikeRandomToken(arg))
        {
            return Mask;
        }
        return arg;
    }

    /// <summary>An argument list: each through <see cref="Argument"/>, and also the value after a flag named for a
    /// secret (<c>--api-key x</c>, <c>--static-oauth-client-info {…}</c>) and a header after <c>--header</c>.</summary>
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
                if (previous.StartsWith('-') && !previous.Contains('=') && CredentialHeuristics.NamesASecret(previous)
                    && !RefersElsewhere(arg))
                {
                    return Mask;
                }
            }
            return Argument(arg);
        }).ToList();
    }

    /// <summary>"Name: value" with the value masked unless it refers elsewhere.</summary>
    internal static string Header(string text)
    {
        var colon = text.IndexOf(':');
        if (colon < 0)
        {
            return Argument(text);
        }
        var value = text[(colon + 1)..].TrimSpaces();
        if (value.Length == 0 || RefersElsewhere(value))
        {
            return text;
        }
        return text[..(colon + 1)] + " " + Mask;
    }

    // MARK: Whole configs

    /// <summary>The config as structured output carries it, in its own shape: <c>env</c> and <c>headers</c> values
    /// become "set" or "needs value", <c>args</c> go through <see cref="Arguments"/>, <c>url</c> through <see cref="Url"/>,
    /// and every other string through <see cref="Argument"/>.</summary>
    public static JsonValue MaskedConfig(JsonValue config, bool reveal = false)
    {
        if (reveal || config.Kind != JsonKind.Object)
        {
            return config;
        }
        var result = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var (key, value) in config.ObjectProperties)
        {
            if ((key == "env" || key == "headers") && value.Kind == JsonKind.Object)
            {
                result[key] = JsonValue.Object(value.ObjectProperties.Select(item => new KeyValuePair<string, JsonValue>(
                    item.Key, JsonValue.String(item.Value.Kind == JsonKind.String ? SecretState(item.Value.StringValue) : SetState))));
            }
            else if (key == "args" && value.Kind == JsonKind.Array)
            {
                var items = value.ArrayItems;
                result[key] = items.All(item => item.Kind == JsonKind.String)
                    ? JsonValue.Array(Arguments(items.Select(item => item.StringValue).ToList()).Select(JsonValue.String))
                    : JsonValue.Array(items.Select(MaskedLeaves));
            }
            else if (key == "url" && value.Kind == JsonKind.String)
            {
                result[key] = JsonValue.String(Url(value.StringValue));
            }
            else
            {
                result[key] = MaskedLeaves(value);
            }
        }
        return JsonValue.Object(result);
    }

    internal static JsonValue MaskedLeaves(JsonValue value) => value.Kind switch
    {
        JsonKind.String => JsonValue.String(Argument(value.StringValue)),
        JsonKind.Array => JsonValue.Array(value.ArrayItems.Select(MaskedLeaves)),
        JsonKind.Object => JsonValue.Object(value.ObjectProperties.Select(item =>
            new KeyValuePair<string, JsonValue>(item.Key, MaskedLeaves(item.Value)))),
        _ => value,
    };

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
