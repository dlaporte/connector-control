namespace ConnectorControl.Core;

public enum CollectionKind
{
    Local,
    Synced,
}

public static class CollectionKinds
{
    public static string ToJsonString(this CollectionKind kind) => kind == CollectionKind.Local ? "local" : "synced";

    public static CollectionKind? Parse(string s) => s switch
    {
        "local" => CollectionKind.Local,
        "synced" => CollectionKind.Synced,
        _ => null,
    };
}

public sealed class CollectionsFileException(string message) : Exception(message)
{
    public static CollectionsFileException Malformed(string detail) => new("collections file: " + detail);
}

/// <summary>
/// The sidecar written beside the master list: for each collection that has something to say,
/// whether it is synced or published, where its document is relative to the store, and the
/// placeholders it carries. It travels with the master list, so it holds only what is true on
/// every machine — the per-machine bindings live in <see cref="CollectionsLocalCache"/>.
///
/// Mirror: Sources/ConnectorControlCore/CollectionsFile.swift
/// </summary>
public sealed record CollectionsFile
{
    public const string FileName = "collections.json";
    public const int FormatVersion = 1;

    /// <summary>Only collections with something to say: a local collection that was neither published nor imported from has no entry at all.</summary>
    public IReadOnlyDictionary<string, Entry> Collections { get; }

    public CollectionsFile(IEnumerable<KeyValuePair<string, Entry>> collections)
    {
        Collections = new Dictionary<string, Entry>(collections, StringComparer.Ordinal);
    }

    public bool Equals(CollectionsFile? other) => other is not null && DictionaryEquality.Equal(Collections, other.Collections);

    public override int GetHashCode() => DictionaryEquality.Hash(Collections);

    public sealed record Entry
    {
        public CollectionKind Kind { get; }

        /// <summary>Synced: the document's file name.</summary>
        public string? FileName { get; }

        /// <summary>Synced: the document's path relative to the store dir, when it lies inside that tree.</summary>
        public string? RelativeToStore { get; }

        /// <summary>Synced: the document's origin.</summary>
        public string? Origin { get; }

        /// <summary>Synced: connector → placeholder name → what the last Apply asked for.</summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, Need>> Needs { get; }

        /// <summary>Local: what publishing this collection fixed.</summary>
        public PublishRecord? Publish { get; }

        /// <summary>Local: connector → where an imported copy came from.</summary>
        public IReadOnlyDictionary<string, Provenance> Provenance { get; }

        public Entry(
            CollectionKind kind,
            string? fileName = null,
            string? relativeToStore = null,
            string? origin = null,
            IEnumerable<KeyValuePair<string, IReadOnlyDictionary<string, Need>>>? needs = null,
            PublishRecord? publish = null,
            IEnumerable<KeyValuePair<string, Provenance>>? provenance = null)
        {
            Kind = kind;
            FileName = fileName;
            RelativeToStore = relativeToStore;
            Origin = origin;
            Needs = needs is null
                ? new Dictionary<string, IReadOnlyDictionary<string, Need>>(StringComparer.Ordinal)
                : new Dictionary<string, IReadOnlyDictionary<string, Need>>(needs, StringComparer.Ordinal);
            Publish = publish;
            Provenance = provenance is null
                ? new Dictionary<string, Provenance>(StringComparer.Ordinal)
                : new Dictionary<string, Provenance>(provenance, StringComparer.Ordinal);
        }

        /// <summary>An ordinary local collection: the entry the file leaves out entirely.</summary>
        public static Entry Local { get; } = new(CollectionKind.Local);

        public bool Equals(Entry? other) =>
            other is not null
            && Kind == other.Kind
            && string.Equals(FileName, other.FileName, StringComparison.Ordinal)
            && string.Equals(RelativeToStore, other.RelativeToStore, StringComparison.Ordinal)
            && string.Equals(Origin, other.Origin, StringComparison.Ordinal)
            && DictionaryEquality.Equal(Needs, other.Needs, static (x, y) => DictionaryEquality.Equal(x, y))
            && Publish == other.Publish
            && DictionaryEquality.Equal(Provenance, other.Provenance);

        public override int GetHashCode() => HashCode.Combine(
            Kind,
            FileName is null ? 0 : FileName.GetHashCode(StringComparison.Ordinal),
            RelativeToStore is null ? 0 : RelativeToStore.GetHashCode(StringComparison.Ordinal),
            Origin is null ? 0 : Origin.GetHashCode(StringComparison.Ordinal),
            DictionaryEquality.Hash(Needs, static n => DictionaryEquality.Hash(n)),
            Publish,
            DictionaryEquality.Hash(Provenance));

        internal JsonValue Encode()
        {
            var props = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["kind"] = JsonValue.String(Kind.ToJsonString()),
            };
            if (FileName is not null)
            {
                props["fileName"] = JsonValue.String(FileName);
            }
            if (RelativeToStore is not null)
            {
                props["relativeToStore"] = JsonValue.String(RelativeToStore);
            }
            if (Origin is not null)
            {
                props["origin"] = JsonValue.String(Origin);
            }
            if (Needs.Count > 0)
            {
                props["needs"] = JsonValue.Object(Needs.Select(p => new KeyValuePair<string, JsonValue>(
                    p.Key,
                    JsonValue.Object(p.Value.Select(n => new KeyValuePair<string, JsonValue>(n.Key, n.Value.Encode()))))));
            }
            if (Publish is not null)
            {
                props["publish"] = Publish.Encode();
            }
            if (Provenance.Count > 0)
            {
                props["provenance"] = JsonValue.Object(Provenance.Select(p => new KeyValuePair<string, JsonValue>(p.Key, p.Value.Encode())));
            }
            return JsonValue.Object(props);
        }

        internal static Entry Decode(JsonValue json, string name)
        {
            var what = $"collection \"{name}\"";
            if (json.Kind != JsonKind.Object)
            {
                throw CollectionsFileException.Malformed($"{what} is not a JSON object");
            }
            var rawKind = RequiredString(json["kind"], $"{what} kind");
            var kind = CollectionKinds.Parse(rawKind)
                ?? throw CollectionsFileException.Malformed($"{what} kind \"{rawKind}\" is not local or synced");
            var needs = new Dictionary<string, IReadOnlyDictionary<string, Need>>(StringComparer.Ordinal);
            foreach (var (connector, byName) in ObjectValue(json["needs"], $"{what} needs"))
            {
                var forConnector = new Dictionary<string, Need>(StringComparer.Ordinal);
                foreach (var (needName, value) in ObjectValue(byName, $"{what} needs \"{connector}\""))
                {
                    forConnector[needName] = Need.Decode(value, $"{what} need \"{connector}\".\"{needName}\"");
                }
                needs[connector] = forConnector;
            }
            var provenance = new Dictionary<string, Provenance>(StringComparer.Ordinal);
            foreach (var (connector, value) in ObjectValue(json["provenance"], $"{what} provenance"))
            {
                provenance[connector] = CollectionsFile.Provenance.Decode(value, $"{what} provenance \"{connector}\"");
            }
            var publish = json["publish"] is { } rawPublish ? PublishRecord.Decode(rawPublish, $"{what} publish") : null;
            return new Entry(
                kind,
                OptionalString(json["fileName"], $"{what} fileName"),
                OptionalString(json["relativeToStore"], $"{what} relativeToStore"),
                OptionalString(json["origin"], $"{what} origin"),
                needs,
                publish,
                provenance);
        }
    }

    /// <summary>A collection with no entry is local — a profile the master list has and the sidecar has never had anything to add about.</summary>
    public CollectionKind KindOf(string name) => Collections.TryGetValue(name, out var entry) ? entry.Kind : CollectionKind.Local;

    // MARK: Encode

    public JsonValue Encode() => JsonValue.Object(
        ("version", JsonValue.Int(FormatVersion)),
        ("collections", JsonValue.Object(Collections
            .Where(p => p.Value != Entry.Local)
            .Select(p => new KeyValuePair<string, JsonValue>(p.Key, p.Value.Encode())))));

    // MARK: Decode

    public static CollectionsFile Decode(JsonValue json)
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
        var collections = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var (name, value) in ObjectValue(json["collections"], "collections"))
        {
            collections[name] = Entry.Decode(value, name);
        }
        return new CollectionsFile(collections);
    }

    // MARK: Disk

    /// <summary>
    /// A missing or unreadable sidecar loads as empty, and the file on disk is left exactly as
    /// it was: everything here is derived from the master list and the documents beside it, so
    /// losing it costs hints and origins the app can rebuild — unlike a corrupt mcps.json,
    /// there is nothing to move aside and preserve.
    /// </summary>
    public static CollectionsFile Load(string path)
    {
        try
        {
            return Decode(JsonValue.Parse(File.ReadAllBytes(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or CollectionsFileException)
        {
            return new CollectionsFile([]);
        }
    }

    public AtomicWriteResult Save(string path) => AtomicFile.Write(Encode().Serialize(), path);

    /// <summary>Drops entries whose name is not a collection in the store: the master list decides which collections exist, and the sidecar only ever annotates them.</summary>
    public CollectionsFile Reconciled(MasterStore store) =>
        new(Collections.Where(p => store.Collections.ContainsKey(p.Key)));

    // MARK: Decoding helpers
    // Every failure names the key it read, so a hand-edited sidecar says what is wrong with it.

    internal static string RequiredString(JsonValue? value, string what)
    {
        if (value is null)
        {
            throw CollectionsFileException.Malformed($"{what} is missing");
        }
        if (value.Kind != JsonKind.String)
        {
            throw CollectionsFileException.Malformed($"{what} is not a string");
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
            throw CollectionsFileException.Malformed($"{what} is not a string");
        }
        return value.StringValue;
    }

    internal static IReadOnlyDictionary<string, JsonValue> ObjectValue(JsonValue? value, string what)
    {
        if (value is null)
        {
            return new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        }
        if (value.Kind != JsonKind.Object)
        {
            throw CollectionsFileException.Malformed($"{what} is not a JSON object");
        }
        return value.ObjectProperties;
    }

    internal static IReadOnlySet<string> StringSet(JsonValue? value, string what)
    {
        if (value is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        if (value.Kind != JsonKind.Array)
        {
            throw CollectionsFileException.Malformed($"{what} is not an array");
        }
        var items = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < value.ArrayItems.Length; i++)
        {
            var item = value.ArrayItems[i];
            if (item.Kind != JsonKind.String)
            {
                throw CollectionsFileException.Malformed($"{what}[{i}] is not a string");
            }
            items.Add(item.StringValue);
        }
        return items;
    }

    internal static JsonPointer Pointer(string text, string what) =>
        JsonPointer.Parse(text) ?? throw CollectionsFileException.Malformed($"{what} \"{text}\" is not a JSON pointer");

    /// <summary>A placeholder the last Apply recorded: its hint, and where its marker sits inside the connector's config, so a filled value follows the marker when the author reorders arguments.</summary>
    public sealed record Need(string? Hint, JsonPointer Pointer)
    {
        internal JsonValue Encode() => JsonValue.Object(
            ("hint", Hint is null ? JsonValue.Null : JsonValue.String(Hint)),
            ("path", JsonValue.String(Pointer.ToString())));

        internal static Need Decode(JsonValue json, string what)
        {
            if (json.Kind != JsonKind.Object)
            {
                throw CollectionsFileException.Malformed($"{what} is not a JSON object");
            }
            return new Need(
                OptionalString(json["hint"], $"{what} hint"),
                // Qualified: this record's own Pointer member would shadow the helper.
                CollectionsFile.Pointer(RequiredString(json["path"], $"{what} path"), $"{what} path"));
        }
    }

    /// <summary>What publishing a collection fixed. The slug and origin are set when publishing starts and never re-derived, so a rename cannot orphan the document importers already hold.</summary>
    public sealed record PublishRecord(string Slug, string Origin, PublishIntent Intent)
    {
        internal JsonValue Encode()
        {
            var paths = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            foreach (var (connector, marks) in Intent.PathMarks)
            {
                var byPointer = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                foreach (var (pointer, mark) in marks)
                {
                    var fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                    {
                        ["name"] = JsonValue.String(mark.Name),
                        ["hint"] = mark.Hint is null ? JsonValue.Null : JsonValue.String(mark.Hint),
                    };
                    // The path itself, which this file may hold: it travels only among the
                    // author's own machines, beside a master list that already holds the same value.
                    if (mark.Value is not null)
                    {
                        fields["value"] = JsonValue.String(mark.Value);
                    }
                    byPointer[pointer.ToString()] = JsonValue.Object(fields);
                }
                paths[connector] = JsonValue.Object(byPointer);
            }
            return JsonValue.Object(
                ("slug", JsonValue.String(Slug)),
                ("origin", JsonValue.String(Origin)),
                // Sorted: a set has no order of its own, and the file must not churn between saves.
                ("shareValues", JsonValue.Object(Intent.ShareValues.Select(p => new KeyValuePair<string, JsonValue>(
                    p.Key, JsonValue.Array(p.Value.Order(StringComparer.Ordinal).Select(JsonValue.String)))))),
                ("paths", JsonValue.Object(paths)),
                ("hints", JsonValue.Object(Intent.Hints.Select(p => new KeyValuePair<string, JsonValue>(
                    p.Key, JsonValue.FromObject(p.Value))))));
        }

        internal static PublishRecord Decode(JsonValue json, string what)
        {
            if (json.Kind != JsonKind.Object)
            {
                throw CollectionsFileException.Malformed($"{what} is not a JSON object");
            }
            var shareValues = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
            foreach (var (connector, value) in ObjectValue(json["shareValues"], $"{what} shareValues"))
            {
                shareValues[connector] = StringSet(value, $"{what} shareValues \"{connector}\"");
            }
            var pathMarks = new Dictionary<string, IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark>>(StringComparer.Ordinal);
            foreach (var (connector, value) in ObjectValue(json["paths"], $"{what} paths"))
            {
                var marks = new Dictionary<JsonPointer, PublishIntent.PathMark>();
                foreach (var (rawPointer, mark) in ObjectValue(value, $"{what} paths \"{connector}\""))
                {
                    var markWhat = $"{what} path \"{connector}\".\"{rawPointer}\"";
                    if (mark.Kind != JsonKind.Object)
                    {
                        throw CollectionsFileException.Malformed($"{markWhat} is not a JSON object");
                    }
                    // A mark with no value is tolerated rather than refused: no release ever wrote
                    // one, but a build from before values were kept may have, and it still loads as
                    // the pointer-only mark it was.
                    marks[Pointer(rawPointer, markWhat)] = new PublishIntent.PathMark(
                        RequiredString(mark["name"], $"{markWhat} name"),
                        OptionalString(mark["hint"], $"{markWhat} hint"),
                        OptionalString(mark["value"], $"{markWhat} value"));
                }
                pathMarks[connector] = marks;
            }
            var hints = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            foreach (var (connector, value) in ObjectValue(json["hints"], $"{what} hints"))
            {
                var byName = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, hint) in ObjectValue(value, $"{what} hints \"{connector}\""))
                {
                    byName[key] = RequiredString(hint, $"{what} hint \"{connector}\".\"{key}\"");
                }
                hints[connector] = byName;
            }
            return new PublishRecord(
                RequiredString(json["slug"], $"{what} slug"),
                RequiredString(json["origin"], $"{what} origin"),
                new PublishIntent(shareValues, pathMarks, hints));
        }
    }

    /// <summary>Where an imported copy of a connector came from. The date is the local calendar date of the import, as text; the file never does date arithmetic.</summary>
    public sealed record Provenance(string From, string? Author, string Date)
    {
        internal JsonValue Encode()
        {
            var props = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["from"] = JsonValue.String(From),
                ["date"] = JsonValue.String(Date),
            };
            if (Author is not null)
            {
                props["author"] = JsonValue.String(Author);
            }
            return JsonValue.Object(props);
        }

        internal static Provenance Decode(JsonValue json, string what)
        {
            if (json.Kind != JsonKind.Object)
            {
                throw CollectionsFileException.Malformed($"{what} is not a JSON object");
            }
            return new Provenance(
                RequiredString(json["from"], $"{what} from"),
                OptionalString(json["author"], $"{what} author"),
                RequiredString(json["date"], $"{what} date"));
        }
    }
}
