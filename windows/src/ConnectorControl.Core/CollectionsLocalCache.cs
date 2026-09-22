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

    /// <summary>
    /// What a collection's publish binding left behind when publishing stopped: this machine's
    /// memory of the paths it kept back and the folders it published into, kept so a collection
    /// published again still refuses them. A binding and a record never both hold a collection —
    /// starting again takes the record back into the binding.
    /// </summary>
    public IReadOnlyDictionary<string, KeptRecord> Kept { get; }

    /// <summary>
    /// The collection Claude's config was last written from on this machine. Claude's file holds
    /// that collection's connectors, so it is the only one a launch may ingest them into; null
    /// until the first apply that records it.
    /// </summary>
    public string? LastAppliedCollection { get; init; }

    public CollectionsLocalCache(
        IEnumerable<KeyValuePair<string, SyncedBinding>> synced,
        IEnumerable<KeyValuePair<string, PublishBinding>> published,
        IEnumerable<KeyValuePair<string, KeptRecord>>? kept = null,
        string? lastAppliedCollection = null)
    {
        Synced = new Dictionary<string, SyncedBinding>(synced, StringComparer.Ordinal);
        Published = new Dictionary<string, PublishBinding>(published, StringComparer.Ordinal);
        Kept = kept is null
            ? new Dictionary<string, KeptRecord>(StringComparer.Ordinal)
            : new Dictionary<string, KeptRecord>(kept, StringComparer.Ordinal);
        LastAppliedCollection = lastAppliedCollection;
    }

    /// <summary>The lists a stopped publish left behind, as <see cref="PublishBinding"/> holds them while it publishes.</summary>
    public sealed record KeptRecord
    {
        public IReadOnlySet<string> MarkedValues { get; }
        public IReadOnlySet<string> ReleasedValues { get; }
        public IReadOnlySet<string> PublishedFolders { get; }

        public KeptRecord(IEnumerable<string>? markedValues = null, IEnumerable<string>? releasedValues = null,
                          IEnumerable<string>? publishedFolders = null)
        {
            MarkedValues = new HashSet<string>(markedValues ?? [], StringComparer.Ordinal);
            ReleasedValues = new HashSet<string>(releasedValues ?? [], StringComparer.Ordinal);
            PublishedFolders = new HashSet<string>(publishedFolders ?? [], StringComparer.Ordinal);
        }

        public bool IsEmpty => MarkedValues.Count == 0 && ReleasedValues.Count == 0 && PublishedFolders.Count == 0;

        public bool Equals(KeptRecord? other) =>
            other is not null && MarkedValues.SetEquals(other.MarkedValues)
            && ReleasedValues.SetEquals(other.ReleasedValues) && PublishedFolders.SetEquals(other.PublishedFolders);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var set in new[] { MarkedValues, ReleasedValues, PublishedFolders })
            {
                hash.Add(set.Count);
                foreach (var value in set.Order(StringComparer.Ordinal))
                {
                    hash.Add(value, StringComparer.Ordinal);
                }
            }
            return hash.ToHashCode();
        }

        internal JsonValue Encode()
        {
            var props = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            foreach (var (key, values) in new (string, IReadOnlySet<string>)[]
                     { ("markedValues", MarkedValues), ("releasedValues", ReleasedValues), ("publishedFolders", PublishedFolders) })
            {
                if (values.Count > 0)
                {
                    props[key] = JsonValue.Array(values.Order(StringComparer.Ordinal).Select(JsonValue.String));
                }
            }
            return JsonValue.Object(props);
        }

        internal static KeptRecord Decode(JsonValue json, string what)
        {
            if (json.Kind != JsonKind.Object)
            {
                throw CollectionsFileException.Malformed($"{what} is not a JSON object");
            }
            return new KeptRecord(
                CollectionsFile.StringSet(json["markedValues"], $"{what} markedValues"),
                CollectionsFile.StringSet(json["releasedValues"], $"{what} releasedValues"),
                CollectionsFile.StringSet(json["publishedFolders"], $"{what} publishedFolders"));
        }
    }

    public bool Equals(CollectionsLocalCache? other) =>
        other is not null
        && DictionaryEquality.Equal(Synced, other.Synced)
        && DictionaryEquality.Equal(Published, other.Published)
        && DictionaryEquality.Equal(Kept, other.Kept)
        && string.Equals(LastAppliedCollection, other.LastAppliedCollection, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(
        DictionaryEquality.Hash(Synced),
        DictionaryEquality.Hash(Published),
        DictionaryEquality.Hash(Kept),
        LastAppliedCollection is null ? 0 : LastAppliedCollection.GetHashCode(StringComparison.Ordinal));

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

    public sealed record PublishBinding
    {
        public string Folder { get; }

        public string? LastWrittenHash { get; }

        /// <summary>
        /// Every path this machine has written into the document as a placeholder, which must never
        /// appear in it as written. Kept here, where nothing syncs it, so the publisher fails closed
        /// whatever the sidecar or the editor says: a publish that happens on its own only adds to
        /// it, and only the author, pressing Publish in the sheet after reading the preview,
        /// replaces it.
        /// </summary>
        public IReadOnlySet<string> MarkedValues { get; }

        /// <summary>
        /// Paths the author let travel as written in this collection's document, pressing Release
        /// and then Publish in the sheet after reading the preview, although this machine keeps them
        /// back elsewhere: on another collection's list, or as a folder it binds.
        /// </summary>
        public IReadOnlySet<string> ReleasedValues { get; }

        /// <summary>
        /// Every folder this binding has published into, the current one included. A connector can
        /// bring an earlier one back as written — a backup taken before the folder moved — and the
        /// author's old folder is no more a subscriber's than the current one.
        /// </summary>
        public IReadOnlySet<string> PublishedFolders { get; }

        public PublishBinding(string folder, string? lastWrittenHash, IEnumerable<string>? markedValues = null,
                              IEnumerable<string>? releasedValues = null, IEnumerable<string>? publishedFolders = null)
        {
            Folder = folder;
            LastWrittenHash = lastWrittenHash;
            MarkedValues = markedValues is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(markedValues, StringComparer.Ordinal);
            ReleasedValues = releasedValues is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(releasedValues, StringComparer.Ordinal);
            PublishedFolders = publishedFolders is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(publishedFolders, StringComparer.Ordinal);
        }

        public bool Equals(PublishBinding? other) =>
            other is not null
            && string.Equals(Folder, other.Folder, StringComparison.Ordinal)
            && string.Equals(LastWrittenHash, other.LastWrittenHash, StringComparison.Ordinal)
            && MarkedValues.SetEquals(other.MarkedValues)
            && ReleasedValues.SetEquals(other.ReleasedValues)
            && PublishedFolders.SetEquals(other.PublishedFolders);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Folder, StringComparer.Ordinal);
            hash.Add(LastWrittenHash ?? string.Empty, StringComparer.Ordinal);
            foreach (var value in MarkedValues.Order(StringComparer.Ordinal))
            {
                hash.Add(value, StringComparer.Ordinal);
            }
            hash.Add(ReleasedValues.Count);
            foreach (var value in ReleasedValues.Order(StringComparer.Ordinal))
            {
                hash.Add(value, StringComparer.Ordinal);
            }
            hash.Add(PublishedFolders.Count);
            foreach (var value in PublishedFolders.Order(StringComparer.Ordinal))
            {
                hash.Add(value, StringComparer.Ordinal);
            }
            return hash.ToHashCode();
        }

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
            // Sorted, so the file does not churn between saves that change nothing.
            if (MarkedValues.Count > 0)
            {
                props["markedValues"] = JsonValue.Array(MarkedValues.Order(StringComparer.Ordinal).Select(JsonValue.String));
            }
            if (ReleasedValues.Count > 0)
            {
                props["releasedValues"] = JsonValue.Array(ReleasedValues.Order(StringComparer.Ordinal).Select(JsonValue.String));
            }
            if (PublishedFolders.Count > 0)
            {
                props["publishedFolders"] = JsonValue.Array(PublishedFolders.Order(StringComparer.Ordinal).Select(JsonValue.String));
            }
            return JsonValue.Object(props);
        }

        internal static PublishBinding Decode(JsonValue json, string what)
        {
            if (json.Kind != JsonKind.Object)
            {
                throw CollectionsFileException.Malformed($"{what} is not a JSON object");
            }
            var folder = CollectionsFile.RequiredString(json["folder"], $"{what} folder");
            return new PublishBinding(
                folder,
                CollectionsFile.OptionalString(json["lastWrittenHash"], $"{what} lastWrittenHash"),
                // Absent in a cache written before the list was kept: nothing marked yet, which the
                // next write fills in.
                CollectionsFile.StringSet(json["markedValues"], $"{what} markedValues"),
                CollectionsFile.StringSet(json["releasedValues"], $"{what} releasedValues"),
                // The folder a binding names is one it publishes into, whether or not the list says
                // so: a binding written before the list was kept knows that much about itself.
                CollectionsFile.StringSet(json["publishedFolders"], $"{what} publishedFolders").Append(folder));
        }
    }

    // MARK: Encode

    public JsonValue Encode()
    {
        var root = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
        {
            ["version"] = JsonValue.Int(FormatVersion),
            ["synced"] = JsonValue.Object(Synced.Select(p => new KeyValuePair<string, JsonValue>(p.Key, p.Value.Encode()))),
            ["published"] = JsonValue.Object(Published.Select(p => new KeyValuePair<string, JsonValue>(p.Key, p.Value.Encode()))),
        };
        var remembered = Kept.Where(p => !p.Value.IsEmpty).ToList();
        if (remembered.Count > 0)
        {
            root["kept"] = JsonValue.Object(remembered.Select(p => new KeyValuePair<string, JsonValue>(p.Key, p.Value.Encode())));
        }
        if (LastAppliedCollection is not null)
        {
            root["lastAppliedCollection"] = JsonValue.String(LastAppliedCollection);
        }
        return JsonValue.Object(root);
    }

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
        var kept = new Dictionary<string, KeptRecord>(StringComparer.Ordinal);
        foreach (var (name, value) in CollectionsFile.ObjectValue(json["kept"], "kept"))
        {
            kept[name] = KeptRecord.Decode(value, $"kept \"{name}\"");
        }
        return new CollectionsLocalCache(
            synced, published, kept,
            // Absent in a cache written before it was recorded: the next apply records it.
            CollectionsFile.OptionalString(json["lastAppliedCollection"], "lastAppliedCollection"));
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

    /// <summary>
    /// Drops a binding the sidecar no longer vouches for: a source binding for a collection that is
    /// not synced any more, and a publish folder for one that is not published any more. What a
    /// stopped publish left behind is not a binding the sidecar vouches for, and is kept whatever it
    /// says: it is this machine's memory of what must not travel.
    /// </summary>
    public CollectionsLocalCache Reconciled(CollectionsFile file) => new(
        Synced.Where(p => file.KindOf(p.Key) == CollectionKind.Synced),
        Published.Where(p => file.Collections.TryGetValue(p.Key, out var entry) && entry.Publish is not null),
        Kept,
        LastAppliedCollection);
}
