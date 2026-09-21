namespace ConnectorControl.Core;

/// <summary>
/// The bindings only this machine knows: where a synced collection's document actually sits
/// here, what it hashed to the last time it was read, and which folder a published collection
/// writes to. Never synced — the sidecar beside the master list carries what every machine
/// shares.
///
/// Mirror: Sources/ConnectorControlCore/CollectionsLocalCache.swift
/// </summary>
public sealed record CollectionsLocalCache
{
    public const string FileName = "collections-local.json";
    public const int FormatVersion = 1;

    public IReadOnlyDictionary<string, SyncedBinding> Synced { get; }
    public IReadOnlyDictionary<string, PublishBinding> Published { get; }

    public CollectionsLocalCache(
        IEnumerable<KeyValuePair<string, SyncedBinding>> synced,
        IEnumerable<KeyValuePair<string, PublishBinding>> published)
    {
        Synced = new Dictionary<string, SyncedBinding>(synced, StringComparer.Ordinal);
        Published = new Dictionary<string, PublishBinding>(published, StringComparer.Ordinal);
    }

    public bool Equals(CollectionsLocalCache? other) =>
        other is not null
        && DictionaryEquality.Equal(Synced, other.Synced)
        && DictionaryEquality.Equal(Published, other.Published);

    public override int GetHashCode() => HashCode.Combine(DictionaryEquality.Hash(Synced), DictionaryEquality.Hash(Published));

    public sealed record SyncedBinding
    {
        /// <summary>Absent means the document has not been located on this machine.</summary>
        public string? Path { get; }

        public string? LastHash { get; }

        /// <summary>Connector → why this platform skipped it, so the pending diff can leave it out.</summary>
        public IReadOnlyDictionary<string, string> Excluded { get; }

        public SyncedBinding(string? path, string? lastHash, IEnumerable<KeyValuePair<string, string>>? excluded = null)
        {
            Path = path;
            LastHash = lastHash;
            Excluded = excluded is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(excluded, StringComparer.Ordinal);
        }

        public bool Equals(SyncedBinding? other) =>
            other is not null
            && string.Equals(Path, other.Path, StringComparison.Ordinal)
            && string.Equals(LastHash, other.LastHash, StringComparison.Ordinal)
            && DictionaryEquality.Equal(Excluded, other.Excluded);

        public override int GetHashCode() => HashCode.Combine(
            Path is null ? 0 : Path.GetHashCode(StringComparison.Ordinal),
            LastHash is null ? 0 : LastHash.GetHashCode(StringComparison.Ordinal),
            DictionaryEquality.Hash(Excluded));

        internal JsonValue Encode()
        {
            var props = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["excluded"] = JsonValue.FromObject(Excluded),
            };
            if (Path is not null)
            {
                props["path"] = JsonValue.String(Path);
            }
            if (LastHash is not null)
            {
                props["lastHash"] = JsonValue.String(LastHash);
            }
            return JsonValue.Object(props);
        }

        internal static SyncedBinding Decode(JsonValue json, string what)
        {
            if (json.Kind != JsonKind.Object)
            {
                throw CollectionsFileException.Malformed($"{what} is not a JSON object");
            }
            var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (connector, reason) in CollectionsFile.ObjectValue(json["excluded"], $"{what} excluded"))
            {
                excluded[connector] = CollectionsFile.RequiredString(reason, $"{what} excluded \"{connector}\"");
            }
            return new SyncedBinding(
                CollectionsFile.OptionalString(json["path"], $"{what} path"),
                CollectionsFile.OptionalString(json["lastHash"], $"{what} lastHash"),
                excluded);
        }
    }

    public sealed record PublishBinding(string Folder, string? LastWrittenHash)
    {
        internal JsonValue Encode()
        {
            var props = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["folder"] = JsonValue.String(Folder),
            };
            if (LastWrittenHash is not null)
            {
                props["lastWrittenHash"] = JsonValue.String(LastWrittenHash);
            }
            return JsonValue.Object(props);
        }

        internal static PublishBinding Decode(JsonValue json, string what)
        {
            if (json.Kind != JsonKind.Object)
            {
                throw CollectionsFileException.Malformed($"{what} is not a JSON object");
            }
            return new PublishBinding(
                CollectionsFile.RequiredString(json["folder"], $"{what} folder"),
                CollectionsFile.OptionalString(json["lastWrittenHash"], $"{what} lastWrittenHash"));
        }
    }

    // MARK: Encode

    public JsonValue Encode() => JsonValue.Object(
        ("version", JsonValue.Int(FormatVersion)),
        ("synced", JsonValue.Object(Synced.Select(p => new KeyValuePair<string, JsonValue>(p.Key, p.Value.Encode())))),
        ("published", JsonValue.Object(Published.Select(p => new KeyValuePair<string, JsonValue>(p.Key, p.Value.Encode())))));

    // MARK: Decode

    public static CollectionsLocalCache Decode(JsonValue json)
    {
        if (json.Kind != JsonKind.Object)
        {
            throw CollectionsFileException.Malformed("top level is not a JSON object");
        }
        if (json["version"] is not { Kind: JsonKind.Int } version)
        {
            throw CollectionsFileException.Malformed("version is missing");
        }
        if (version.IntValue != FormatVersion)
        {
            throw CollectionsFileException.Malformed($"version {version.IntValue} is not {FormatVersion}");
        }
        var synced = new Dictionary<string, SyncedBinding>(StringComparer.Ordinal);
        foreach (var (name, value) in CollectionsFile.ObjectValue(json["synced"], "synced"))
        {
            synced[name] = SyncedBinding.Decode(value, $"synced \"{name}\"");
        }
        var published = new Dictionary<string, PublishBinding>(StringComparer.Ordinal);
        foreach (var (name, value) in CollectionsFile.ObjectValue(json["published"], "published"))
        {
            published[name] = PublishBinding.Decode(value, $"published \"{name}\"");
        }
        return new CollectionsLocalCache(synced, published);
    }

    // MARK: Disk

    /// <summary>Missing or unreadable loads as empty, for the reason <see cref="CollectionsFile.Load"/> gives: every binding here can be found again, and the Locate banner asks for the one that cannot.</summary>
    public static CollectionsLocalCache Load(string path)
    {
        try
        {
            return Decode(JsonValue.Parse(File.ReadAllBytes(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or CollectionsFileException)
        {
            return new CollectionsLocalCache([], []);
        }
    }

    public AtomicWriteResult Save(string path) => AtomicFile.Write(Encode().Serialize(), path);

    /// <summary>Drops a binding the sidecar no longer vouches for: a source binding for a collection that is not synced any more, and a publish folder for one that is not published any more.</summary>
    public CollectionsLocalCache Reconciled(CollectionsFile file) => new(
        Synced.Where(p => file.KindOf(p.Key) == CollectionKind.Synced),
        Published.Where(p => file.Collections.TryGetValue(p.Key, out var entry) && entry.Publish is not null));
}
