namespace ConnectorControl.Core;

/// <summary>
/// What a connector runs, as a row or an answer to Claude shows it, never a secret: the one
/// allowlist that decides how much of a connector's command line may be shown, and the shell
/// reading of a command line that the control layer's masking shares. Pure, with no window or app
/// state, so the Collections window, the control layer and the Publish dialog all read it alike.
///
/// Mirror: Sources/ConnectorControlCore/ConnectorTarget.swift
/// </summary>
public static class ConnectorTarget
{
    /// <summary>What a remote connector shows when its host is in doubt, or when it has no URL to read.</summary>
    public const string RemoteType = "remote";

    /// <summary>
    /// What a row says the connector runs, never a secret: a remote connector's host; a local
    /// one's launcher and arguments, shortened. Public and pure so both platforms test the same
    /// inputs. <paramref name="home"/> is the user's home folder, abbreviated to "~".
    ///
    /// An allowlist, not a mask: an argument is shown only when it is a URL, an explicit path or
    /// a named artefact, and every other argument - a flag, a flag's value, <c>KEY=value</c>, a
    /// header, a shell string, a bare word - is left out. A list of secret shapes to hide would
    /// leak every shape it did not foresee. The one exception is backed by a name rather than a
    /// shape: whatever follows a flag named for a secret is left out too, since a password can
    /// look like a package.
    /// </summary>
    public static string TargetOf(JsonValue config, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // A connector Claude reaches by URL has no command to read: its URL's scheme and host, as a
        // URL argument shows them, or only that it is remote when that host is in doubt.
        if (config.Kind == JsonKind.Object && config["command"] is null
            && (config["url"] is not null || config["type"] == JsonValue.String("http") || config["type"] == JsonValue.String("sse")))
        {
            return config["url"] is { Kind: JsonKind.String } url && UrlOrigin(url.StringValue) is { } origin
                ? $"{origin.Scheme}://{origin.Host}"
                : RemoteType;
        }
        var model = FormMapper.Analyze(config).Model;
        var (command, commandArgs) = SplitCommandLine(model.Command, model.Args);
        // Windows' `cmd /c npx …`: what cmd runs is the launcher, and the rule applies to what
        // follows it.
        if (LauncherName(command).ToLowerInvariant() == "cmd" && commandArgs.Count > 0
            && commandArgs[0].ToLowerInvariant() is "/c" or "/k")
        {
            commandArgs.RemoveAt(0);
            if (commandArgs.Count > 0)
            {
                var inner = commandArgs[0];
                commandArgs.RemoveAt(0);
                (command, commandArgs) = SplitCommandLine(inner, commandArgs);
            }
        }
        var runner = LauncherName(command).ToLowerInvariant();
        // Read through the unwrapping and the launcher's extension, so the same bridge is a remote
        // connector however it is spelled, and on both platforms. The decoder's URL is checked
        // like any other: it vouches for a URL, not for a host free of userinfo.
        if (runner == "npx"
            && RemotePattern.Decode(JsonValue.Object(
                ("command", JsonValue.String("npx")),
                ("args", JsonValue.Array(commandArgs.Select(JsonValue.String))))) is { } remote)
        {
            return UrlOrigin(remote.Url)?.Host ?? RemoteType;
        }
        // The slot where a package runner names the server it fetches: the one place a bare
        // hyphenated word is a package rather than, as likely, a password. It can land on the
        // value of a flag named for a secret, which the first check below drops before it counts.
        var serverSlot = PackageRunners.Contains(runner)
            ? commandArgs.FindIndex(a => !a.StartsWith('-')) : -1;
        var args = commandArgs.Select((arg, index) =>
            index > 0 && IsSecretNamedFlag(commandArgs[index - 1]) ? null : Shown(arg, home, index == serverSlot));
        var tokens = new[] { Launcher(command) }.Concat(args);
        return string.Join(" ", tokens.OfType<string>());
    }

    /// <summary>Launchers whose first positional argument names the package they fetch and run.</summary>
    private static readonly HashSet<string> PackageRunners = ["npx", "uvx", "pipx", "bunx", "pnpx"];

    /// <summary>
    /// A launcher written as a whole command line - <c>npx -y server --token x</c> in one string -
    /// split into arguments the way a shell passes them: its first argument is the launcher and
    /// the rest are arguments ahead of <paramref name="args"/>, each held to the same rule, so a
    /// flag named for a secret at its end guards <c>args[0]</c> as it would any value.
    ///
    /// A command that is a path is not tokenized, since a Windows path holds spaces. It keeps its
    /// launcher - the last component of the path, which runs to the last word holding a separator -
    /// only when the words after its first are plainly more path or plain words: none starts
    /// with <c>-</c> or <c>/</c> or holds <c>:</c> (which covers <c>://</c>), <c>=</c> or a quote.
    /// Otherwise that last component could be the tail of a packed argument, and the launcher is
    /// omitted. The packed words are never shown, but a flag named for a secret at their end,
    /// read with its quotes removed as a shell would pass it, still guards <c>args[0]</c>. A
    /// path-shaped raw secret that passes these checks (<c>C:\x\tool.exe abc\cd.ef</c>) is
    /// accepted: residual 2 in the B1 report.
    /// </summary>
    private static (string, List<string>) SplitCommandLine(string text, IEnumerable<string> args)
    {
        if (!IsExplicitPath(text))
        {
            var tokens = ShellWords(text);
            return (tokens.Count > 0 ? tokens[0] : "", tokens.Skip(1).Concat(args).ToList());
        }
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1)
        {
            return (text, args.ToList());
        }
        var flag = string.Concat(ShellWords(words[^1]));
        var guarded = IsSecretNamedFlag(flag) ? new[] { flag } : [];
        var isPlain = !words.Skip(1).Any(w =>
            w.StartsWith('-') || w.StartsWith('/') || w.Contains(':') || w.Contains('=')
            || w.Contains('"') || w.Contains('\''));
        var pathEnd = Math.Max(Array.FindLastIndex(words, w => w.Contains('/') || w.Contains('\\')), 0);
        return (isPlain ? string.Join(" ", words[..(pathEnd + 1)]) : "", guarded.Concat(args).ToList());
    }

    /// <summary>
    /// <paramref name="text"/> as a shell passes it, which is also how the control layer's masking reads
    /// an argument written as a whole command line: whitespace separates arguments, a <c>"…"</c>
    /// or <c>'…'</c> run belongs to the argument it touches and loses its quotes, <c>\"</c> inside
    /// double quotes is a quote, and a backslash anywhere else is itself, as a Windows path needs.
    /// An unterminated quote runs to the end.
    /// </summary>
    internal static List<string> ShellWords(string text)
    {
        var words = new List<string>();
        System.Text.StringBuilder? word = null;
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is { } open)
            {
                if (c == open)
                {
                    quote = null;
                }
                else if (open == '"' && c == '\\' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    word?.Append('"');
                    i++;
                }
                else
                {
                    word?.Append(c);
                }
            }
            else if (char.IsWhiteSpace(c))
            {
                if (word is not null)
                {
                    words.Add(word.ToString());
                }
                word = null;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                word ??= new System.Text.StringBuilder();
            }
            else
            {
                (word ??= new System.Text.StringBuilder()).Append(c);
            }
        }
        if (word is not null)
        {
            words.Add(word.ToString());
        }
        return words;
    }

    /// <summary>
    /// The launcher, named the way it would be typed, or null when it could be a secret - or when
    /// it holds whitespace, a path with arguments packed into its last component.
    /// </summary>
    private static string? Launcher(string command)
    {
        var name = LauncherName(command);
        return name.Length == 0 || name.Any(char.IsWhiteSpace) || name.Contains('=')
            || CredentialHeuristics.LooksLikeCredential(name) || CredentialHeuristics.LooksLikeRandomToken(name) ? null : name;
    }

    /// <summary>
    /// One argument as the target column shows it, or null when it is none of the three shapes
    /// known to be safe: a URL, as its scheme and host; an explicit path, with the home folder
    /// abbreviated; or a named artefact - a package, image, script or module. A bare word, one
    /// with no <c>/</c>, <c>@</c> or <c>.</c>, is an artefact only in the server slot.
    /// </summary>
    private static string? Shown(string arg, string home, bool isServerSlot)
    {
        if (arg.Contains("://", StringComparison.Ordinal))
        {
            return UrlOrigin(arg) is { } origin ? $"{origin.Scheme}://{origin.Host}" : null;
        }
        if (CredentialHeuristics.LooksLikeCredential(arg) || CredentialHeuristics.LooksLikeRandomToken(arg))
        {
            return null;
        }
        if (IsExplicitPath(arg))
        {
            // A colon anywhere but a drive letter's is a Windows switch's value, `/p:secret`.
            var colonIsDrive = IsDriveRoot(arg) && !arg[2..].Contains(':');
            if (arg.Contains('=') || (arg.Contains(':') && !colonIsDrive))
            {
                return null;
            }
            var root = TrimmingOneTrailingSeparator(home);
            // Case-insensitive, as both platforms' default file systems are.
            if (root.Length == 0 || !arg.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return arg;
            }
            var remainder = arg[root.Length..];
            return remainder.Length == 0 || remainder.StartsWith('/') || remainder.StartsWith('\\') ? "~" + remainder : arg;
        }
        if (arg.Length is 0 or > 100 || !(char.IsAsciiLetterOrDigit(arg[0]) || arg[0] == '@')
            || !arg.All(c => char.IsAsciiLetterOrDigit(c) || "._@/-".Contains(c))
            || !(arg.Any(c => "/.@".Contains(c)) || (isServerSlot && arg.Any(c => "-_".Contains(c)))))
        {
            return null;
        }
        var slash = arg.IndexOf('/');
        if (arg.StartsWith('@') && slash > 1)
        {
            var name = arg[(slash + 1)..];
            if (name.Length > 0 && !name.Contains('/'))
            {
                return "…/" + name;
            }
        }
        return arg;
    }

    /// <summary><paramref name="path"/> without one trailing separator of either kind.</summary>
    private static string TrimmingOneTrailingSeparator(string path) =>
        path.EndsWith('/') || path.EndsWith('\\') ? path[..^1] : path;

    /// <summary>
    /// A flag without an attached value whose name says the next argument is a secret, whatever
    /// its dashes and case.
    /// </summary>
    private static bool IsSecretNamedFlag(string arg)
    {
        if (!arg.StartsWith('-') || arg.Contains('='))
        {
            return false;
        }
        return CredentialHeuristics.NamesASecret(arg);
    }

    /// <summary>
    /// Starts with <c>/</c>, <c>~</c>, <c>./</c>, <c>../</c> or a drive root (<c>X:\</c> or <c>X:/</c>).
    /// Internal rather than private because the Publish dialog offers a path row by the same rule.
    /// </summary>
    internal static bool IsExplicitPath(string arg) =>
        PathPrefixes.Any(p => arg.StartsWith(p, StringComparison.Ordinal)) || IsDriveRoot(arg);

    private static readonly string[] PathPrefixes = ["/", "~", "./", "../"];

    /// <summary><c>X:\</c> or <c>X:/</c>.</summary>
    private static bool IsDriveRoot(string arg) =>
        arg.Length >= 3 && char.IsAsciiLetter(arg[0]) && arg[1] == ':' && (arg[2] == '/' || arg[2] == '\\');

    /// <summary>
    /// The scheme and the host of the URL in <paramref name="text"/>, the host with its port when
    /// one is written; null when either is not plainly one. The authority runs from <c>://</c> to
    /// the first <c>/</c>, <c>?</c> or <c>#</c>, and the host is what follows its last <c>@</c>.
    /// What remains must be a bare host and optional port: a password holding an unencoded
    /// <c>/</c>, <c>?</c> or <c>#</c> ends the authority early, and whatever of it is left is
    /// refused rather than shown as the host, as is any URL with an <c>@</c> past its authority,
    /// where the boundary is in doubt. Taken from the text by hand rather than by a URL parser,
    /// because the two platforms' parsers disagree about case and default ports.
    /// </summary>
    private static (string Scheme, string Host)? UrlOrigin(string text)
    {
        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
        {
            return null;
        }
        var scheme = text[..separator];
        if (scheme.Length is < 2 or > 16 || !char.IsAsciiLetterLower(scheme[0])
            || !scheme.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '+'))
        {
            return null;
        }
        var rest = text[(separator + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        // An `@` after the authority ends means the boundary cannot be trusted: `u:p/w@h` is a
        // password holding a `/`, not a host `u:p` with an `@` in its path.
        if (end >= 0 && rest[end..].Contains('@'))
        {
            return null;
        }
        var authority = end >= 0 ? rest[..end] : rest;
        var host = authority[(authority.LastIndexOf('@') + 1)..];
        var parts = host.Split(':', 2);
        if (parts[0].Length == 0 || !parts[0].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
            || (parts.Length == 2 && (parts[1].Length is < 1 or > 5 || !parts[1].All(char.IsAsciiDigit))))
        {
            return null;
        }
        return (scheme, host);
    }

    /// <summary>
    /// The last component of a command, splitting on both separators rather than this platform's:
    /// a collection carries connectors authored on either, and a Mac command's launcher is still
    /// worth naming on a PC. Split by hand, because the path APIs on the two platforms disagree
    /// about which separators count. A Windows launcher's <c>.cmd</c>, <c>.exe</c> or <c>.bat</c> is
    /// dropped: <c>npx.cmd</c> is <c>npx</c>, both to name and to recognise.
    /// </summary>
    private static string LauncherName(string command)
    {
        var parts = command.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length > 0 ? parts[^1] : command;
        var isWindowsLauncher = name.Length > 4 && name[^4..].ToLowerInvariant() is ".cmd" or ".exe" or ".bat";
        return isWindowsLauncher ? name[..^4] : name;
    }
}
