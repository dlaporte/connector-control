namespace ConnectorControl.Core;

/// <summary>
/// The bindings only this machine knows: where a synced collection's document actually sits
/// here, what it hashed to the last time it was read, and which folder a published collection
/// writes to. Never synced — the sidecar beside the master list carries what every machine
/// shares.
///
/// Mirror: Sources/ConnectorControlCore/CollectionsLocalCache.swift
/// </summary>
/// <remarks>
/// Every property is <c>init</c>, so a change to one field is <c>cache with { … }</c> — the one
/// field assigned in place, as Swift mutates it — rather than a rebuild that has to name every
/// other one. The collections copy what they are given, ordinally, however they are set.
/// </remarks>
public sealed record CollectionsLocalCache
{
    public const string FileName = "collections-local.json";
    public const int FormatVersion = 1;

    private readonly IReadOnlyDictionary<string, SyncedBinding> synced;
    private readonly IReadOnlyDictionary<string, PublishBinding> published;
    private readonly IReadOnlyDictionary<string, KeptRecord> kept;
    private readonly IReadOnlySet<string>? lastAppliedNames;

    public IReadOnlyDictionary<string, SyncedBinding> Synced
    {
        get => synced;
        init => synced = new Dictionary<string, SyncedBinding>(value, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, PublishBinding> Published
    {
        get => published;
        init => published = new Dictionary<string, PublishBinding>(value, StringComparer.Ordinal);
    }

    /// <summary>
    /// What a collection's publish binding left behind when publishing stopped: this machine's
    /// memory of the paths it kept back and the folders it published into, kept so a collection
    /// published again still refuses them. Starting again takes the record back into the binding
    /// where it is that collection's own; what a collection that has since left the store left
    /// under the name stays beside the binding, since its folders are not the new one's to take.
    /// </summary>
    public IReadOnlyDictionary<string, KeptRecord> Kept
    {
        get => kept;
        init => kept = new Dictionary<string, KeptRecord>(value, StringComparer.Ordinal);
    }

    /// <summary>
    /// The collection Claude's config was last written from on this machine. Claude's file holds
    /// that collection's connectors, so it is the only one a launch may ingest them into; null
    /// until the first apply that records it.
    /// </summary>
    public string? LastAppliedCollection { get; init; }

    /// <summary>
    /// The connector names that apply wrote into Claude's file. They are what
    /// <see cref="LastAppliedCollection"/> rendered, so they still say which entries are its own
    /// once the collection itself is gone — deleted here, or on another machine, which leaves
    /// nothing else behind. null until the first apply that records them.
    /// </summary>
    public IReadOnlySet<string>? LastAppliedNames
    {
        get => lastAppliedNames;
        init => lastAppliedNames = value is null ? null : new HashSet<string>(value, StringComparer.Ordinal);
    }

    public CollectionsLocalCache(
        IEnumerable<KeyValuePair<string, SyncedBinding>> synced,
        IEnumerable<KeyValuePair<string, PublishBinding>> published,
        IEnumerable<KeyValuePair<string, KeptRecord>>? kept = null,
        string? lastAppliedCollection = null,
        IEnumerable<string>? lastAppliedNames = null)
    {
        this.synced = new Dictionary<string, SyncedBinding>(synced, StringComparer.Ordinal);
        this.published = new Dictionary<string, PublishBinding>(published, StringComparer.Ordinal);
        this.kept = new Dictionary<string, KeptRecord>(kept ?? [], StringComparer.Ordinal);
        LastAppliedCollection = lastAppliedCollection;
        this.lastAppliedNames = lastAppliedNames is null ? null : new HashSet<string>(lastAppliedNames, StringComparer.Ordinal);
    }

    /// <summary>
    /// The lists a stopped publish left behind, as <see cref="PublishBinding"/> holds them while it
    /// publishes, and the one no binding holds: the folders of the collections that bore the name
    /// before.
    /// </summary>
    public sealed record KeptRecord
    {
        public IReadOnlySet<string> MarkedValues { get; }
        public IReadOnlySet<string> ReleasedValues { get; }
        public IReadOnlySet<string> PublishedFolders { get; }

        /// <summary>
        /// The folders that collections which once bore this name, and have since left the store,
        /// published into. They are this machine's paths to keep back — refused as a releasable
        /// kept path, named in the dialog as that collection's folder — and never any collection's
        /// own, so <c>${COLLECTION_DIR}</c> never stands for them and no publish consumes them.
        /// Kept apart from <see cref="PublishedFolders"/> because a Stop would otherwise fold them
        /// into the next collection's own, and withdraw the release the dialog offered for them.
        /// </summary>
        public IReadOnlySet<string> DepartedFolders { get; }

        /// <summary>
        /// The origin the collection this record belongs to published under, while that collection
        /// is still the one bearing the name. A collection made with a deleted one's name is a
        /// different collection and clears it: the folders the old one left are another
        /// collection's to it, released rather than written over. Absent too in a record written
        /// before origins were kept, which is read the same way.
        /// </summary>
        public string? Origin { get; init; }

        public KeptRecord(IEnumerable<string>? markedValues, IEnumerable<string>? releasedValues,
                          IEnumerable<string>? publishedFolders, IEnumerable<string>? departedFolders,
                          string? origin = null)
        {
            MarkedValues = new HashSet<string>(markedValues ?? [], StringComparer.Ordinal);
            ReleasedValues = new HashSet<string>(releasedValues ?? [], StringComparer.Ordinal);
            PublishedFolders = new HashSet<string>(publishedFolders ?? [], StringComparer.Ordinal);
            DepartedFolders = new HashSet<string>(departedFolders ?? [], StringComparer.Ordinal);
            Origin = origin;
        }

        /// <summary>An origin alone says nothing about what must not travel, so a record holding only one is no record at all.</summary>
        public bool IsEmpty =>
            MarkedValues.Count == 0 && ReleasedValues.Count == 0 && PublishedFolders.Count == 0 && DepartedFolders.Count == 0;

        /// <summary>
        /// What a publish binding leaves behind when it goes, merged with anything already
        /// remembered under that name. The binding goes three ways — Stop Publishing, a delete made
        /// here, and a load finding the collection deleted or unpublished on another machine — and
        /// all three leave the same memory of what must not travel.
        /// </summary>
        /// <remarks>
        /// The earlier record's folders merge into the binding's own only where the two published
        /// under one origin, none on either side counting as one — a binding and a record written
        /// before origins were kept are read as one collection's. Otherwise the record is another
        /// collection's, one that bore the name and left, and its folders go to
        /// <see cref="DepartedFolders"/> with whatever it already held there: the next publish must
        /// not take them as its own.
        /// </remarks>
        public static KeptRecord Remembering(PublishBinding binding, KeptRecord? earlier)
        {
            var own = string.Equals(earlier?.Origin, binding.Origin, StringComparison.Ordinal);
            var earlierFolders = earlier?.PublishedFolders ?? Enumerable.Empty<string>();
            return new(
                binding.MarkedValues.Concat(earlier?.MarkedValues ?? Enumerable.Empty<string>()),
                binding.ReleasedValues.Concat(earlier?.ReleasedValues ?? Enumerable.Empty<string>()),
                binding.PublishedFolders.Append(binding.Folder)
                    .Concat(own ? earlierFolders : Enumerable.Empty<string>()),
                (earlier?.DepartedFolders ?? Enumerable.Empty<string>())
                    .Concat(own ? Enumerable.Empty<string>() : earlierFolders),
                binding.Origin ?? earlier?.Origin);
        }

        /// <summary>
        /// What a rename files under a name a departed collection left a record under. A rename
        /// can land on a record only where the collection that left it has gone — a live
        /// collection's name is refused — so the record already there is a departed collection's,
        /// or one written before origins were kept, read the same way, and the collection taking
        /// the name is a different one. Its folders are departed to it, as they would be to a
        /// collection made with the name, and its paths are inherited, as a re-used name inherits
        /// them; the moving record's own folders and origin stay its own, so the collection goes
        /// on being the one that published under them. A collection that never published moves no
        /// record, and leaves the one under the name as it found it, belonging to none.
        /// </summary>
        public static KeptRecord Renamed(KeptRecord? moving, KeptRecord displaced) =>
            moving is null
                ? displaced
                : new(
                    moving.MarkedValues.Concat(displaced.MarkedValues),
                    moving.ReleasedValues.Concat(displaced.ReleasedValues),
                    moving.PublishedFolders,
                    moving.DepartedFolders.Concat(displaced.PublishedFolders).Concat(displaced.DepartedFolders),
                    moving.Origin);

        public bool Equals(KeptRecord? other) =>
            other is not null && MarkedValues.SetEquals(other.MarkedValues)
            && ReleasedValues.SetEquals(other.ReleasedValues) && PublishedFolders.SetEquals(other.PublishedFolders)
            && DepartedFolders.SetEquals(other.DepartedFolders)
            && string.Equals(Origin, other.Origin, StringComparison.Ordinal);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var set in new[] { MarkedValues, ReleasedValues, PublishedFolders, DepartedFolders })
            {
                hash.Add(set.Count);
                foreach (var value in set.Order(StringComparer.Ordinal))
                {
                    hash.Add(value, StringComparer.Ordinal);
                }
            }
            hash.Add(Origin ?? string.Empty, StringComparer.Ordinal);
            return hash.ToHashCode();
        }

        internal JsonValue Encode()
        {
            var props = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            foreach (var (key, values) in new (string, IReadOnlySet<string>)[]
                     {
                         ("markedValues", MarkedValues), ("releasedValues", ReleasedValues),
                         ("publishedFolders", PublishedFolders), ("departedFolders", DepartedFolders),
                     })
            {
                if (values.Count > 0)
                {
                    props[key] = JsonValue.Array(values.Order(StringComparer.Ordinal).Select(JsonValue.String));
                }
            }
            if (Origin is not null)
            {
                props["origin"] = JsonValue.String(Origin);
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
                CollectionsFile.StringSet(json["publishedFolders"], $"{what} publishedFolders"),
                // Absent in a record written before they were kept apart: nothing departed yet.
                CollectionsFile.StringSet(json["departedFolders"], $"{what} departedFolders"),
                CollectionsFile.OptionalString(json["origin"], $"{what} origin"));
        }
    }

    public bool Equals(CollectionsLocalCache? other) =>
        other is not null
        && DictionaryEquality.Equal(Synced, other.Synced)
        && DictionaryEquality.Equal(Published, other.Published)
        && DictionaryEquality.Equal(Kept, other.Kept)
        && string.Equals(LastAppliedCollection, other.LastAppliedCollection, StringComparison.Ordinal)
        && (LastAppliedNames is null
            ? other.LastAppliedNames is null
            : other.LastAppliedNames is not null && LastAppliedNames.SetEquals(other.LastAppliedNames));

    public override int GetHashCode() => HashCode.Combine(
        DictionaryEquality.Hash(Synced),
        DictionaryEquality.Hash(Published),
        DictionaryEquality.Hash(Kept),
        LastAppliedCollection is null ? 0 : LastAppliedCollection.GetHashCode(StringComparison.Ordinal),
        LastAppliedNames is null ? 0 : LastAppliedNames.Count);

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

        /// <summary>
        /// The origin the collection publishes under, so what this binding leaves behind still says
        /// whose folders they were once the sidecar entry that named it is gone. Absent in a binding
        /// written before it was kept.
        /// </summary>
        public string? Origin { get; init; }

        public PublishBinding(string folder, string? lastWrittenHash, IEnumerable<string>? markedValues = null,
                              IEnumerable<string>? releasedValues = null, IEnumerable<string>? publishedFolders = null,
                              string? origin = null)
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
            Origin = origin;
        }

        public bool Equals(PublishBinding? other) =>
            other is not null
            && string.Equals(Folder, other.Folder, StringComparison.Ordinal)
            && string.Equals(LastWrittenHash, other.LastWrittenHash, StringComparison.Ordinal)
            && MarkedValues.SetEquals(other.MarkedValues)
            && ReleasedValues.SetEquals(other.ReleasedValues)
            && PublishedFolders.SetEquals(other.PublishedFolders)
            && string.Equals(Origin, other.Origin, StringComparison.Ordinal);

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
            hash.Add(Origin ?? string.Empty, StringComparer.Ordinal);
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
            if (Origin is not null)
            {
                props["origin"] = JsonValue.String(Origin);
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
                CollectionsFile.StringSet(json["publishedFolders"], $"{what} publishedFolders").Append(folder),
                // Absent in a binding written before the origin was kept: the next publish fills it in.
                CollectionsFile.OptionalString(json["origin"], $"{what} origin"));
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
        if (LastAppliedNames is not null)
        {
            root["lastAppliedNames"] = JsonValue.Array(LastAppliedNames.Order(StringComparer.Ordinal).Select(JsonValue.String));
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
            CollectionsFile.OptionalString(json["lastAppliedCollection"], "lastAppliedCollection"),
            // Absent, rather than empty, in a cache written before they were recorded: an apply that
            // rendered nothing records an empty list, which is not the same thing.
            json["lastAppliedNames"] is null
                ? null
                : CollectionsFile.StringSet(json["lastAppliedNames"], "lastAppliedNames"));
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
    /// <remarks>
    /// A publish binding dropped here is a collection deleted, or stopped, on another machine, which
    /// is how a collection disappears from a store that syncs. It leaves what stopping it here
    /// leaves: the paths it kept back, the paths the author released and the folders it published
    /// into. Nothing else on this machine remembers them — there is no record to union, and the
    /// sidecar entry that carried its marks went with it.
    /// </remarks>
    public CollectionsLocalCache Reconciled(CollectionsFile file)
    {
        var vouched = Published
            .Where(p => file.Collections.TryGetValue(p.Key, out var entry) && entry.Publish is not null)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var remembered = new Dictionary<string, KeptRecord>(Kept, StringComparer.Ordinal);
        foreach (var (name, binding) in Published.Where(p => !vouched.ContainsKey(p.Key)))
        {
            var record = KeptRecord.Remembering(binding, Kept.GetValueOrDefault(name));
            if (!record.IsEmpty)
            {
                remembered[name] = record;
            }
        }
        return this with
        {
            Synced = Synced.Where(p => file.KindOf(p.Key) == CollectionKind.Synced).ToDictionary(StringComparer.Ordinal),
            Published = vouched,
            Kept = remembered,
        };
    }
}
