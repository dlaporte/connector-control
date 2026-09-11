using System.Text.Json;

namespace ConnectorControl.Core;

/// <summary>
/// Recognizes the <c>npx [-y] mcp-remote[@version] &lt;url&gt;</c> bridge pattern (and
/// its Windows spelling <c>cmd /c npx …</c>) so the form view can show just Name + Server URL.
/// </summary>
public static class RemotePattern
{
    /// <summary>The bridge package as this app writes it when the user has not pinned a version.</summary>
    public const string DefaultPackage = "mcp-remote";

    /// <summary>True when <paramref name="s"/> is the mcp-remote package specifier, with or without a version tag.</summary>
    internal static bool IsMarker(string s) => s == DefaultPackage || s.StartsWith(DefaultPackage + "@", StringComparison.Ordinal);

    /// <summary>Swift's <c>URL(string:)</c> + http(s) scheme + non-empty host.</summary>
    public static bool IsValidHttpUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.Host.Length > 0;

    /// <summary>
    /// Characters cmd.exe acts on wherever they appear on its command line — separators, redirections,
    /// its escape and its quote. Claude Desktop starts the <c>cmd /c npx …</c> shape through cross-spawn,
    /// which escapes arguments only for batch-file commands; cmd.exe itself is an .exe, so every argument
    /// reaches it verbatim and a URL such as <c>https://host/mcp&amp;calc</c> runs <c>calc</c>. Bare
    /// <c>npx</c> resolves to npx.cmd, which cross-spawn wraps and caret-escapes itself, so the Npx
    /// style needs no such rule.
    /// </summary>
    public const string CmdUnsafeCharacters = "&|<>^\"";

    /// <summary>
    /// The first character of <paramref name="value"/> cmd.exe would act on — one of
    /// <see cref="CmdUnsafeCharacters"/>, or whitespace unless <paramref name="allowWhitespace"/> — or null.
    /// </summary>
    public static char? CmdUnsafeCharacter(string value, bool allowWhitespace = false)
    {
        foreach (var c in value)
        {
            if (CmdUnsafeCharacters.Contains(c) || (!allowWhitespace && char.IsWhiteSpace(c)))
            {
                return c;
            }
        }
        return null;
    }

    /// <summary>
    /// True when cmd.exe could expand part of <paramref name="value"/> as a <c>%NAME%</c> variable:
    /// two or more percent signs. A lone <c>%20</c> is left alone; <c>%41x%42</c> is not.
    /// </summary>
    public static bool HasCmdExpansionRisk(string value) => value.Count(c => c == '%') >= 2;

    /// <summary>
    /// The field of <paramref name="r"/> cmd.exe would re-parse under the <see cref="RemoteLaunchStyle.CmdNpx"/>
    /// launcher, or null. Only fields <see cref="Encode"/> places in <c>args</c> count: the URL, a header NAME,
    /// and the OAuth client JSON; bearer tokens and header VALUES travel in env, which cmd.exe never parses.
    /// Scopes are space-separated by definition, so whitespace is allowed there alone.
    /// </summary>
    public static RemoteField? CmdUnsafeField(RemoteConfig r)
    {
        if (r.LaunchStyle != RemoteLaunchStyle.CmdNpx)
        {
            return null;
        }
        if (CmdUnsafeCharacter(r.Url) is not null)
        {
            return RemoteField.Url;
        }
        return r.Auth switch
        {
            RemoteAuth.Header h when CmdUnsafeCharacter(h.Name) is not null => RemoteField.HeaderName,
            RemoteAuth.OAuthClient o when CmdUnsafeCharacter(o.ClientId) is not null => RemoteField.ClientId,
            RemoteAuth.OAuthClient o when CmdUnsafeCharacter(o.ClientSecret) is not null => RemoteField.ClientSecret,
            RemoteAuth.OAuthClient o when CmdUnsafeCharacter(o.Scopes, allowWhitespace: true) is not null => RemoteField.Scopes,
            _ => null,
        };
    }

    /// <summary>
    /// Strips the launcher — <c>npx</c> or <c>cmd /c npx</c> — and returns the style
    /// plus the remaining args (before "-y" handling). Null when the config is not an
    /// all-string-args npx invocation.
    /// </summary>
    internal static (RemoteLaunchStyle Style, List<string> Args)? LauncherArgs(JsonValue config)
    {
        // A missing/invalid args array degrades to empty rather than failing the read; every
        // caller below already treats an empty list the same as "not recognized".
        if (!CommandLine.TryRead(config, out var cmd, out var rawArgs))
        {
            return null;
        }
        var args = rawArgs.ToList();
        if (cmd == "npx")
        {
            return (RemoteLaunchStyle.Npx, args);
        }
        bool isCmd = cmd.Equals("cmd", StringComparison.OrdinalIgnoreCase) || cmd.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);
        if (isCmd && args.Count >= 2 && args[0].Equals("/c", StringComparison.OrdinalIgnoreCase) && args[1] == "npx")
        {
            return (RemoteLaunchStyle.CmdNpx, args.Skip(2).ToList());
        }
        return null;
    }

    /// <summary>Launcher args with a leading "-y" stripped (Swift <c>strippedArgs</c>).</summary>
    private static List<string>? StrippedArgs(JsonValue config)
    {
        var launcher = LauncherArgs(config);
        if (launcher is null)
        {
            return null;
        }
        var args = launcher.Value.Args;
        if (args.Count > 0 && args[0] == "-y")
        {
            args.RemoveAt(0);
        }
        return args;
    }

    public static string? Detect(JsonValue config)
    {
        var args = StrippedArgs(config);
        if (args is null || args.Count != 2 || !IsMarker(args[0]) || !IsValidHttpUrl(args[1]))
        {
            return null;
        }
        return args[1];
    }

    public static JsonValue Make(string url, RemoteLaunchStyle style) =>
        BuildConfig(style, ["-y", DefaultPackage, url], null);

    /// <summary>An mcp-remote invocation regardless of URL validity.</summary>
    public static bool IsRemoteShaped(JsonValue config)
    {
        var args = StrippedArgs(config);
        return args is { Count: > 0 } && IsMarker(args[0]);
    }

    /// <summary>A BARE bridge invocation with at most one trailing argument (the URL slot).</summary>
    public static bool IsCanonicalShape(JsonValue config)
    {
        var args = StrippedArgs(config);
        return args is { Count: > 0 and <= 2 } && IsMarker(args[0]);
    }

    private static JsonValue BuildConfig(RemoteLaunchStyle style, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env)
    {
        var props = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        if (style == RemoteLaunchStyle.CmdNpx)
        {
            props["command"] = JsonValue.String("cmd");
            props["args"] = JsonValue.Array(new[] { "/c", "npx" }.Concat(args).Select(JsonValue.String));
        }
        else
        {
            props["command"] = JsonValue.String("npx");
            props["args"] = JsonValue.Array(args.Select(JsonValue.String));
        }
        if (env is { Count: > 0 })
        {
            props["env"] = JsonValue.FromObject(env);
        }
        return JsonValue.Object(props);
    }

    /// <summary>Builds the full mcp-remote config for a <see cref="RemoteConfig"/>, encoding auth into the flags/env mcp-remote expects.</summary>
    public static JsonValue Encode(RemoteConfig r)
    {
        var args = new List<string> { "-y", r.Package, r.Url };
        args.AddRange(r.ExtraArgs);
        var env = new Dictionary<string, string>(r.PassthroughEnv, StringComparer.Ordinal);
        switch (r.Auth)
        {
            case RemoteAuth.Automatic:
                break;
            case RemoteAuth.Bearer b:
                // Space-safe: no space around the colon in the arg; the space lives in the env value.
                args.AddRange(["--header", "Authorization:${AUTH_HEADER}"]);
                env["AUTH_HEADER"] = "Bearer " + b.Token;
                break;
            case RemoteAuth.Header h:
                args.AddRange(["--header", $"{h.Name}:${{AUTH_HEADER}}"]);
                env["AUTH_HEADER"] = h.Value;
                break;
            case RemoteAuth.OAuthClient o:
                // Literal id/secret in the arg: mcp-remote reads client info from this flag directly.
                args.AddRange(["--static-oauth-client-info", CompactJson(("client_id", o.ClientId), ("client_secret", o.ClientSecret))]);
                if (o.Scopes.Length > 0)
                {
                    args.AddRange(["--static-oauth-client-metadata", CompactJson(("scope", o.Scopes))]);
                }
                break;
        }
        return BuildConfig(r.LaunchStyle, args, env);
    }

    /// <summary>Reads an mcp-remote config back into a <see cref="RemoteConfig"/>, or null when it isn't one.</summary>
    public static RemoteConfig? Decode(JsonValue config)
    {
        var launcher = LauncherArgs(config);
        if (launcher is null)
        {
            return null;
        }
        var (style, args) = launcher.Value;
        int i = 0;
        if (args.Count > 0 && args[0] == "-y")
        {
            i = 1;
        }
        if (i >= args.Count || !IsMarker(args[i]))
        {
            return null;
        }
        var package = args[i];
        i++;
        if (i >= args.Count)
        {
            return null;
        }
        var urlString = args[i];
        if (!IsValidHttpUrl(urlString))
        {
            return null;
        }
        i++;

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config["env"] is { Kind: JsonKind.Object } envObject)
        {
            foreach (var (key, value) in envObject.ObjectProperties)
            {
                if (value.Kind == JsonKind.String)
                {
                    env[key] = value.StringValue;
                }
            }
        }

        var extraArgs = new List<string>();
        var consumedEnvKeys = new HashSet<string>(StringComparer.Ordinal);
        string? headerName = null, headerValue = null, clientId = null, clientSecret = null, scopes = null;

        while (i < args.Count)
        {
            var arg = args[i];
            if (arg == "--header" && i + 1 < args.Count)
            {
                var raw = args[i + 1];
                int colon = raw.IndexOf(':');
                // Recognize ONLY this app's own indirection sentinel — `Name:${AUTH_HEADER}`
                // backed by an AUTH_HEADER env var — and only the first one.
                if (headerName is null && colon >= 0 && raw[(colon + 1)..] == "${AUTH_HEADER}" && env.TryGetValue("AUTH_HEADER", out var value))
                {
                    headerName = raw[..colon];
                    headerValue = value;
                    consumedEnvKeys.Add("AUTH_HEADER");
                }
                else
                {
                    extraArgs.Add(arg);
                    extraArgs.Add(raw);
                }
                i += 2;
            }
            else if (arg == "--static-oauth-client-info" && i + 1 < args.Count)
            {
                var obj = ParseJsonObject(args[i + 1]);
                if (obj is not null)
                {
                    clientId = obj["client_id"] is { Kind: JsonKind.String } cid ? cid.StringValue : null;
                    clientSecret = obj["client_secret"] is { Kind: JsonKind.String } cs ? cs.StringValue : null;
                }
                i += 2;
            }
            else if (arg == "--static-oauth-client-metadata" && i + 1 < args.Count)
            {
                var obj = ParseJsonObject(args[i + 1]);
                if (obj is not null)
                {
                    scopes = obj["scope"] is { Kind: JsonKind.String } sc ? sc.StringValue : null;
                }
                i += 2;
            }
            else
            {
                extraArgs.Add(arg);
                i++;
            }
        }

        RemoteAuth auth = RemoteAuth.Auto;
        if (clientId is not null)
        {
            auth = new RemoteAuth.OAuthClient(clientId, clientSecret ?? "", scopes ?? "");
        }
        else if (headerName is not null && headerValue is not null)
        {
            auth = headerName == "Authorization" && headerValue.StartsWith("Bearer ", StringComparison.Ordinal)
                ? new RemoteAuth.Bearer(headerValue["Bearer ".Length..])
                : new RemoteAuth.Header(headerName, headerValue);
        }
        foreach (var key in consumedEnvKeys)
        {
            env.Remove(key);
        }
        return new RemoteConfig(urlString, auth, style, extraArgs, env, package);
    }

    /// <summary>Compact, key-sorted JSON — what mcp-remote expects in a single CLI argument.</summary>
    private static string CompactJson(params (string Key, string Value)[] pairs) =>
        AppleJsonWriter.Write(
            JsonValue.Object(pairs.Select(p => (p.Key, JsonValue.String(p.Value))).ToArray()),
            AppleJsonFormat.SerializationCompact);

    private static JsonValue? ParseJsonObject(string text)
    {
        try
        {
            var value = JsonValue.Parse(text);
            return value.Kind == JsonKind.Object ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
