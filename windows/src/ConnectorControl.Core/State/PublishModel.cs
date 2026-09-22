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
    public const string CancelButton = "Cancel";
    /// <summary>This sheet's own folder picker, not the failed-publish banner's button of the same words: a sheet's buttons are its model's, as Settings' and the Import sheet's already are.</summary>
    public const string ChooseFolderButton = "Choose Folder…";
    /// <summary>What a screen reader says for the bare tick beside a path row, which has no visible label.</summary>
    public const string MarkPathLabel = "Mark as a path this machine supplies";
    /// <summary>The button beside an unresolved mark's note: drop the mark and let the path travel as the preview shows it.</summary>
    public const string ForgetMarkButton = "Forget Mark";

    public static string Title(string collection) => $"Publish “{collection}”";

    /// <summary>
    /// The sheet's own title in export mode. No trailing ellipsis: the one on the menu item that
    /// opens it (<see cref="FlyoutModel.ExportTitleFor"/>) says a sheet follows, and this is that sheet.
    /// </summary>
    public static string ExportTitle(string collection) => $"Export “{collection}”";

    /// <summary>"this PC" is the platform-forced half of this sentence; the Mac mirror says "this Mac".</summary>
    public static string FolderLine(string fileName) => $"writes {fileName} from this PC on every change";

    public static string WarningLine(string connector, string warning) => $"{connector}: {warning}";

    public static string FooterLine(string fileName, string originShort) => $"{fileName} · {originShort}";

    public static string UnresolvedMarkNote(string connector) =>
        $"A path marked in “{connector}” has moved. Tick it where it now sits, or forget the mark.";

    /// <summary>
    /// One environment variable of one connector. Stripped by default: its name and hint travel,
    /// its value does not. A class, not a record: the sheet edits <see cref="Share"/> and
    /// <see cref="Hint"/> in place through two-way bindings. It raises PropertyChanged so the
    /// model can re-raise what a tick changes; the Mac needs none of this, because its rows are
    /// structs inside a @Published array and the array itself is what announces the edit.
    /// </summary>
    public sealed class EnvRow(string connector, string name, string value, bool share, string hint) : ObservableObject
    {
        private bool share = share;
        private string hint = hint;

        public string Id { get; } = connector + "/env/" + name;
        public string Connector { get; } = connector;
        public string Name { get; } = name;

        /// <summary>
        /// What the variable holds now, in full and unelided, so the tick beside it is a decision
        /// made with the value in view. Shortening it is the sheet's business, not the model's.
        /// </summary>
        public string Value { get; } = value;

        public bool Share { get => share; set => Set(ref share, value); }

        public string Hint { get => hint; set => Set(ref hint, value); }
    }

    /// <summary>
    /// One argument that looks like a path on this machine. Marking it replaces it with a
    /// placeholder every recipient fills in for themselves. Raises PropertyChanged for the reason
    /// <see cref="EnvRow"/> does.
    /// </summary>
    public sealed class PathRow(string connector, JsonPointer pointer, string value, bool marked, string name, string hint)
        : ObservableObject
    {
        private bool marked = marked;
        private string name = name;
        private string hint = hint;

        public string Id { get; } = connector + pointer;
        public string Connector { get; } = connector;
        public JsonPointer Pointer { get; } = pointer;
        public string Value { get; } = value;

        public bool Marked { get => marked; set => Set(ref marked, value); }

        public string Name { get => name; set => Set(ref name, value); }

        public string Hint { get => hint; set => Set(ref hint, value); }
    }

    private readonly AppState state;
    private string? folder;
    /// <summary>
    /// Connectors whose recorded path mark found no argument when the dialog opened, and those the
    /// collection no longer holds that still carry one. Kept rather than dropped: the rows alone
    /// show such a path unticked, and publishing or exporting what they say would send it as
    /// written. Each waits for the author to tick the path where it now sits, or to forget it.
    /// </summary>
    private readonly HashSet<string> lostMarks = new(StringComparer.Ordinal);
    /// <summary>The path rows ticked when the dialog opened. Those are marks already on record, so only a tick made since can stand in for a lost one.</summary>
    private readonly HashSet<string> tickedAtOpen = new(StringComparer.Ordinal);

    public PublishModel(AppState state, string collection, IReadOnlyList<string>? connectors = null)
    {
        this.state = state;
        Collection = collection;
        Connectors = connectors;
        // A collection that already publishes reopens showing what it publishes: the folder it
        // writes to and every tick the record remembers.
        var intent = state.CollectionsFile.Collections.GetValueOrDefault(collection)?.Publish?.Intent ?? PublishIntent.None;
        folder = state.CollectionsCache.Published.GetValueOrDefault(collection)?.Folder;
        var env = new List<EnvRow>();
        var paths = new List<PathRow>();
        var seeded = Held(state, collection, connectors);
        foreach (var name in seeded.Keys.Order(StringComparer.Ordinal))
        {
            var config = seeded[name].Config;
            IReadOnlySet<string> shared = intent.ShareValues.TryGetValue(name, out var s)
                ? s
                : new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyDictionary<string, string> hints = intent.Hints.TryGetValue(name, out var h)
                ? h
                : new Dictionary<string, string>(StringComparer.Ordinal);
            var variables = Env(config);
            foreach (var key in variables.Keys.Order(StringComparer.Ordinal))
            {
                env.Add(new EnvRow(name, key, variables[key], shared.Contains(key),
                                   hints.GetValueOrDefault(key) ?? string.Empty));
            }
            var arguments = Arguments(config);
            var found = 0;
            // The ticks sit where the exporter would place them, not where the record says they
            // were made: an argument that moved since keeps its tick, and a mark that has lost its
            // argument ticks nothing, which is the dialog asking for it to be marked again. A
            // marked argument keeps its row even once it stops looking like a path (the file it
            // named is gone), or publishing from the dialog would quietly unmark it.
            var placement = PublishIntent.PlacePathMarks(
                intent.PathMarks.TryGetValue(name, out var marks) ? marks : new Dictionary<JsonPointer, PublishIntent.PathMark>(),
                arguments);
            var placed = placement.Placed;
            if (placement.Unresolved.Count > 0)
            {
                lostMarks.Add(name);
            }
            for (var index = 0; index < arguments.Count; index++)
            {
                var mark = placed.GetValueOrDefault(index);
                if (!LooksLikeAPath(arguments[index]) && mark is null)
                {
                    continue;
                }
                found++;
                var pointer = new JsonPointer(["args", index.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                paths.Add(new PathRow(name, pointer, arguments[index], mark is not null,
                                      mark?.Name ?? DefaultPathName(found), mark?.Hint ?? string.Empty));
            }
        }
        // A mark for a connector the collection no longer holds was made on one renamed or removed
        // where the record could not follow, and the exporter refuses it whatever the subset. No
        // row can be ticked for it, so it waits to be forgotten.
        var all = state.Store.Collections.GetValueOrDefault(collection)?.Mcps;
        foreach (var (name, marks) in intent.PathMarks)
        {
            if ((all is null || !all.ContainsKey(name)) && marks.Values.Any(mark => mark.Value is not null))
            {
                lostMarks.Add(name);
            }
        }
        tickedAtOpen.UnionWith(paths.Where(row => row.Marked).Select(row => row.Id));
        ReplaceRows(env, paths);
    }

    public string Collection { get; }

    /// <summary>
    /// The connectors this dialog speaks for: the Export dialog's ticked subset, or null for the
    /// whole collection. Publishing always writes the whole collection, so a model built with a
    /// subset is an export's — <see cref="Publish"/> on one would record an intent that speaks
    /// for only part of what the document carries.
    /// </summary>
    public IReadOnlyList<string>? Connectors { get; }

    /// <summary>The connectors a dialog over <paramref name="collection"/> speaks for, which is every one of them unless an export ticked a subset.</summary>
    private static IReadOnlyDictionary<string, McpEntry> Held(
        AppState state, string collection, IReadOnlyList<string>? only)
    {
        var all = state.Store.Collections.TryGetValue(collection, out var held)
            ? held.Mcps
            : new Dictionary<string, McpEntry>(StringComparer.Ordinal);
        if (only is null)
        {
            return all;
        }
        var keep = only.ToHashSet(StringComparer.Ordinal);
        return all.Where(pair => keep.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    /// <summary>Where the document is written, null until the user chooses. Settable: the sheet's Choose Folder… is the only thing that fills it.</summary>
    public string? Folder
    {
        get => folder;
        set
        {
            if (Set(ref folder, value))
            {
                Raise(nameof(CanPublish));
            }
        }
    }

    public IReadOnlyList<EnvRow> EnvRows { get; private set; } = [];

    public IReadOnlyList<PathRow> PathRows { get; private set; } = [];

    /// <summary>
    /// Whether each section has anything to show. A collection of remote connectors with no
    /// passthrough environment has neither, and an empty heading over nothing is worse than no
    /// heading; the sheet binds these rather than counting rows itself.
    /// </summary>
    public bool HasEnvRows => EnvRows.Count > 0;

    public bool HasPathRows => PathRows.Count > 0;

    /// <summary>
    /// Takes a new set of rows, listening to each one and letting the previous set go. Every tick
    /// and every keystroke in a row changes what the document says, so the model re-raises what
    /// the rows feed rather than leaving the sheet to refresh itself. The rows live and die with
    /// this model, so there is nothing to unsubscribe beyond a replacement.
    /// </summary>
    private void ReplaceRows(IReadOnlyList<EnvRow> env, IReadOnlyList<PathRow> paths)
    {
        foreach (var row in EnvRows)
        {
            row.PropertyChanged -= OnRowChanged;
        }
        foreach (var row in PathRows)
        {
            row.PropertyChanged -= OnRowChanged;
        }
        EnvRows = env;
        PathRows = paths;
        foreach (var row in env)
        {
            row.PropertyChanged += OnRowChanged;
        }
        foreach (var row in paths)
        {
            row.PropertyChanged += OnRowChanged;
        }
        Raise(nameof(EnvRows));
        Raise(nameof(PathRows));
        Raise(nameof(HasEnvRows));
        Raise(nameof(HasPathRows));
    }

    /// <summary>Everything below the rows is derived from them, and nothing above is.</summary>
    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Raise(nameof(Intent));
        Raise(nameof(Preview));
        Raise(nameof(Warnings));
        // A tick, or the name beside it, is what answers a lost mark.
        if (sender is PathRow)
        {
            RaiseMarkGates();
        }
    }

    /// <summary>The Mac calls this <c>title</c>; here the static factory already owns that name.</summary>
    public string SheetTitle => Title(Collection);

    /// <summary>The document's name in the folder: the slug publishing fixed, or what this collection's name would make of it.</summary>
    public string FileName =>
        (state.CollectionsFile.Collections.GetValueOrDefault(Collection)?.Publish?.Slug ?? Slug.Make(Collection))
        + "." + CollectionDocument.FileExtension;

    /// <summary>The Mac calls this <c>folderLine</c>; here the static factory already owns that name.</summary>
    public string FolderSentence => FolderLine(FileName);

    /// <summary>
    /// The eight characters of the origin the footer shows — enough to tell one publisher's
    /// document from another's at a glance, which is all the footer is for. Empty until the
    /// collection has published once, because that is when the origin is minted. Read from the
    /// publish record rather than through <c>ExportDocument</c>, which carries the same value and
    /// renders every connector to get there.
    /// </summary>
    public string OriginShort
    {
        get
        {
            var origin = state.CollectionsFile.Collections.GetValueOrDefault(Collection)?.Publish?.Origin ?? "";
            return origin.Length <= 8 ? origin : origin[..8];
        }
    }

    /// <summary>
    /// The footer: the file name, and the origin once there is one. The Mac calls this
    /// <c>footerLine</c>; here the static factory already owns that name, as with
    /// <see cref="FolderSentence"/>.
    /// </summary>
    public string FooterSentence
    {
        get
        {
            var origin = OriginShort;
            return origin.Length == 0 ? FileName : FooterLine(FileName, origin);
        }
    }

    /// <summary>Nothing is published while a mark is unresolved: the rows would send its path as written.</summary>
    public bool CanPublish => !string.IsNullOrEmpty(Folder) && UnresolvedMarks.Count == 0;

    /// <summary>Nothing is exported while a mark is unresolved, for the same reason.</summary>
    public bool CanExport => UnresolvedMarks.Count == 0;

    /// <summary>
    /// The connectors whose path mark was lost and is still unanswered, sorted. One leaves the list
    /// when a path row of it is ticked that was not ticked on open, with a name the placeholder
    /// can carry — the new tick replaces the lost mark — or when the mark is forgotten. Unticking
    /// that row puts it back.
    /// </summary>
    public IReadOnlyList<string> UnresolvedMarks => lostMarks
        .Where(connector => !PathRows.Any(row => row.Connector == connector && row.Marked
                                                 && !tickedAtOpen.Contains(row.Id)
                                                 && PlaceholderName(row.Name).Length > 0))
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// Drops a lost mark by the author's explicit choice: the path then travels as the preview
    /// shows it, as written unless a row of it is ticked.
    /// </summary>
    public void ForgetUnresolvedMark(string connector)
    {
        if (lostMarks.Remove(connector))
        {
            RaiseMarkGates();
        }
    }

    private void RaiseMarkGates()
    {
        Raise(nameof(UnresolvedMarks));
        Raise(nameof(CanPublish));
        Raise(nameof(CanExport));
    }

    /// <summary>
    /// What the rows say, in the form the exporter reads. A marked row whose name is not a legal
    /// placeholder name is sanitized rather than dropped: the author ticked that row to keep a
    /// path on this machine out of the document, and silently publishing it because of how they
    /// spelled the name would be the one failure here nobody would notice.
    /// <para>
    /// A lost mark is not in it: the preview shows what forgetting one would send, and nothing that
    /// records or writes this intent runs while one is unresolved.
    /// </para>
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
                // The value is what lets the mark find its argument again once arguments move.
                marks[row.Pointer] = new PublishIntent.PathMark(name, hint.Length == 0 ? null : hint, row.Value);
            }
            return new PublishIntent(
                shareValues.Select(p => new KeyValuePair<string, IReadOnlySet<string>>(p.Key, p.Value)),
                pathMarks.Select(p => new KeyValuePair<string, IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark>>(p.Key, p.Value)),
                hints.Select(p => new KeyValuePair<string, IReadOnlyDictionary<string, string>>(p.Key, p.Value)));
        }
    }

    /// <summary>
    /// The document itself, as the editor would show it. Every byte that leaves this machine is
    /// in here. When the ticks can no longer be placed — the collection changed under the open
    /// dialog — there is no document, and the preview says why rather than showing one that would
    /// not be written.
    /// </summary>
    public string Preview
    {
        get
        {
            try
            {
                return state.ExportDocument(Collection, Intent, Connectors).Encode().EditorText();
            }
            catch (PathMarkMovedException moved)
            {
                return AppState.Friendly(moved);
            }
        }
    }

    /// <summary>
    /// What the exporter cannot know is a secret: a value that looks like a credential and is
    /// about to travel. Never an edit — the author decides.
    /// </summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var intent = Intent;
            var held = Held(state, Collection, Connectors);
            var warnings = new List<string>();
            foreach (var name in held.Keys.Order(StringComparer.Ordinal))
            {
                IReadOnlySet<string> shared = intent.ShareValues.TryGetValue(name, out var s)
                    ? s
                    : new HashSet<string>(StringComparer.Ordinal);
                warnings.AddRange(CollectionDocument.CredentialWarnings(held[name].Config, shared)
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
        // Behind the disabled button: what the rows say would send a lost mark's path as written.
        if (UnresolvedMarks.Count > 0)
        {
            return UnresolvedMarkNote(UnresolvedMarks[0]);
        }
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

    /// <summary>
    /// The same document, written once, binding nothing. null on success. Refused with the note
    /// while a mark is unresolved, as <see cref="Publish"/> is.
    /// </summary>
    public string? Export(string path) => UnresolvedMarks.Count > 0
        ? UnresolvedMarkNote(UnresolvedMarks[0])
        : state.WriteExport(Collection, Intent, path, Connectors);

    // MARK: rows

    /// <summary>The environment variables the exporter will read, from the same place it reads them: a remote connector's are its passthrough env, a local one's are the config's own.</summary>
    private static IReadOnlyDictionary<string, string> Env(JsonValue config) =>
        RemotePattern.Decode(config) is { } remote ? remote.PassthroughEnv : FormMapper.Analyze(config).Model.Env;

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
    /// <c>[A-Za-z0-9_]</c> becomes "_" (a leading digit is legal in a marker name). Empty for a
    /// name that is only whitespace — there is nothing there to make a name out of.
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
        return name.ToString();
    }
}
