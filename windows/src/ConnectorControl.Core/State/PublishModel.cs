namespace ConnectorControl.Core.State;

/// <summary>
/// The Publish/Export sheet: every environment value this collection carries and every argument
/// that looks like a path on this machine, with the ticks that decide what travels as a value and
/// what travels as a placeholder. The preview under them is the document itself, because the only
/// guarantee worth making about a secret is that the author saw every byte that leaves.
///
/// Mirror: Sources/ConnectorControlState/PublishModel.swift
/// </summary>
public sealed class PublishModel : ObservableObject
{
    public const string EnvSectionTitle = "Environment values · stripped unless shared";
    public const string PathsSectionTitle = "Machine-specific paths · found in arguments";
    public const string PreviewTitle = "Document preview";
    public const string ShareValueLabel = "share value";
    public const string HintPlaceholder = "hint for recipients";
    public const string PathNamePlaceholder = "placeholder name";
    public const string PublishButton = "Publish";
    public const string ExportButton = "Export…";

    public static string Title(string collection) => $"Publish “{collection}”";

    /// <summary>"this PC" is the platform-forced half of this sentence; the Mac mirror says "this Mac".</summary>
    public static string FolderLine(string fileName) => $"writes {fileName} from this PC on every change";

    public static string WarningLine(string connector, string warning) => $"{connector}: {warning}";

    /// <summary>
    /// One environment variable of one connector. Stripped by default: its name and hint travel,
    /// its value does not. A class, not a record: the sheet edits <see cref="Share"/> and
    /// <see cref="Hint"/> in place through two-way bindings.
    /// </summary>
    public sealed class EnvRow(string connector, string name, bool share, string hint)
    {
        public string Id { get; } = connector + "/env/" + name;
        public string Connector { get; } = connector;
        public string Name { get; } = name;
        public bool Share { get; set; } = share;
        public string Hint { get; set; } = hint;
    }

    /// <summary>
    /// One argument that looks like a path on this machine. Marking it replaces it with a
    /// placeholder every recipient fills in for themselves.
    /// </summary>
    public sealed class PathRow(string connector, JsonPointer pointer, string value, bool marked, string name, string hint)
    {
        public string Id { get; } = connector + pointer;
        public string Connector { get; } = connector;
        public JsonPointer Pointer { get; } = pointer;
        public string Value { get; } = value;
        public bool Marked { get; set; } = marked;
        public string Name { get; set; } = name;
        public string Hint { get; set; } = hint;
    }

    private readonly AppState state;
    private string? folder;

    public PublishModel(AppState state, string collection)
    {
        this.state = state;
        Collection = collection;
        // A collection that already publishes reopens showing what it publishes: the folder it
        // writes to and every tick the record remembers.
        var intent = state.CollectionsFile.Collections.GetValueOrDefault(collection)?.Publish?.Intent ?? PublishIntent.None;
        folder = state.CollectionsCache.Published.GetValueOrDefault(collection)?.Folder;
        var env = new List<EnvRow>();
        var paths = new List<PathRow>();
        var connectors = state.Store.Collections.TryGetValue(collection, out var held)
            ? held.Mcps
            : new Dictionary<string, McpEntry>(StringComparer.Ordinal);
        foreach (var name in connectors.Keys.Order(StringComparer.Ordinal))
        {
            var config = connectors[name].Config;
            IReadOnlySet<string> shared = intent.ShareValues.TryGetValue(name, out var s)
                ? s
                : new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyDictionary<string, string> hints = intent.Hints.TryGetValue(name, out var h)
                ? h
                : new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in EnvNames(config))
            {
                env.Add(new EnvRow(name, key, shared.Contains(key), hints.GetValueOrDefault(key) ?? string.Empty));
            }
            var arguments = Arguments(config);
            var found = 0;
            for (var index = 0; index < arguments.Count; index++)
            {
                if (!LooksLikeAPath(arguments[index]))
                {
                    continue;
                }
                found++;
                var pointer = new JsonPointer(["args", index.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                var mark = intent.PathMarks.TryGetValue(name, out var marks) ? marks.GetValueOrDefault(pointer) : null;
                paths.Add(new PathRow(name, pointer, arguments[index], mark is not null,
                                      mark?.Name ?? DefaultPathName(found), mark?.Hint ?? string.Empty));
            }
        }
        EnvRows = env;
        PathRows = paths;
    }

    public string Collection { get; }

    /// <summary>Where the document is written, null until the user chooses. Settable: the sheet's Choose Folder… is the only thing that fills it.</summary>
    public string? Folder { get => folder; set => Set(ref folder, value); }

    public IReadOnlyList<EnvRow> EnvRows { get; }

    public IReadOnlyList<PathRow> PathRows { get; }

    /// <summary>The Mac calls this <c>title</c>; here the static factory already owns that name.</summary>
    public string SheetTitle => Title(Collection);

    /// <summary>The document's name in the folder: the slug publishing fixed, or what this collection's name would make of it.</summary>
    public string FileName =>
        (state.CollectionsFile.Collections.GetValueOrDefault(Collection)?.Publish?.Slug ?? Slug.Make(Collection))
        + "." + CollectionDocument.FileExtension;

    /// <summary>The Mac calls this <c>folderLine</c>; here the static factory already owns that name.</summary>
    public string FolderSentence => FolderLine(FileName);

    public bool CanPublish => !string.IsNullOrEmpty(Folder);

    /// <summary>
    /// What the rows say, in the form the exporter reads. A marked row whose name is not a legal
    /// placeholder name is sanitized rather than dropped: the author ticked that row to keep a
    /// path on this machine out of the document, and silently publishing it because of how they
    /// spelled the name would be the one failure here nobody would notice.
    /// </summary>
    public PublishIntent Intent
    {
        get
        {
            var shareValues = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var hints = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var row in EnvRows)
            {
                if (row.Share)
                {
                    if (!shareValues.TryGetValue(row.Connector, out var names))
                    {
                        names = new HashSet<string>(StringComparer.Ordinal);
                        shareValues[row.Connector] = names;
                    }
                    names.Add(row.Name);
                }
                var hint = row.Hint.TrimSpaces();
                if (hint.Length == 0)
                {
                    continue;
                }
                if (!hints.TryGetValue(row.Connector, out var byName))
                {
                    byName = new Dictionary<string, string>(StringComparer.Ordinal);
                    hints[row.Connector] = byName;
                }
                byName[row.Name] = hint;
            }
            var pathMarks = new Dictionary<string, Dictionary<JsonPointer, PublishIntent.PathMark>>(StringComparer.Ordinal);
            foreach (var row in PathRows)
            {
                var name = PlaceholderName(row.Name);
                // Nothing to make a name out of is the one case left, and a marker with no name
                // in it is text nobody can fill: the row stays unmarked, which is visible in the
                // preview right under it.
                if (!row.Marked || name.Length == 0)
                {
                    continue;
                }
                if (!pathMarks.TryGetValue(row.Connector, out var marks))
                {
                    marks = [];
                    pathMarks[row.Connector] = marks;
                }
                var hint = row.Hint.TrimSpaces();
                marks[row.Pointer] = new PublishIntent.PathMark(name, hint.Length == 0 ? null : hint);
            }
            return new PublishIntent(
                shareValues.Select(p => new KeyValuePair<string, IReadOnlySet<string>>(p.Key, p.Value)),
                pathMarks.Select(p => new KeyValuePair<string, IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark>>(p.Key, p.Value)),
                hints.Select(p => new KeyValuePair<string, IReadOnlyDictionary<string, string>>(p.Key, p.Value)));
        }
    }

    /// <summary>The document itself, as the editor would show it. Every byte that leaves this machine is in here.</summary>
    public string Preview => state.ExportDocument(Collection, Intent).Encode().EditorText();

    /// <summary>
    /// What the exporter cannot know is a secret: a value that looks like a credential and is
    /// about to travel. Never an edit — the author decides.
    /// </summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var intent = Intent;
            var connectors = state.Store.Collections.TryGetValue(Collection, out var held)
                ? held.Mcps
                : new Dictionary<string, McpEntry>(StringComparer.Ordinal);
            var warnings = new List<string>();
            foreach (var name in connectors.Keys.Order(StringComparer.Ordinal))
            {
                IReadOnlySet<string> shared = intent.ShareValues.TryGetValue(name, out var s)
                    ? s
                    : new HashSet<string>(StringComparer.Ordinal);
                warnings.AddRange(CollectionDocument.CredentialWarnings(connectors[name].Config, shared)
                    .Select(warning => WarningLine(name, warning)));
            }
            return warnings;
        }
    }

    /// <summary>
    /// Publish, or re-publish with what the sheet now says. A folder that is not the one on record
    /// starts publishing again there, which is how the failed-write banner's Choose Folder… moves
    /// a collection. null on success.
    /// </summary>
    public string? Publish()
    {
        var chosen = Folder?.TrimSpaces() ?? string.Empty;
        if (chosen.Length == 0)
        {
            return null;
        }
        if (!state.IsPublished(Collection)
            || !string.Equals(state.CollectionsCache.Published.GetValueOrDefault(Collection)?.Folder, chosen, StringComparison.Ordinal))
        {
            return state.StartPublishing(Collection, chosen, Intent);
        }
        // The same folder, already publishing: the ticks go on record, and then the document is
        // written whether or not it changed. Pressing Publish again is how a write that failed is
        // retried, and by then nothing about the document is different — only the folder is.
        state.UpdatePublishIntent(Collection, Intent);
        return state.Republish(Collection);
    }

    /// <summary>The same document, written once, binding nothing. null on success.</summary>
    public string? Export(string path) => state.WriteExport(Collection, Intent, path);

    // MARK: rows

    /// <summary>The environment variables the exporter will read, from the same place it reads them: a remote connector's are its passthrough env, a local one's are the config's own.</summary>
    private static IReadOnlyList<string> EnvNames(JsonValue config) =>
        RemotePattern.Decode(config) is { } remote
            ? remote.PassthroughEnv.Keys.Order(StringComparer.Ordinal).ToList()
            : FormMapper.Analyze(config).Model.Env.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>Only a local connector's arguments are the author's own. A remote connector's are built by the launcher on each machine, so there is nothing there to mark.</summary>
    private static IReadOnlyList<string> Arguments(JsonValue config) =>
        RemotePattern.Decode(config) is not null ? [] : FormMapper.Analyze(config).Model.Args;

    /// <summary>An argument worth offering as machine-specific: it is written as a path, or something by that name is on this disk. A flag or a URL is neither.</summary>
    private static bool LooksLikeAPath(string argument)
    {
        if (Placeholder.ContainsMarker(argument))
        {
            return false;
        }
        if (argument.StartsWith('/') || argument.StartsWith('~')
            || argument.StartsWith("./", StringComparison.Ordinal) || argument.StartsWith("../", StringComparison.Ordinal))
        {
            return true;
        }
        // A Windows path written on either platform: one letter, a colon, a backslash.
        if (argument.Length >= 3 && char.IsLetter(argument[0]) && argument[1] == ':' && argument[2] == '\\')
        {
            return true;
        }
        try
        {
            return File.Exists(argument) || Directory.Exists(argument);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>"path", then "path_2", "path_3" — numbered inside each connector, since a recipient fills one connector's placeholders at a time.</summary>
    private static string DefaultPathName(int index) =>
        index <= 1 ? "path" : "path_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A legal placeholder name out of whatever the author typed: anything outside
    /// <c>[A-Za-z0-9_]</c> becomes "_", and a leading digit takes a "p_" prefix, since a marker
    /// name may not start with one. Empty for a name that is only whitespace — there is nothing
    /// there to make a name out of.
    /// </summary>
    internal static string PlaceholderName(string typed)
    {
        var trimmed = typed.TrimSpaces();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }
        // Per rune, as the Mac mirror walks Unicode scalars: one character the name cannot carry
        // becomes one underscore on both platforms.
        var name = new System.Text.StringBuilder(trimmed.Length);
        foreach (var rune in trimmed.EnumerateRunes())
        {
            var text = rune.ToString();
            name.Append(Placeholder.IsValidName(text) ? text : "_");
        }
        return char.IsAsciiDigit(name[0]) ? "p_" + name : name.ToString();
    }
}
