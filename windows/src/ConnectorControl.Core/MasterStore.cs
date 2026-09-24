namespace ConnectorControl.Core;

/// <summary>
/// The master list (schema v2, collection-aware). Mutable like the Swift struct's
/// `var` usage; structural equality like the Swift value type. Use
/// <see cref="Clone"/> where Swift relied on copy semantics.
/// </summary>
public sealed class MasterStore : IEquatable<MasterStore>
{
    // Schema v2 only — no v1 fallback. A v1 (or otherwise malformed) file on
    // disk fails to decode and is handled by MasterStoreIO.Load's existing
    // corrupt-file path: moved aside and rebuilt fresh from Claude's config.
    public const long CurrentVersion = 2;

    public long Version { get; }
    public string ActiveCollection { get; set; }
    public Dictionary<string, Collection> Collections { get; }

    public MasterStore(long version, string activeCollection, IEnumerable<KeyValuePair<string, Collection>> collections)
    {
        Version = version;
        Collections = new Dictionary<string, Collection>(collections, StringComparer.Ordinal);
        // The Mcps getter must never create a collection as a side effect, so this constructor is
        // the one place that guarantees Collections[ActiveCollection] exists — including a decoded
        // file that names a collection it doesn't have (MasterStoreIO.Load relies on this).
        if (Collections.ContainsKey(activeCollection))
        {
            ActiveCollection = activeCollection;
        }
        else if (Collections.Keys.Order(StringComparer.Ordinal).FirstOrDefault() is { } fallback)
        {
            ActiveCollection = fallback;
        }
        else
        {
            Collections["Default"] = new Collection();
            ActiveCollection = "Default";
        }
    }

    /// <summary>Swift <c>MasterStore(version:mcps:)</c>: a single "Default" collection; always v2.</summary>
    public MasterStore(IEnumerable<KeyValuePair<string, McpEntry>> mcps)
        : this(CurrentVersion, "Default", [new KeyValuePair<string, Collection>("Default", new Collection(mcps))])
    {
    }

    /// <summary>Swift <c>.empty</c>. A fresh instance every call — this type is mutable.</summary>
    public static MasterStore Empty() =>
        new(CurrentVersion, "Default", [new KeyValuePair<string, Collection>("Default", new Collection())]);

    /// <summary>
    /// The active collection's connectors — the view the entire app operates on. Side-effect free:
    /// an active collection that somehow doesn't exist yet returns an empty, unstored dictionary
    /// rather than creating one — the constructor is what normally guarantees
    /// Collections[ActiveCollection] exists.
    /// </summary>
    public Dictionary<string, McpEntry> Mcps =>
        Collections.TryGetValue(ActiveCollection, out var collection) ? collection.Mcps : new Dictionary<string, McpEntry>(StringComparer.Ordinal);

    /// <summary>Claude's <c>mcpServers</c> section rendered from this store: the enabled subset's configs.</summary>
    public IReadOnlyDictionary<string, JsonValue> EnabledServers =>
        Mcps.Where(p => p.Value.Enabled).ToDictionary(p => p.Key, p => p.Value.Config, StringComparer.Ordinal);

    /// <summary>How many connectors are enabled, without building the config dictionary <see cref="EnabledServers"/> does.</summary>
    public int EnabledCount => Mcps.Count(p => p.Value.Enabled);

    /// <summary>
    /// The name a collection is kept under for the one typed: spaces trimmed from both ends.
    /// Adding and renaming both apply it, so a caller that follows the collection it just named
    /// asks here rather than trimming again.
    /// </summary>
    public static string CollectionName(string typed) => typed.TrimSpaces();

    /// <summary>
    /// null on success, else a user-facing error message. The new collection becomes the active
    /// one unless <paramref name="activating"/> is false.
    /// </summary>
    public string? AddCollection(string name, bool copyingCurrent, bool activating = true)
    {
        var trimmed = CollectionName(name);
        if (trimmed.Length == 0)
        {
            return "Name must not be empty.";
        }
        if (Collections.ContainsKey(trimmed))
        {
            return $"A collection named “{trimmed}” already exists.";
        }
        Collections[trimmed] = copyingCurrent ? new Collection(Mcps) : new Collection();
        if (activating)
        {
            ActiveCollection = trimmed;
        }
        return null;
    }

    /// <summary>null on success, else a user-facing error message. Renaming the active collection keeps it active under its new name.</summary>
    public string? RenameCollection(string name, string newName)
    {
        if (!Collections.ContainsKey(name))
        {
            return NoCollectionError(name);
        }
        var trimmed = CollectionName(newName);
        if (trimmed.Length == 0)
        {
            return "Name must not be empty.";
        }
        if (trimmed != name && Collections.ContainsKey(trimmed))
        {
            return $"A collection named “{trimmed}” already exists.";
        }
        if (!Collections.Remove(name, out var current))
        {
            return null;
        }
        Collections[trimmed] = current;
        if (ActiveCollection == name)
        {
            ActiveCollection = trimmed;
        }
        return null;
    }

    /// <summary>
    /// null on success, else a user-facing error message. Refuses to delete the last remaining
    /// collection. Deleting the active collection hands the sorted-first remaining collection
    /// the active spot.
    /// </summary>
    public string? DeleteCollection(string name)
    {
        if (!Collections.ContainsKey(name))
        {
            return NoCollectionError(name);
        }
        if (Collections.Count <= 1)
        {
            return "Can’t delete the last collection.";
        }
        var successor = ActiveAfterDeleting(name);
        Collections.Remove(name);
        if (ActiveCollection == name)
        {
            ActiveCollection = successor ?? "Default";
        }
        return null;
    }

    /// <summary>
    /// The collection that takes the active spot if <paramref name="name"/> is deleted: the
    /// sorted-first of the rest, or null when none remain. The one rule, so the Delete
    /// confirmation that names it cannot disagree with the delete that picks it.
    /// </summary>
    public string? ActiveAfterDeleting(string name) =>
        Collections.Keys.Where(k => k != name).Order(StringComparer.Ordinal).FirstOrDefault();

    /// <summary>The one wording for a name no collection has, shared by switch, rename and delete.</summary>
    private static string NoCollectionError(string name) => $"No collection named “{name}”.";

    public string? SwitchCollection(string name)
    {
        if (!Collections.ContainsKey(name))
        {
            return NoCollectionError(name);
        }
        ActiveCollection = name;
        return null;
    }

    public MasterStore Clone() =>
        new(Version, ActiveCollection, Collections.Select(p => new KeyValuePair<string, Collection>(p.Key, p.Value.Clone())));

    // MARK: JSON (the Swift Codable synthesis, made explicit)

    // The file keeps the v2 key names: machines on the current release share it through the
    // synced master-list folder, and their decoder knows only these two keys.
    public JsonValue ToJson() => JsonValue.Object(
        ("version", JsonValue.Int(Version)),
        ("activeProfile", JsonValue.String(ActiveCollection)),
        ("profiles", JsonValue.Object(Collections.Select(p =>
            new KeyValuePair<string, JsonValue>(p.Key, CollectionToJson(p.Value))))));

    private static JsonValue CollectionToJson(Collection collection) => JsonValue.Object(
        ("mcps", JsonValue.Object(collection.Mcps.Select(m =>
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
        // The file keeps the v2 key names: machines on the current release share it through the
        // synced master-list folder, and their decoder knows only these two keys.
        var active = Require(json, "activeProfile", JsonKind.String).StringValue;
        var collections = Require(json, "profiles", JsonKind.Object).ObjectProperties
            .Select(p => new KeyValuePair<string, Collection>(p.Key, CollectionFromJson(p.Value)));
        return new MasterStore(Require(json, "version", JsonKind.Int).IntValue, active, collections);
    }

    private static Collection CollectionFromJson(JsonValue json)
    {
        if (json.Kind != JsonKind.Object)
        {
            throw new FormatException("master store: collection is not an object");
        }
        var mcps = Require(json, "mcps", JsonKind.Object).ObjectProperties
            .Select(m => new KeyValuePair<string, McpEntry>(m.Key, EntryFromJson(m.Value)));
        return new Collection(mcps);
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
        && string.Equals(ActiveCollection, other.ActiveCollection, StringComparison.Ordinal)
        && DictionaryEquality.Equal(Collections, other.Collections);

    public override bool Equals(object? obj) => Equals(obj as MasterStore);

    public override int GetHashCode() =>
        HashCode.Combine(Version, ActiveCollection.GetHashCode(StringComparison.Ordinal), DictionaryEquality.Hash(Collections));
}
