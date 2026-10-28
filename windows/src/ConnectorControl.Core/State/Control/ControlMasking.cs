using System.Text;
using System.Text.Json;

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
/// parser, so both platforms mask the same text the same way. Structured output is masked or, for
/// the command-line tool's <c>show --reveal</c> (never Claude's), revealed; the line renderers take an
/// <see cref="Exposure"/>, which adds the confirmation dialog's.
/// The Mac mirror is main-actor only; C# has no isolation to declare.
///
/// Mirror: Sources/ConnectorControlState/Control/ControlMasking.swift
/// </summary>
public static class ControlMasking
{
    /// <summary>How much a line renderer shows. <c>Masked</c>: what Claude and the command-line tool see.
    /// <c>Dialog</c>: the confirmation dialogs, whose text never goes back to Claude — the command,
    /// arguments and URL in full, with environment and header values, the OAuth client secret and
    /// every value under a key named for a secret still masked. <c>Revealed</c>: everything as written,
    /// for the command-line tool's <c>show --reveal</c>.</summary>
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
        return args.Select((arg, index) =>
        {
            if (index > 0)
            {
                var previous = args[index - 1];
                if (previous == "--header" || previous == "-H")
                {
                    return Header(arg);
                }
                if (HidesNext(previous, exposure) && !IsFlag(arg) && !RefersElsewhere(arg))
                {
                    return exposure == Exposure.Dialog ? ClientInfo(arg) : Mask;
                }
            }
            return exposure == Exposure.Masked ? Argument(arg) : DialogArgument(arg);
        }).ToList();
    }

    /// <summary>One argument as the dialog shows it: as written, but for the value of
    /// <c>--static-oauth-client-info=…</c>, which holds the client secret.</summary>
    internal static string DialogArgument(string arg)
    {
        if (Cut(arg, "=") is not { } piece || !HidesNext(piece.Before, Exposure.Dialog) || RefersElsewhere(piece.After))
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

    /// <summary>Whether a key's values are secrets: <c>env</c> and <c>headers</c> wherever they sit, and any key named
    /// for a secret.</summary>
    internal static bool NamesASecret(string key) =>
        SecretKeys.Contains(key) || CredentialHeuristics.NamesASecret(key);

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
            if (SecretKeys.Contains(key) && value.Kind == JsonKind.Object)
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
                new Field(key, [(Raw(JsonValue.Array([JsonValue.String(key), properties[key]])), ValueText(key, properties[key], exposure))]))).ToList();
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
            return exposure == Exposure.Masked ? Argument(value.StringValue) : value.StringValue;
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
            new(UrlLabel, [(Raw(JsonValue.String(remote.Url)), Url(remote.Url, exposure != Exposure.Masked))]),
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
                    [(Raw(JsonValue.Array([JsonValue.String(header.Name), JsonValue.String(header.Value)])), Assignment(header.Name, SecretText(header.Value, reveal)))]));
                break;
            case RemoteAuth.OAuthClient client:
                fields.Add(new Field(OauthLabel,
                    [(Raw(JsonValue.Array([JsonValue.String(client.ClientId), JsonValue.String(client.ClientSecret)])),
                        OauthClientText(client.ClientId, SecretText(client.ClientSecret, reveal)))]));
                if (client.Scopes.Length > 0)
                {
                    fields.Add(new Field(ScopesLabel, [(Raw(JsonValue.String(client.Scopes)), client.Scopes)]));
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
                fields.Add(SecretField(EnvLabel, passthrough, exposure));
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
            new(TypeLabel, [(Raw(JsonValue.Null), CollectionsModel.RemoteType)]),
            new(UrlLabel, [(Raw(JsonValue.String(address)), Url(address, exposure != Exposure.Masked))]),
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
            new(CommandLabel, [(Raw(config["command"] ?? JsonValue.Null), exposure == Exposure.Masked ? Argument(model.Command) : model.Command)]),
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
            fields.Add(SecretField(EnvLabel, env.ObjectProperties, exposure));
            shown.Add("env");
        }
        return (fields, shown);
    }

    /// <summary>Environment variables or headers by name, each value as <see cref="SecretText"/> shows it; one that is
    /// not a string is set, and shows as JSON only when revealed.</summary>
    private static Field SecretField(string label, IReadOnlyDictionary<string, JsonValue> values, Exposure exposure) =>
        new(label, values.Keys.Order(StringComparer.Ordinal).Select(name =>
        {
            var value = values[name];
            var shown = exposure == Exposure.Revealed ? CompactText(value) : SetText;
            if (value.Kind == JsonKind.String)
            {
                shown = SecretText(value.StringValue, exposure == Exposure.Revealed);
            }
            return (Raw(JsonValue.Array([JsonValue.String(name), value])), Assignment(name, shown));
        }).ToList());

    /// <summary>The strings of an array value; nothing else.</summary>
    private static List<string> Strings(JsonValue? value) =>
        value is { Kind: JsonKind.Array } ? value.ArrayItems.Where(item => item.Kind == JsonKind.String).Select(item => item.StringValue).ToList() : [];
}
