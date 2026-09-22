using System.Text.Json;

namespace ConnectorControl.Core;

public enum CollectionPlatform
{
    Mac,
    Windows,
}

public static class CollectionPlatforms
{
    /// <summary>The platform this build renders launchers for; stated, not detected, so the Core tests on a Mac exercise the Windows rendering.</summary>
    public const CollectionPlatform Current = CollectionPlatform.Windows;

    public static string ToJsonString(this CollectionPlatform p) => p == CollectionPlatform.Mac ? "mac" : "windows";

    public static CollectionPlatform? Parse(string s) => s switch
    {
        "mac" => CollectionPlatform.Mac,
        "windows" => CollectionPlatform.Windows,
        _ => null,
    };
}

public sealed class CollectionDocumentException(string message) : Exception(message)
{
    /// <summary>Set only by <see cref="NewerFormat"/>: the version the document claims.</summary>
    public int? NewerFormatVersion { get; private init; }

    public static CollectionDocumentException NewerFormat(int version) =>
        new("This collection was made by a newer Connector Control.") { NewerFormatVersion = version };

    public static CollectionDocumentException Malformed(string detail) => new("collection document: " + detail);
}

/// <summary>
/// A path the author marked on <see cref="Connector"/> can no longer be found where it was
/// marked. The argument it stood for may be anywhere, so no document is written rather than one
/// that might carry that path as written. What the user reads is
/// <c>AppState.PathMarkMovedError</c>, which <c>AppState.Friendly</c> gives for this.
///
/// Mirror: <c>PublishIntentError.pathMarkMoved</c> in Sources/ConnectorControlCore/CollectionDocument.swift
/// </summary>
public sealed class PathMarkMovedException(string connector)
    : Exception($"a path mark on \"{connector}\" no longer finds its argument")
{
    public string Connector { get; } = connector;
}

/// <summary>
/// What the author ticked in the Publish sheet: which env values travel as values rather than as
/// stripped hints, which arguments become markers, and the hint text for each.
/// </summary>
public sealed class PublishIntent : IEquatable<PublishIntent>
{
    /// <summary>
    /// One marked argument. <paramref name="Value"/> is the argument as it read when it was
    /// marked, so the mark can follow it when arguments move and can tell when it no longer marks
    /// anything. Null only in a record written before values were kept, which no release did:
    /// such a mark is placed where its pointer points, as every mark once was.
    /// </summary>
    public sealed record PathMark(string Name, string? Hint, string? Value);

    public IReadOnlyDictionary<string, IReadOnlySet<string>> ShareValues { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<JsonPointer, PathMark>> PathMarks { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Hints { get; }

    public PublishIntent(
        IEnumerable<KeyValuePair<string, IReadOnlySet<string>>> shareValues,
        IEnumerable<KeyValuePair<string, IReadOnlyDictionary<JsonPointer, PathMark>>> pathMarks,
        IEnumerable<KeyValuePair<string, IReadOnlyDictionary<string, string>>> hints)
    {
        ShareValues = new Dictionary<string, IReadOnlySet<string>>(shareValues, StringComparer.Ordinal);
        PathMarks = new Dictionary<string, IReadOnlyDictionary<JsonPointer, PathMark>>(pathMarks, StringComparer.Ordinal);
        Hints = new Dictionary<string, IReadOnlyDictionary<string, string>>(hints, StringComparer.Ordinal);
    }

    public static PublishIntent None { get; } = new([], [], []);

    /// <summary>
    /// Everything this intent says about <paramref name="connector"/> said about
    /// <paramref name="newName"/> instead, or dropped when <paramref name="newName"/> is null: a
    /// renamed connector keeps its ticks, and a removed one leaves none behind for a later
    /// connector of the same name to inherit.
    /// </summary>
    public PublishIntent MovingConnector(string connector, string? newName)
    {
        var shareValues = new Dictionary<string, IReadOnlySet<string>>(ShareValues, StringComparer.Ordinal);
        var pathMarks = new Dictionary<string, IReadOnlyDictionary<JsonPointer, PathMark>>(PathMarks, StringComparer.Ordinal);
        var hints = new Dictionary<string, IReadOnlyDictionary<string, string>>(Hints, StringComparer.Ordinal);
        var hadShared = shareValues.Remove(connector, out var shared);
        var hadMarks = pathMarks.Remove(connector, out var marks);
        var hadHints = hints.Remove(connector, out var connectorHints);
        if (newName is not null)
        {
            if (hadShared)
            {
                shareValues[newName] = shared!;
            }
            if (hadMarks)
            {
                pathMarks[newName] = marks!;
            }
            if (hadHints)
            {
                hints[newName] = connectorHints!;
            }
        }
        return new PublishIntent(shareValues, pathMarks, hints);
    }

    /// <summary>The same intent with <paramref name="connector"/>'s path marks replaced; an empty set drops its entry.</summary>
    public PublishIntent ReplacingPathMarks(string connector, IReadOnlyDictionary<JsonPointer, PathMark> marks)
    {
        var pathMarks = new Dictionary<string, IReadOnlyDictionary<JsonPointer, PathMark>>(PathMarks, StringComparer.Ordinal);
        if (marks.Count == 0)
        {
            pathMarks.Remove(connector);
        }
        else
        {
            pathMarks[connector] = marks;
        }
        return new PublishIntent(ShareValues, pathMarks, Hints);
    }

    /// <summary>
    /// Where one connector's path marks sit among its arguments now.
    /// <para>
    /// A mark stays on the argument at its pointer while that argument still reads as it did when
    /// it was marked. Failing that, it follows its value to the one argument that holds it. A mark
    /// that finds neither — its value edited away, held by two arguments, or already claimed by
    /// another mark — is unresolved: the argument it was made on could be anywhere, and nothing
    /// may be published over it.
    /// </para>
    /// <para>
    /// A mark with no recorded value is placed where its pointer points, and one pointing past the
    /// arguments marks nothing, which is how every mark behaved before values were kept.
    /// </para>
    /// </summary>
    public static PathMarkPlacement PlacePathMarks(IReadOnlyDictionary<JsonPointer, PathMark> marks, IReadOnlyList<string> args)
    {
        var placed = new Dictionary<int, PathMark>();
        var unresolved = new Dictionary<JsonPointer, PathMark>();
        var following = new List<(JsonPointer Pointer, PathMark Mark)>();
        // Pointer order on both platforms, so which of two competing marks wins is the same
        // everywhere. Marks still on their own argument go first: a mark that stayed put keeps it,
        // whatever another mark's value would follow onto.
        foreach (var (pointer, mark) in marks.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        {
            var index = ArgumentIndex(pointer, args.Count);
            if (mark.Value is null)
            {
                if (index is { } at)
                {
                    if (!placed.TryAdd(at, mark))
                    {
                        unresolved[pointer] = mark;
                    }
                }
                continue;
            }
            if (index is { } i && string.Equals(args[i], mark.Value, StringComparison.Ordinal) && !placed.ContainsKey(i))
            {
                placed[i] = mark;
            }
            else
            {
                following.Add((pointer, mark));
            }
        }
        foreach (var (pointer, mark) in following)
        {
            var holders = Enumerable.Range(0, args.Count)
                .Where(i => string.Equals(args[i], mark.Value, StringComparison.Ordinal))
                .ToList();
            if (holders.Count == 1 && placed.TryAdd(holders[0], mark))
            {
                continue;
            }
            unresolved[pointer] = mark;
        }
        return new PathMarkPlacement(placed, unresolved);
    }

    /// <summary>The argument index a <c>/args/&lt;n&gt;</c> pointer names, when there is an argument there.</summary>
    private static int? ArgumentIndex(JsonPointer pointer, int count) =>
        pointer.Segments.Length == 2 && pointer.Segments[0] == "args"
        && int.TryParse(pointer.Segments[1], System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture, out var index)
        && index >= 0 && index < count
            ? index
            : null;

    /// <summary>
    /// Structural over all three dictionaries, so two intents that say the same thing are the
    /// same intent however they were built. Swift gets this from its value types; here every
    /// level is a reference whose own Equals compares references, so each one is spelled out.
    /// </summary>
    public bool Equals(PublishIntent? other) =>
        other is not null
        && DictionaryEquality.Equal(ShareValues, other.ShareValues, static (x, y) => x.SetEquals(y))
        && DictionaryEquality.Equal(PathMarks, other.PathMarks, static (x, y) => MarksEqual(x, y))
        && DictionaryEquality.Equal(Hints, other.Hints, static (x, y) => DictionaryEquality.Equal(x, y));

    public override bool Equals(object? obj) => Equals(obj as PublishIntent);

    public override int GetHashCode() => HashCode.Combine(
        DictionaryEquality.Hash(ShareValues, SetHash),
        DictionaryEquality.Hash(PathMarks, MarksHash),
        DictionaryEquality.Hash(Hints, static h => DictionaryEquality.Hash(h)));

    private static int SetHash(IReadOnlySet<string> names)
    {
        var hash = new HashCode();
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            hash.Add(name, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    /// <summary>Marks are keyed by pointer, not by string, so the string-keyed helper cannot serve.</summary>
    private static bool MarksEqual(IReadOnlyDictionary<JsonPointer, PathMark> a, IReadOnlyDictionary<JsonPointer, PathMark> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        foreach (var (pointer, mark) in a)
        {
            if (!b.TryGetValue(pointer, out var other) || !mark.Equals(other))
            {
                return false;
            }
        }
        return true;
    }

    private static int MarksHash(IReadOnlyDictionary<JsonPointer, PathMark> marks)
    {
        var hash = new HashCode();
        foreach (var (pointer, mark) in marks.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        {
            hash.Add(pointer);
            hash.Add(mark);
        }
        return hash.ToHashCode();
    }
}

/// <summary>One connector's path marks, placed on the arguments it holds now.</summary>
public sealed class PathMarkPlacement(
    IReadOnlyDictionary<int, PublishIntent.PathMark> placed,
    IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark> unresolved)
{
    /// <summary>Argument index → the mark that sits on it.</summary>
    public IReadOnlyDictionary<int, PublishIntent.PathMark> Placed { get; } = placed;

    /// <summary>The marks that found no argument, by the pointer they were recorded at.</summary>
    public IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark> Unresolved { get; } = unresolved;
}

public sealed record RenderedNeed(string? Hint, JsonPointer Pointer);

public sealed record RenderedConnector(JsonValue Config, IReadOnlyDictionary<string, RenderedNeed> Needs, CollectionPlatform? AuthoredOn)
{
    public bool Equals(RenderedConnector? other) =>
        other is not null && Config.Equals(other.Config) && DictionaryEquality.Equal(Needs, other.Needs) && AuthoredOn == other.AuthoredOn;

    public override int GetHashCode() => HashCode.Combine(Config, DictionaryEquality.Hash(Needs), AuthoredOn);
}

public sealed record RenderedCollection(IReadOnlyDictionary<string, RenderedConnector> Connectors, IReadOnlyDictionary<string, string> Excluded)
{
    public bool Equals(RenderedCollection? other) =>
        other is not null && DictionaryEquality.Equal(Connectors, other.Connectors) && DictionaryEquality.Equal(Excluded, other.Excluded);

    public override int GetHashCode() => HashCode.Combine(DictionaryEquality.Hash(Connectors), DictionaryEquality.Hash(Excluded));
}

/// <summary>
/// The document a collection travels as. Remote connectors are stored in the neutral form the
/// editor already uses, so each importer renders its own launcher; secrets and marked paths are
/// placeholders; enabled flags never travel.
///
/// Mirror: Sources/ConnectorControlCore/CollectionDocument.swift
/// </summary>
public sealed class CollectionDocument : IEquatable<CollectionDocument>
{
    public const int FormatVersion = 1;
    public const string FileExtension = "json";
    public const string TokenNeed = "token";
    public const string HeaderValueNeed = "header_value";
    public const string ClientSecretNeed = "client_secret";

    public string Name { get; }
    public string? Author { get; }
    public string? Origin { get; }

    /// <summary>ISO 8601 UTC, e.g. "2026-09-21T14:02:11Z". Stored as text: the document never does date arithmetic, and a string cannot drift between two platforms' formatters.</summary>
    public string Exported { get; }

    public IReadOnlyDictionary<string, Connector> Connectors { get; }

    public CollectionDocument(string name, string? author, string? origin, string exported,
                              IEnumerable<KeyValuePair<string, Connector>> connectors)
    {
        Name = name;
        Author = author;
        Origin = origin;
        Exported = exported;
        Connectors = new Dictionary<string, Connector>(connectors, StringComparer.Ordinal);
    }

    public string FileName => Slug.Make(Name) + "." + FileExtension;

    public sealed class Connector : IEquatable<Connector>
    {
        public Launcher Launcher { get; }
        public IReadOnlyDictionary<string, EnvValue> Env { get; }

        /// <summary>Placeholder name → hint (null: no hint).</summary>
        public IReadOnlyDictionary<string, string?> Needs { get; }

        public IReadOnlyDictionary<string, JsonValue> Additional { get; }

        public Connector(
            Launcher launcher,
            IEnumerable<KeyValuePair<string, EnvValue>>? env = null,
            IEnumerable<KeyValuePair<string, string?>>? needs = null,
            IEnumerable<KeyValuePair<string, JsonValue>>? additional = null)
        {
            Launcher = launcher;
            Env = env is null ? new Dictionary<string, EnvValue>(StringComparer.Ordinal) : new Dictionary<string, EnvValue>(env, StringComparer.Ordinal);
            Needs = needs is null ? new Dictionary<string, string?>(StringComparer.Ordinal) : new Dictionary<string, string?>(needs, StringComparer.Ordinal);
            Additional = additional is null ? new Dictionary<string, JsonValue>(StringComparer.Ordinal) : new Dictionary<string, JsonValue>(additional, StringComparer.Ordinal);
        }

        /// <summary>DictionaryEquality cannot take a nullable value type argument, and a need with no hint is a null value.</summary>
        internal static bool NeedsEqual(IReadOnlyDictionary<string, string?> a, IReadOnlyDictionary<string, string?> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }
            foreach (var (key, value) in a)
            {
                if (!b.TryGetValue(key, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        internal static int NeedsHash(IReadOnlyDictionary<string, string?> d)
        {
            var hash = new HashCode();
            foreach (var (key, value) in d.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                hash.Add(key, StringComparer.Ordinal);
                hash.Add(value, StringComparer.Ordinal);
            }
            return hash.ToHashCode();
        }

        public bool Equals(Connector? other) =>
            other is not null
            && Launcher.Equals(other.Launcher)
            && DictionaryEquality.Equal(Env, other.Env)
            && NeedsEqual(Needs, other.Needs)
            && DictionaryEquality.Equal(Additional, other.Additional);

        public override bool Equals(object? obj) => Equals(obj as Connector);

        public override int GetHashCode() =>
            HashCode.Combine(Launcher, DictionaryEquality.Hash(Env), NeedsHash(Needs), DictionaryEquality.Hash(Additional));

        internal JsonValue Encode()
        {
            var props = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            switch (Launcher)
            {
                case Launcher.Remote r:
                    props["remote"] = r.Encode();
                    break;
                case Launcher.Local l:
                    props["local"] = l.Encode();
                    break;
            }
            props["env"] = JsonValue.Object(Env.Select(p => new KeyValuePair<string, JsonValue>(p.Key, p.Value.Encode())));
            props["needs"] = JsonValue.Object(Needs.Select(p => new KeyValuePair<string, JsonValue>(
                p.Key, JsonValue.Object(("hint", p.Value is null ? JsonValue.Null : JsonValue.String(p.Value))))));
            props["additional"] = JsonValue.Object(Additional);
            return JsonValue.Object(props);
        }

        internal static Connector Decode(JsonValue json, string connector)
        {
            var what = $"connector \"{connector}\"";
            if (json.Kind != JsonKind.Object)
            {
                throw CollectionDocumentException.Malformed($"{what} is not a JSON object");
            }
            var remote = json["remote"];
            var local = json["local"];
            Launcher launcher;
            if (remote is not null && local is null)
            {
                launcher = Launcher.Remote.Decode(remote, $"{what} remote");
            }
            else if (remote is null && local is not null)
            {
                launcher = Launcher.Local.Decode(local, $"{what} local");
            }
            else
            {
                throw CollectionDocumentException.Malformed($"{what} needs exactly one of remote or local");
            }
            var env = new Dictionary<string, EnvValue>(StringComparer.Ordinal);
            foreach (var (key, value) in ObjectValue(json["env"], $"{what} env"))
            {
                env[key] = EnvValue.Decode(value, $"{what} env \"{key}\"");
            }
            var needs = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (key, value) in ObjectValue(json["needs"], $"{what} needs"))
            {
                var entryWhat = $"{what} need \"{key}\"";
                var entry = ObjectValue(value, entryWhat);
                needs[key] = OptionalString(entry.GetValueOrDefault(key: "hint"), $"{entryWhat} hint");
            }
            var additional = ObjectValue(json["additional"], $"{what} additional");
            return new Connector(launcher, env, needs, additional);
        }
    }

    public abstract record Launcher
    {
        private Launcher()
        {
        }

        public sealed record Remote(string Url, Auth Auth, string Package, IReadOnlyList<string> ExtraArgs) : Launcher
        {
            public bool Equals(Remote? other) =>
                other is not null
                && string.Equals(Url, other.Url, StringComparison.Ordinal)
                && Auth.Equals(other.Auth)
                && string.Equals(Package, other.Package, StringComparison.Ordinal)
                && ExtraArgs.SequenceEqual(other.ExtraArgs, StringComparer.Ordinal);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(Url, StringComparer.Ordinal);
                hash.Add(Auth);
                hash.Add(Package, StringComparer.Ordinal);
                foreach (var arg in ExtraArgs) { hash.Add(arg, StringComparer.Ordinal); }
                return hash.ToHashCode();
            }

            internal JsonValue Encode() => JsonValue.Object(
                ("url", JsonValue.String(Url)),
                ("auth", Auth.Encode()),
                ("package", JsonValue.String(Package)),
                ("extraArgs", JsonValue.Array(ExtraArgs.Select(JsonValue.String))));

            internal static Remote Decode(JsonValue json, string what)
            {
                if (json.Kind != JsonKind.Object)
                {
                    throw CollectionDocumentException.Malformed($"{what} is not a JSON object");
                }
                var auth = json["auth"] ?? throw CollectionDocumentException.Malformed($"{what} auth is missing");
                return new Remote(
                    RequiredString(json["url"], $"{what} url"),
                    Auth.Decode(auth, $"{what} auth"),
                    RequiredString(json["package"], $"{what} package"),
                    StringArray(json["extraArgs"], $"{what} extraArgs"));
            }
        }

        public sealed record Local(string Command, IReadOnlyList<string> Args, CollectionPlatform Platform) : Launcher
        {
            public bool Equals(Local? other) =>
                other is not null
                && string.Equals(Command, other.Command, StringComparison.Ordinal)
                && Args.SequenceEqual(other.Args, StringComparer.Ordinal)
                && Platform == other.Platform;

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(Command, StringComparer.Ordinal);
                foreach (var arg in Args) { hash.Add(arg, StringComparer.Ordinal); }
                hash.Add(Platform);
                return hash.ToHashCode();
            }

            internal JsonValue Encode() => JsonValue.Object(
                ("command", JsonValue.String(Command)),
                ("args", JsonValue.Array(Args.Select(JsonValue.String))),
                ("platform", JsonValue.String(Platform.ToJsonString())));

            internal static Local Decode(JsonValue json, string what)
            {
                if (json.Kind != JsonKind.Object)
                {
                    throw CollectionDocumentException.Malformed($"{what} is not a JSON object");
                }
                var rawPlatform = RequiredString(json["platform"], $"{what} platform");
                var platform = CollectionPlatforms.Parse(rawPlatform)
                    ?? throw CollectionDocumentException.Malformed($"{what} platform \"{rawPlatform}\" is not mac or windows");
                return new Local(
                    RequiredString(json["command"], $"{what} command"),
                    StringArray(json["args"], $"{what} args"),
                    platform);
            }
        }
    }

    public abstract record Auth
    {
        private Auth()
        {
        }

        public sealed record Automatic : Auth;

        public sealed record Bearer : Auth;

        public sealed record Header(string Name) : Auth;

        public sealed record OAuthClient(string ClientId, string Scopes) : Auth;

        /// <summary>The payload-free automatic case as a value; a record's type name cannot itself be one (Swift writes <c>.automatic</c>).</summary>
        public static Auth Auto { get; } = new Automatic();

        internal JsonValue Encode() => this switch
        {
            Automatic => JsonValue.Object(("kind", JsonValue.String("automatic"))),
            Bearer => JsonValue.Object(("kind", JsonValue.String("bearer"))),
            Header h => JsonValue.Object(("kind", JsonValue.String("header")), ("name", JsonValue.String(h.Name))),
            OAuthClient o => JsonValue.Object(
                ("kind", JsonValue.String("oauthClient")),
                ("clientId", JsonValue.String(o.ClientId)),
                ("scopes", JsonValue.String(o.Scopes))),
            _ => throw new InvalidOperationException(),
        };

        internal static Auth Decode(JsonValue json, string what)
        {
            if (json.Kind != JsonKind.Object)
            {
                throw CollectionDocumentException.Malformed($"{what} is not a JSON object");
            }
            var kind = RequiredString(json["kind"], $"{what} kind");
            return kind switch
            {
                "automatic" => Auto,
                "bearer" => new Bearer(),
                "header" => new Header(RequiredString(json["name"], $"{what} name")),
                "oauthClient" => new OAuthClient(
                    RequiredString(json["clientId"], $"{what} clientId"),
                    OptionalString(json["scopes"], $"{what} scopes") ?? ""),
                _ => throw CollectionDocumentException.Malformed($"{what} kind \"{kind}\" is not automatic, bearer, header or oauthClient"),
            };
        }
    }

    public abstract record EnvValue
    {
        private EnvValue()
        {
        }

        public sealed record Hint(string? Text) : EnvValue;

        public sealed record Value(string Text) : EnvValue;

        internal JsonValue Encode() => this switch
        {
            Hint h => JsonValue.Object(("hint", h.Text is null ? JsonValue.Null : JsonValue.String(h.Text))),
            Value v => JsonValue.Object(("value", JsonValue.String(v.Text))),
            _ => throw new InvalidOperationException(),
        };

        internal static EnvValue Decode(JsonValue json, string what)
        {
            if (json.Kind != JsonKind.Object)
            {
                throw CollectionDocumentException.Malformed($"{what} is not a JSON object");
            }
            if (json["value"] is { } value)
            {
                return new Value(RequiredString(value, $"{what} value"));
            }
            if (!json.ObjectProperties.ContainsKey("hint"))
            {
                throw CollectionDocumentException.Malformed($"{what} has neither hint nor value");
            }
            return new Hint(OptionalString(json["hint"], $"{what} hint"));
        }
    }

    public bool Equals(CollectionDocument? other) =>
        other is not null
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Author, other.Author, StringComparison.Ordinal)
        && string.Equals(Origin, other.Origin, StringComparison.Ordinal)
        && string.Equals(Exported, other.Exported, StringComparison.Ordinal)
        && DictionaryEquality.Equal(Connectors, other.Connectors);

    public override bool Equals(object? obj) => Equals(obj as CollectionDocument);

    public override int GetHashCode() =>
        HashCode.Combine(Name, Author, Origin, Exported, DictionaryEquality.Hash(Connectors));

    // MARK: Encode

    public JsonValue Encode()
    {
        var root = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
        {
            ["connectorControlCollection"] = JsonValue.Int(FormatVersion),
            ["name"] = JsonValue.String(Name),
            ["exported"] = JsonValue.String(Exported),
            ["connectors"] = JsonValue.Object(Connectors.Select(p => new KeyValuePair<string, JsonValue>(p.Key, p.Value.Encode()))),
        };
        if (Author is not null)
        {
            root["author"] = JsonValue.String(Author);
        }
        if (Origin is not null)
        {
            root["origin"] = JsonValue.String(Origin);
        }
        return JsonValue.Object(root);
    }

    public byte[] Serialize() => Encode().Serialize();

    // MARK: Decode

    public static CollectionDocument Decode(byte[] data)
    {
        JsonValue json;
        try
        {
            json = JsonValue.Parse(data);
        }
        catch (JsonException e)
        {
            throw CollectionDocumentException.Malformed("not JSON: " + e.Message);
        }
        return Decode(json);
    }

    public static CollectionDocument Decode(JsonValue json)
    {
        if (json.Kind != JsonKind.Object)
        {
            throw CollectionDocumentException.Malformed("top level is not a JSON object");
        }
        if (json["connectorControlCollection"] is not { Kind: JsonKind.Int } version)
        {
            throw CollectionDocumentException.Malformed("connectorControlCollection is missing");
        }
        if (version.IntValue > FormatVersion)
        {
            throw CollectionDocumentException.NewerFormat((int)version.IntValue);
        }
        var name = RequiredString(json["name"], "name");
        var author = OptionalString(json["author"], "author");
        var origin = OptionalString(json["origin"], "origin");
        var exported = RequiredString(json["exported"], "exported");
        var rawConnectors = json["connectors"] ?? throw CollectionDocumentException.Malformed("connectors is missing");
        if (rawConnectors.Kind != JsonKind.Object)
        {
            throw CollectionDocumentException.Malformed("connectors is not a JSON object");
        }
        var connectors = new Dictionary<string, Connector>(StringComparer.Ordinal);
        foreach (var (key, value) in rawConnectors.ObjectProperties)
        {
            connectors[key] = Connector.Decode(value, key);
        }
        return new CollectionDocument(name, author, origin, exported, connectors);
    }

    // MARK: Render (this platform)

    public RenderedCollection Render()
    {
        var rendered = new Dictionary<string, RenderedConnector>(StringComparer.Ordinal);
        var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, connector) in Connectors)
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in connector.Env)
            {
                env[key] = value is EnvValue.Value shared ? shared.Text : Placeholder.Marker(key);
            }
            JsonValue config;
            CollectionPlatform? authoredOn = null;
            switch (connector.Launcher)
            {
                case Launcher.Remote r:
                {
                    RemoteAuth auth = r.Auth switch
                    {
                        Auth.Bearer => new RemoteAuth.Bearer(Placeholder.Marker(TokenNeed)),
                        Auth.Header h => new RemoteAuth.Header(h.Name, Placeholder.Marker(HeaderValueNeed)),
                        Auth.OAuthClient o => new RemoteAuth.OAuthClient(o.ClientId, Placeholder.Marker(ClientSecretNeed), o.Scopes),
                        _ => RemoteAuth.Auto,
                    };
                    var remoteConfig = new RemoteConfig(
                        url: r.Url,
                        auth: auth,
                        launchStyle: RemoteLaunchStyle.CmdNpx,
                        extraArgs: r.ExtraArgs,
                        passthroughEnv: env,
                        package: r.Package);
                    if (RemotePattern.CmdUnsafeField(remoteConfig) is { } field)
                    {
                        // The cmd /c launcher hands these characters to cmd.exe; the Mac never writes that
                        // launcher, so it renders every remote connector and excludes none.
                        excluded[name] = RemotePattern.CmdUnsafeReason(field);
                        continue;
                    }
                    config = MergeAdditional(connector.Additional, RemotePattern.Encode(remoteConfig));
                    break;
                }
                case Launcher.Local l:
                    config = FormMapper.Serialize(new FormModel(l.Command, l.Args, env, connector.Additional));
                    authoredOn = l.Platform;
                    break;
                default:
                    continue;
            }
            var needs = new Dictionary<string, RenderedNeed>(StringComparer.Ordinal);
            foreach (var (pointer, names) in Placeholder.MarkersIn(config))
            {
                foreach (var n in names)
                {
                    string? hint;
                    if (connector.Needs.TryGetValue(n, out var declared))
                    {
                        hint = declared;
                    }
                    else
                    {
                        hint = connector.Env.TryGetValue(n, out var envValue) && envValue is EnvValue.Hint h ? h.Text : null;
                    }
                    needs[n] = new RenderedNeed(hint, pointer);
                }
            }
            rendered[name] = new RenderedConnector(config, needs, authoredOn);
        }
        return new RenderedCollection(rendered, excluded);
    }

    /// <summary>
    /// A remote connector's <c>Additional</c> fields (whatever the form the config came from
    /// cannot represent) merged into the freshly encoded launcher config; the encoded keys —
    /// always <c>command</c>/<c>args</c>, sometimes <c>env</c> — win on a collision, since they
    /// are what makes the connector run.
    /// </summary>
    private static JsonValue MergeAdditional(IReadOnlyDictionary<string, JsonValue> additional, JsonValue config)
    {
        if (additional.Count == 0 || config.Kind != JsonKind.Object)
        {
            return config;
        }
        var merged = new Dictionary<string, JsonValue>(additional, StringComparer.Ordinal);
        foreach (var (key, value) in config.ObjectProperties)
        {
            merged[key] = value;
        }
        return JsonValue.Object(merged);
    }

    // MARK: Export

    /// <summary>
    /// Throws <see cref="PathMarkMovedException"/> for the first connector, by name, whose path
    /// marks cannot all be placed (<see cref="PublishIntent.PlacePathMarks"/>), and for a remote
    /// connector that still carries a mark with a value: a remote connector's arguments are built
    /// by each importer, so a mark there was made while it was a local one, and the path it stood
    /// for may now be travelling in its extra arguments.
    /// </summary>
    public static CollectionDocument Export(string name, string? author, string? origin, string exported,
                                            IEnumerable<KeyValuePair<string, JsonValue>> connectors, PublishIntent intent)
    {
        var result = new Dictionary<string, Connector>(StringComparer.Ordinal);
        // By name, so the connector a refusal names is the same on both platforms.
        foreach (var (connectorName, config) in connectors.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark> marks =
                intent.PathMarks.TryGetValue(connectorName, out var m) ? m : new Dictionary<JsonPointer, PublishIntent.PathMark>();
            IReadOnlySet<string> shared = intent.ShareValues.TryGetValue(connectorName, out var s) ? s : new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyDictionary<string, string> hints = intent.Hints.TryGetValue(connectorName, out var h) ? h : new Dictionary<string, string>(StringComparer.Ordinal);
            string? HintFor(string key) => hints.TryGetValue(key, out var hint) ? hint : null;
            EnvValue EnvFor(string key, string value) => shared.Contains(key) ? new EnvValue.Value(value) : new EnvValue.Hint(HintFor(key));
            var needs = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (RemotePattern.Decode(config) is { } remote)
            {
                if (marks.Values.Any(mark => mark.Value is not null))
                {
                    throw new PathMarkMovedException(connectorName);
                }
                Auth auth;
                switch (remote.Auth)
                {
                    case RemoteAuth.Bearer:
                        auth = new Auth.Bearer();
                        needs[TokenNeed] = HintFor(TokenNeed);
                        break;
                    case RemoteAuth.Header header:
                        auth = new Auth.Header(header.Name);
                        needs[HeaderValueNeed] = HintFor(HeaderValueNeed);
                        break;
                    case RemoteAuth.OAuthClient oauth:
                        auth = new Auth.OAuthClient(oauth.ClientId, oauth.Scopes);
                        needs[ClientSecretNeed] = HintFor(ClientSecretNeed);
                        break;
                    default:
                        auth = Auth.Auto;
                        break;
                }
                var env = remote.PassthroughEnv.ToDictionary(p => p.Key, p => EnvFor(p.Key, p.Value), StringComparer.Ordinal);
                // Whatever the form has no widget for — a key RemotePattern.Decode doesn't read —
                // travels too, the same way a local connector's does.
                var additional = FormMapper.Analyze(config).Model.Additional;
                result[connectorName] = new Connector(
                    new Launcher.Remote(remote.Url, auth, remote.Package, remote.ExtraArgs.ToList()),
                    env, needs, additional);
            }
            else
            {
                var model = FormMapper.Analyze(config).Model;
                var args = model.Args.ToList();
                var placement = PublishIntent.PlacePathMarks(marks, model.Args);
                if (placement.Unresolved.Count > 0)
                {
                    throw new PathMarkMovedException(connectorName);
                }
                foreach (var (i, mark) in placement.Placed.OrderBy(p => p.Key))
                {
                    args[i] = Placeholder.Marker(mark.Name);
                    needs[mark.Name] = mark.Hint;
                }
                // A marker the author already typed, or one an imported copy still carries, is a
                // need too — otherwise re-exporting a collection would drop what it asks for.
                foreach (var n in args.Append(model.Command).SelectMany(Placeholder.NamesIn))
                {
                    if (!needs.ContainsKey(n))
                    {
                        needs[n] = HintFor(n);
                    }
                }
                var env = model.Env.ToDictionary(p => p.Key, p => EnvFor(p.Key, p.Value), StringComparer.Ordinal);
                result[connectorName] = new Connector(
                    new Launcher.Local(model.Command, args, CollectionPlatforms.Current),
                    env, needs, model.Additional);
            }
        }
        return new CollectionDocument(name, author, origin, exported, result);
    }

    /// <summary>
    /// "args[N] looks like a credential" / "env.NAME looks like a credential" lines for the
    /// publish preview; never an edit. Each arg is tested whole and, for a literal "key: value"
    /// pair such as a <c>--header</c> flag's argument, on the text after the colon too, since the
    /// heuristic's own space check would otherwise hide a credential sitting right after one. Env
    /// is only tested for names in <paramref name="sharedEnv"/> — the ones the author ticked to
    /// travel as a value rather than a hint — since a hint-only value never leaves this machine.
    /// </summary>
    public static IReadOnlyList<string> CredentialWarnings(JsonValue config, IReadOnlySet<string> sharedEnv)
    {
        if (config.Kind != JsonKind.Object)
        {
            return [];
        }
        var warnings = new List<string>();
        if (config["args"] is { Kind: JsonKind.Array } args)
        {
            for (var i = 0; i < args.ArrayItems.Length; i++)
            {
                var item = args.ArrayItems[i];
                if (item.Kind != JsonKind.String)
                {
                    continue;
                }
                var s = item.StringValue;
                var colon = s.IndexOf(": ", StringComparison.Ordinal);
                var afterColon = colon >= 0 ? s[(colon + 2)..] : null;
                if (CredentialHeuristics.LooksLikeCredential(s) || (afterColon is not null && CredentialHeuristics.LooksLikeCredential(afterColon)))
                {
                    warnings.Add($"args[{i}] looks like a credential");
                }
            }
        }
        if (config["env"] is { Kind: JsonKind.Object } env)
        {
            foreach (var name in sharedEnv.OrderBy(n => n, StringComparer.Ordinal))
            {
                if (env[name] is { Kind: JsonKind.String } value && CredentialHeuristics.LooksLikeCredential(value.StringValue))
                {
                    warnings.Add($"env.{name} looks like a credential");
                }
            }
        }
        return warnings;
    }

    // MARK: Decoding helpers
    // Every failure names the key it read, so a hand-edited document says what is wrong with it.

    internal static string RequiredString(JsonValue? value, string what)
    {
        if (value is null)
        {
            throw CollectionDocumentException.Malformed($"{what} is missing");
        }
        if (value.Kind != JsonKind.String)
        {
            throw CollectionDocumentException.Malformed($"{what} is not a string");
        }
        return value.StringValue;
    }

    internal static string? OptionalString(JsonValue? value, string what)
    {
        if (value is null || value.Kind == JsonKind.Null)
        {
            return null;
        }
        if (value.Kind != JsonKind.String)
        {
            throw CollectionDocumentException.Malformed($"{what} is not a string");
        }
        return value.StringValue;
    }

    internal static IReadOnlyList<string> StringArray(JsonValue? value, string what)
    {
        if (value is null)
        {
            return [];
        }
        if (value.Kind != JsonKind.Array)
        {
            throw CollectionDocumentException.Malformed($"{what} is not an array");
        }
        var items = new List<string>();
        for (var i = 0; i < value.ArrayItems.Length; i++)
        {
            var item = value.ArrayItems[i];
            if (item.Kind != JsonKind.String)
            {
                throw CollectionDocumentException.Malformed($"{what}[{i}] is not a string");
            }
            items.Add(item.StringValue);
        }
        return items;
    }

    internal static IReadOnlyDictionary<string, JsonValue> ObjectValue(JsonValue? value, string what)
    {
        if (value is null)
        {
            return new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        }
        if (value.Kind != JsonKind.Object)
        {
            throw CollectionDocumentException.Malformed($"{what} is not a JSON object");
        }
        return value.ObjectProperties;
    }
}
