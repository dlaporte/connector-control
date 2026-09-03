namespace ConnectorControl.Core;

/// <summary>
/// The master list (schema v2, profile-aware). Mutable like the Swift struct's
/// `var` usage; structural equality like the Swift value type. Use
/// <see cref="Clone"/> where Swift relied on copy semantics.
/// </summary>
public sealed class MasterStore : IEquatable<MasterStore>
{
    public const int CurrentVersion = 2;

    public int Version { get; set; }
    public string ActiveProfile { get; set; }
    public Dictionary<string, Profile> Profiles { get; }

    public MasterStore(int version, string activeProfile, IEnumerable<KeyValuePair<string, Profile>> profiles)
    {
        Version = version;
        ActiveProfile = activeProfile;
        Profiles = new Dictionary<string, Profile>(profiles, StringComparer.Ordinal);
    }

    /// <summary>Swift <c>MasterStore(version:mcps:)</c>: a single "Default" profile; always v2.</summary>
    public MasterStore(IEnumerable<KeyValuePair<string, McpEntry>> mcps)
        : this(CurrentVersion, "Default", [new KeyValuePair<string, Profile>("Default", new Profile(mcps))])
    {
    }

    /// <summary>Swift <c>.empty</c>. A fresh instance every call — this type is mutable.</summary>
    public static MasterStore Empty() =>
        new(CurrentVersion, "Default", [new KeyValuePair<string, Profile>("Default", new Profile())]);

    /// <summary>The active profile's connectors — the view the entire app operates on. Created on demand.</summary>
    public Dictionary<string, McpEntry> Mcps
    {
        get
        {
            if (!Profiles.TryGetValue(ActiveProfile, out var profile))
            {
                profile = new Profile();
                Profiles[ActiveProfile] = profile;
            }
            return profile.Mcps;
        }
    }

    /// <summary>Claude's <c>mcpServers</c> section rendered from this store: the enabled subset's configs.</summary>
    public IReadOnlyDictionary<string, JsonValue> EnabledServers =>
        Mcps.Where(p => p.Value.Enabled).ToDictionary(p => p.Key, p => p.Value.Config, StringComparer.Ordinal);

    /// <summary>null on success, else a user-facing error message.</summary>
    public string? AddProfile(string name, bool copyingCurrent)
    {
        var trimmed = name.TrimSpaces();
        if (trimmed.Length == 0)
        {
            return "Name must not be empty.";
        }
        if (Profiles.ContainsKey(trimmed))
        {
            return $"A profile named \u201C{trimmed}\u201D already exists.";
        }
        Profiles[trimmed] = copyingCurrent ? new Profile(Mcps) : new Profile();
        ActiveProfile = trimmed;
        return null;
    }

    public string? RenameActiveProfile(string name)
    {
        var trimmed = name.TrimSpaces();
        if (trimmed.Length == 0)
        {
            return "Name must not be empty.";
        }
        if (trimmed != ActiveProfile && Profiles.ContainsKey(trimmed))
        {
            return $"A profile named \u201C{trimmed}\u201D already exists.";
        }
        if (!Profiles.Remove(ActiveProfile, out var current))
        {
            return null;
        }
        Profiles[trimmed] = current;
        ActiveProfile = trimmed;
        return null;
    }

    public string? DeleteActiveProfile()
    {
        if (Profiles.Count <= 1)
        {
            return "Can\u2019t delete the last profile.";
        }
        Profiles.Remove(ActiveProfile);
        ActiveProfile = Profiles.Keys.Order(StringComparer.Ordinal).First();
        return null;
    }

    public string? SwitchProfile(string name)
    {
        if (!Profiles.ContainsKey(name))
        {
            return $"No profile named \u201C{name}\u201D.";
        }
        ActiveProfile = name;
        return null;
    }

    public MasterStore Clone() =>
        new(Version, ActiveProfile, Profiles.Select(p => new KeyValuePair<string, Profile>(p.Key, p.Value.Clone())));

    // MARK: JSON (the Swift Codable synthesis, made explicit)

    public JsonValue ToJson() => JsonValue.Object(
        ("version", JsonValue.Int(Version)),
        ("activeProfile", JsonValue.String(ActiveProfile)),
        ("profiles", JsonValue.Object(Profiles.Select(p =>
            new KeyValuePair<string, JsonValue>(p.Key, ProfileToJson(p.Value))))));

    private static JsonValue ProfileToJson(Profile profile) => JsonValue.Object(
        ("mcps", JsonValue.Object(profile.Mcps.Select(m =>
            new KeyValuePair<string, JsonValue>(m.Key, EntryToJson(m.Value))))));

    private static JsonValue EntryToJson(McpEntry entry) => JsonValue.Object(
        ("enabled", JsonValue.Bool(entry.Enabled)),
        ("config", entry.Config),
        ("lastEditView", JsonValue.String(entry.LastEditView.ToJsonString())));

    /// <summary>
    /// Strict like Swift's synthesized Codable: every key required with the
    /// right type, unknown keys ignored. Throws <see cref="FormatException"/>.
    /// </summary>
    public static MasterStore FromJson(JsonValue json)
    {
        if (json.Kind != JsonKind.Object)
        {
            throw new FormatException("master store: top level is not an object");
        }
        var version = Require(json, "version", JsonKind.Int).IntValue;
        var active = Require(json, "activeProfile", JsonKind.String).StringValue;
        var profiles = Require(json, "profiles", JsonKind.Object).ObjectProperties
            .Select(p => new KeyValuePair<string, Profile>(p.Key, ProfileFromJson(p.Value)));
        return new MasterStore(checked((int)version), active, profiles);
    }

    private static Profile ProfileFromJson(JsonValue json)
    {
        if (json.Kind != JsonKind.Object)
        {
            throw new FormatException("master store: profile is not an object");
        }
        var mcps = Require(json, "mcps", JsonKind.Object).ObjectProperties
            .Select(m => new KeyValuePair<string, McpEntry>(m.Key, EntryFromJson(m.Value)));
        return new Profile(mcps);
    }

    private static McpEntry EntryFromJson(JsonValue json)
    {
        if (json.Kind != JsonKind.Object)
        {
            throw new FormatException("master store: entry is not an object");
        }
        var enabled = Require(json, "enabled", JsonKind.Bool).BoolValue;
        var config = json["config"] ?? throw new FormatException("master store: entry.config missing");
        var rawView = Require(json, "lastEditView", JsonKind.String).StringValue;
        if (!EditViewJson.TryParse(rawView, out var view))
        {
            throw new FormatException($"master store: unknown lastEditView '{rawView}'");
        }
        return new McpEntry(enabled, config, view);
    }

    private static JsonValue Require(JsonValue obj, string key, JsonKind kind)
    {
        var value = obj[key] ?? throw new FormatException($"master store: '{key}' missing");
        if (value.Kind != kind)
        {
            throw new FormatException($"master store: '{key}' is {value.TypeName}, expected {kind}");
        }
        return value;
    }

    // MARK: equality

    public bool Equals(MasterStore? other) =>
        other is not null
        && Version == other.Version
        && string.Equals(ActiveProfile, other.ActiveProfile, StringComparison.Ordinal)
        && DictionaryEquality.Equal(Profiles, other.Profiles);

    public override bool Equals(object? obj) => Equals(obj as MasterStore);

    public override int GetHashCode() =>
        HashCode.Combine(Version, ActiveProfile.GetHashCode(StringComparison.Ordinal), DictionaryEquality.Hash(Profiles));
}
