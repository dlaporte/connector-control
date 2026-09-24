namespace ConnectorControl.Core.State;

/// <summary>
/// The Publish/Export dialog: every environment value this collection carries and every argument
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
    public const string ExportButton = "Export";
    public const string CancelButton = "Cancel";
    /// <summary>This dialog's own folder picker, not the failed-publish banner's button of the same words: a dialog's buttons are its model's, as Settings' and the Import dialog's already are.</summary>
    public const string ChooseFolderButton = "Choose Folder";
    /// <summary>What a screen reader says for the bare tick beside a path row, which has no visible label.</summary>
    public const string MarkPathLabel = "Mark as a path this machine supplies";
    /// <summary>The button beside an unresolved mark's note: drop the mark and let the path travel as the preview shows it.</summary>
    public const string ForgetMarkButton = "Forget Mark";
    /// <summary>The button beside a kept path's note: let that path travel as written in this collection's document, which the preview above shows.</summary>
    public const string ReleaseValueButton = "Release";
    /// <summary>The button beside a publish folder's note: write <c>${COLLECTION_DIR}</c> in that place of the connector, as the author's own edit.</summary>
    public const string UseDirectoryTokenButton = "Use ${COLLECTION_DIR}";

    public static string Title(string collection) => $"Publish “{collection}”";

    /// <summary>The dialog's own title in export mode.</summary>
    public static string ExportTitle(string collection) => $"Export “{collection}”";

    /// <summary>"this PC" is the platform-forced half of this sentence; the Mac mirror says "this Mac".</summary>
    public static string FolderLine(string fileName) => $"writes {fileName} from this PC on every change";

    public static string WarningLine(string connector, string warning) => $"{connector}: {warning}";

    public static string FooterLine(string fileName, string originShort) => $"{fileName} · {originShort}";

    public static string UnresolvedMarkNote(string connector, string name) =>
        $"A path marked “{name}” in “{connector}” has moved. Tick it where it now sits, or forget the mark.";

    public static string KeptPathNote(string connector, string field) =>
        $"“{connector}” carries a path this machine keeps back, in {field}. Tick it where it sits, or release it.";

    public static string PublishFolderNote(string connector, string field) =>
        $"“{connector}” carries this machine's publish folder as written, in {field}. Use ${{COLLECTION_DIR}} in its place.";

    /// <summary>
    /// The folder sits where this dialog cannot write: the author's editor is the way out.
    /// "dialog" is the platform-forced half of this sentence; the Mac mirror says "sheet".
    /// </summary>
    public static string PublishFolderEditNote(string connector, string field) =>
        $"“{connector}” carries this machine's publish folder as written, in {field}, which this dialog cannot write over. Open “{connector}” and write ${{COLLECTION_DIR}} there.";

    /// <summary>Publish with no folder chosen. The button is disabled then, so only a caller that skips it hears this.</summary>
    public const string NoFolderError = "Choose a folder to publish to.";

    /// <summary>Another collection's folder, or a synced collection's: whose it is, since releasing it sends one of this machine's own folders.</summary>
    public static string OtherFolderNote(string connector, string field, string collection) =>
        $"“{connector}” carries, in {field}, the folder this machine keeps “{collection}” in. Tick it where it sits, or release it.";

    /// <summary>
    /// A path mark that lost its argument: its text is held by no argument now. It waits for the
    /// author to tick the path where it now sits, which answers it, or to forget it.
    /// </summary>
    public sealed record UnresolvedMark(string Id, string Connector, string Name, string? Hint)
    {
        internal JsonPointer Pointer { get; init; } = new([]);
    }

    /// <summary>
    /// Which answer a <see cref="KeptPath"/> takes. The Mac nests this as <c>KeptPath.Kind</c>; C#
    /// forbids a nested type named like the record's own <c>Kind</c> property.
    /// </summary>
    public enum KeptPathKind
    {
        /// <summary>A path this machine keeps back: ticked where it sits in an argument row, or released with <see cref="ReleaseKeptPath"/>.</summary>
        Path,

        /// <summary>A folder of this collection's own, which <c>${COLLECTION_DIR}</c> stands for, in a place the token can be written: answered with <see cref="UseDirectoryToken"/>.</summary>
        Folder,
    }

    /// <summary>A path this machine keeps back that the document would carry as written, and where.</summary>
    /// <param name="Field">
    /// Its place in the connector's document form, as the preview shows it: <c>local.command</c>,
    /// <c>local.args[1]</c>, <c>env.NAME.value</c>, <c>env.NAME.hint</c>, <c>needs.NAME.hint</c>,
    /// <c>additional.cwd</c>, <c>remote.extraArgs[0]</c>.
    /// </param>
    /// <param name="Kind">Which answer the entry takes.</param>
    public sealed record KeptPath(string Value, string Connector, string Field, KeptPathKind Kind = KeptPathKind.Path)
    {
        public string Id => Connector + "\0" + Field + "\0" + Value;
    }

    /// <summary>
    /// One environment variable of one connector. Stripped by default: its name and hint travel,
    /// its value does not. A class, not a record: the dialog edits <see cref="Share"/> and
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
        /// made with the value in view. Shortening it is the dialog's business, not the model's.
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
    /// Every path mark the dialog found lost on open, each on its own: its text is held by no
    /// argument of its connector, or the connector is gone. Kept rather than dropped, because the
    /// rows alone would show such a path unticked and publishing them would send it as written. In
    /// connector, then pointer, order.
    /// </summary>
    private readonly List<UnresolvedMark> lostMarks = [];
    /// <summary>The lost marks the author chose to forget.</summary>
    private readonly HashSet<string> forgotten = new(StringComparer.Ordinal);
    /// <summary>Row → the lost mark its tick answers.</summary>
    private readonly Dictionary<string, string> answers = new(StringComparer.Ordinal);
    /// <summary>The paths the author released in this dialog, to travel as written in this document.</summary>
    private readonly HashSet<string> released = new(StringComparer.Ordinal);
    /// <summary>The path rows ticked when the dialog opened. Those are marks already on record, so only a tick made since can stand in for a lost one.</summary>
    private readonly HashSet<string> tickedAtOpen = new(StringComparer.Ordinal);
    /// <summary>Each row's name as the dialog opened it, so a tick that answers a lost mark gives it that mark's name only while the author has not typed one of their own.</summary>
    private readonly Dictionary<string, string> namesAtOpen = new(StringComparer.Ordinal);

    /// <summary>
    /// Publishing binds the collection to a folder it rewrites on every change; exporting writes
    /// the same document once, wherever the save dialog says, and binds nothing. The dialog is the
    /// same dialog because the decision the author is making — what travels — is the same one, so
    /// the mode is the model's, and with it which title, which gate and which verb.
    /// </summary>
    public enum Mode
    {
        Publish,
        Export,
    }

    public PublishModel(AppState state, string collection, IReadOnlyList<string>? connectors = null,
                        Mode mode = Mode.Publish)
    {
        this.state = state;
        Collection = collection;
        Connectors = connectors;
        SheetMode = mode;
        // A collection that already publishes reopens showing what it publishes: the folder it
        // writes to and every tick the record remembers.
        var intent = state.CollectionsFile.Collections.GetValueOrDefault(collection)?.Publish?.Intent ?? PublishIntent.None;
        folder = state.CollectionsCache.Published.GetValueOrDefault(collection)?.Folder;
        var env = new List<EnvRow>();
        var paths = new List<PathRow>();
        var lost = new List<UnresolvedMark>();
        // What this machine keeps back from the collection's document: its lists of marked paths
        // and the folders it binds. A row holding one of them starts ticked even when the record no
        // longer marks it — the other machine may have dropped the mark while this one still sends
        // the path — so the author unticks it on purpose, in view of the preview, or it stays a
        // placeholder.
        var denied = state.KeptBack(collection).Values.Select(KeptValue.Nfc).ToHashSet(StringComparer.Ordinal);
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
            // were made: an argument that moved since keeps its tick. Every other argument holding a
            // marked path's text is ticked with that mark's name and hint too, since a copy of a
            // marked path is that path. A marked argument keeps its row even once it stops looking
            // like a path (the file it named is gone), or publishing from the dialog would quietly
            // unmark it.
            IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark> marks =
                intent.PathMarks.TryGetValue(name, out var recorded) ? recorded : new Dictionary<JsonPointer, PublishIntent.PathMark>();
            var placement = PublishIntent.PlacePathMarks(marks, arguments);
            var byValue = MarksByValue(marks);
            // A mark whose text no argument holds any more has nothing to tick: it waits, on its
            // own, for the author to tick the path where it now sits.
            var texts = arguments.Select(KeptValue.Nfc).ToHashSet(StringComparer.Ordinal);
            lost.AddRange(SortedByPointer(placement.Unresolved)
                .Where(p => p.Value.Value is { } value && !texts.Contains(KeptValue.Nfc(value)))
                .Select(p => Lost(name, p.Key, p.Value)));
            for (var index = 0; index < arguments.Count; index++)
            {
                var text = KeptValue.Nfc(arguments[index]);
                var mark = placement.Placed.GetValueOrDefault(index) ?? byValue.GetValueOrDefault(text);
                var ticked = mark is not null || denied.Contains(text);
                if (!LooksLikeAPath(arguments[index]) && !ticked)
                {
                    continue;
                }
                found++;
                var pointer = new JsonPointer(["args", index.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                paths.Add(new PathRow(name, pointer, arguments[index], ticked,
                                      mark?.Name ?? DefaultPathName(found), mark?.Hint ?? string.Empty));
            }
        }
        // A mark for a connector the collection no longer holds was made on one renamed or removed
        // where the record could not follow, and the exporter refuses it whatever the subset. No
        // row can be ticked for it, so each waits to be forgotten.
        var all = state.Store.Collections.GetValueOrDefault(collection)?.Mcps;
        foreach (var (name, marks) in intent.PathMarks.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (all is null || !all.ContainsKey(name))
            {
                lost.AddRange(SortedByPointer(marks).Where(p => p.Value.Value is not null).Select(p => Lost(name, p.Key, p.Value)));
            }
        }
        lostMarks.AddRange(lost
            .OrderBy(mark => mark.Connector, StringComparer.Ordinal)
            .ThenBy(mark => mark.Pointer.ToString(), StringComparer.Ordinal));
        tickedAtOpen.UnionWith(paths.Where(row => row.Marked).Select(row => row.Id));
        foreach (var row in paths)
        {
            namesAtOpen[row.Id] = row.Name;
        }
        ReplaceRows(env, paths);
    }

    private static UnresolvedMark Lost(string connector, JsonPointer pointer, PublishIntent.PathMark mark) =>
        new(connector + pointer, connector, mark.Name, mark.Hint) { Pointer = pointer };

    private static IEnumerable<KeyValuePair<JsonPointer, PublishIntent.PathMark>> SortedByPointer(
        IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark> marks) =>
        marks.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal);

    public string Collection { get; }

    /// <summary>
    /// Which of the two dialogs this is. Fixed for the dialog's life: the menu entry that opened it
    /// chose. The Mac calls this <c>mode</c>; here the nested enum already owns that name.
    /// </summary>
    public Mode SheetMode { get; }

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

    /// <summary>Where the document is written, null until the user chooses. Settable: the dialog's Choose Folder is the only thing that fills it.</summary>
    public string? Folder
    {
        get => folder;
        set
        {
            if (Set(ref folder, value))
            {
                Raise(nameof(CanPublish));
                Raise(nameof(CanFinish));
            }
        }
    }

    public IReadOnlyList<EnvRow> EnvRows { get; private set; } = [];

    public IReadOnlyList<PathRow> PathRows { get; private set; } = [];

    /// <summary>
    /// Whether each section has anything to show. A collection of remote connectors with no
    /// passthrough environment has neither, and an empty heading over nothing is worse than no
    /// heading; the dialog binds these rather than counting rows itself.
    /// </summary>
    public bool HasEnvRows => EnvRows.Count > 0;

    public bool HasPathRows => PathRows.Count > 0;

    /// <summary>
    /// Takes a new set of rows, listening to each one and letting the previous set go. Every tick
    /// and every keystroke in a row changes what the document says, so the model re-raises what
    /// the rows feed rather than leaving the dialog to refresh itself. The rows live and die with
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

    /// <summary>
    /// Everything below the rows is derived from them, and nothing above is. A tick made since the
    /// dialog opened answers the next lost mark of its connector and takes its name and hint.
    /// </summary>
    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is PathRow row && e.PropertyName == nameof(PathRow.Marked))
        {
            AnswerLostMark(row);
        }
        Raise(nameof(Intent));
        Raise(nameof(Preview));
        Raise(nameof(Warnings));
        // A tick, or the name beside it, is what answers a lost mark; a shared value can carry a
        // kept path into the document.
        RaiseMarkGates();
    }

    /// <summary>
    /// A row newly ticked, not ticked on open, answers the next unanswered lost mark of its
    /// connector in pointer order, and takes its name and hint while the author has not typed their
    /// own. A row unticked gives its lost mark back. The Mac answers every row at once, as
    /// <c>answerLostMarks(since:)</c>, because its rows are values and only the array announces a
    /// tick; here each row announces its own.
    /// </summary>
    private void AnswerLostMark(PathRow row)
    {
        if (!row.Marked)
        {
            answers.Remove(row.Id);
            return;
        }
        if (tickedAtOpen.Contains(row.Id) || answers.ContainsKey(row.Id))
        {
            return;
        }
        var taken = answers.Values.ToHashSet(StringComparer.Ordinal);
        if (lostMarks.FirstOrDefault(mark => mark.Connector == row.Connector && !forgotten.Contains(mark.Id)
                                             && !taken.Contains(mark.Id)) is not { } answered)
        {
            return;
        }
        answers[row.Id] = answered.Id;
        if (row.Name == namesAtOpen.GetValueOrDefault(row.Id) && row.Hint.Length == 0)
        {
            row.Name = answered.Name;
            row.Hint = answered.Hint ?? string.Empty;
        }
    }

    /// <summary>
    /// Publishing names the collection it binds; exporting borrows the menu item's own wording,
    /// which names the collection it writes once.
    /// </summary>
    public string SheetTitle => SheetMode == Mode.Publish ? Title(Collection) : ExportTitle(Collection);

    /// <summary>The document's name in the folder: the slug publishing fixed, or what this collection's name would make of it.</summary>
    public string FileName =>
        CollectionDocument.FileName(state.CollectionsFile.Collections.GetValueOrDefault(Collection)?.Publish?.Slug ?? Slug.Make(Collection));

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

    /// <summary>Nothing is published while a mark is unresolved or a kept path unanswered: the rows would send the path as written.</summary>
    public bool CanPublish => ChosenFolder is not null && UnresolvedMarks.Count == 0 && KeptPaths.Count == 0;

    /// <summary>
    /// The folder exactly as the picker gave it, or null when there is none. Not trimmed: a folder
    /// whose name ends in a space is a different folder. One that is only spaces is no folder.
    /// </summary>
    private string? ChosenFolder => Folder is { } folder && folder.TrimSpaces().Length > 0 ? folder : null;

    /// <summary>Nothing is exported while either waits, for the same reason.</summary>
    public bool CanExport => UnresolvedMarks.Count == 0 && KeptPaths.Count == 0;

    /// <summary>
    /// The gate on the dialog's one verb: a publish waits for a folder as well, an export only for
    /// what the two lists above hold.
    /// </summary>
    public bool CanFinish => SheetMode == Mode.Publish ? CanPublish : CanExport;

    /// <summary>
    /// The lost marks still unanswered, one note each, in connector then pointer order. A lost mark
    /// is answered by its own tick — a path row of its connector ticked since the dialog opened,
    /// with a name the placeholder can carry, each tick answering the next lost mark in pointer
    /// order — or by forgetting it. Unticking the row puts it back.
    /// </summary>
    public IReadOnlyList<UnresolvedMark> UnresolvedMarks
    {
        get
        {
            var answered = PathRows
                .Where(row => row.Marked && PlaceholderName(row.Name).Length > 0 && answers.ContainsKey(row.Id))
                .Select(row => answers[row.Id])
                .ToHashSet(StringComparer.Ordinal);
            return lostMarks.Where(mark => !forgotten.Contains(mark.Id) && !answered.Contains(mark.Id)).ToList();
        }
    }

    /// <summary>
    /// Drops one lost mark by the author's explicit choice: its path then travels as the preview
    /// shows it, as written unless a row of it is ticked.
    /// </summary>
    public void ForgetUnresolvedMark(string id)
    {
        if (!forgotten.Add(id))
        {
            return;
        }
        foreach (var row in answers.Where(p => p.Value == id).Select(p => p.Key).ToList())
        {
            answers.Remove(row);
        }
        RaiseMarkGates();
    }

    /// <summary>
    /// Every path this machine keeps back that the document, as the rows now make it, would carry
    /// as written, with its connector and field: a copy of a ticked path; a path on one of this
    /// machine's lists of marked paths, this collection's or another's; a folder it binds. Each is
    /// answered by ticking it where it sits in an argument row, or by releasing it.
    /// </summary>
    public IReadOnlyList<KeptPath> KeptPaths
    {
        get
        {
            var intent = Intent;
            var held = Held(state, Collection, Connectors).ToDictionary(p => p.Key, p => p.Value.Config, StringComparer.Ordinal);
            var found = CollectionDocument.CopiesOfMarkedPaths(held, intent).Select(f => new KeptPath(f.Value, f.Connector, f.Field)).ToList();
            if (found.Count == 0)
            {
                try
                {
                    var document = state.ExportDocument(Collection, intent, Connectors);
                    var (values, folders) = state.KeptBack(Collection, ReviewedValues, released);
                    found.AddRange(document.Findings(values).Select(f => new KeptPath(f.Value, f.Connector, f.Field)));
                    // A folder of this collection's own is a folder entry wherever it sits, even where
                    // the rewrite cannot reach it: its note then says where to write the token.
                    found.AddRange(document.Findings(folders).Select(f => new KeptPath(
                        f.Value, f.Connector, f.Field, KeptPathKind.Folder)));
                }
                catch (PublishIntentException)
                {
                    found.Clear();
                }
            }
            return found.DistinctBy(kept => kept.Id).ToList();
        }
    }

    /// <summary>
    /// Lets one kept path travel as written in this collection's document, by the author's explicit
    /// choice after reading the preview. Everywhere: every row holding it is unticked, since a path
    /// both marked and released would be both a placeholder and not.
    /// <para>
    /// A folder of this collection's own is never released, whatever the view offers: it is
    /// answered by <see cref="UseDirectoryToken"/>, or by writing <c>${COLLECTION_DIR}</c> in the
    /// connector's editor. null when the path is released; otherwise a folder entry's note for where
    /// it sits, and nothing changes. A folder's note even where the list shows it as a path kept
    /// back — a copy of a ticked one — since that note's own answer is the Release just refused.
    /// </para>
    /// </summary>
    public string? ReleaseKeptPath(string value)
    {
        var text = KeptValue.Nfc(value);
        if (state.KeptBack(Collection).Folders.Any(folder => KeptValue.Nfc(folder) == text))
        {
            return KeptPaths.FirstOrDefault(kept => KeptValue.Nfc(kept.Value) == text) is { } entry
                ? Note(entry with { Kind = KeptPathKind.Folder })
                : null;
        }
        released.Add(value);
        foreach (var row in PathRows.Where(row => row.Marked && KeptValue.Nfc(row.Value) == text))
        {
            row.Marked = false;
        }
        RaiseMarkGates();
        return null;
    }

    /// <summary>
    /// What the dialog says about one entry: how it is answered, and for a path kept back that is
    /// another collection's folder, whose folder it is. The view shows this rather than composing it,
    /// so a new kind of entry cannot reach the dialog with the wrong sentence.
    /// </summary>
    public string Note(KeptPath kept)
    {
        var field = state.FieldNameOf(new KeptValueFinding(kept.Connector, kept.Field, kept.Value), Collection);
        if (kept.Kind == KeptPathKind.Folder)
        {
            return CanWriteDirectoryToken(kept.Connector, kept.Field, kept.Value)
                ? PublishFolderNote(kept.Connector, field)
                : PublishFolderEditNote(kept.Connector, field);
        }
        return state.CollectionBound(kept.Value) is { } owner
            ? OtherFolderNote(kept.Connector, field, owner)
            : KeptPathNote(kept.Connector, field);
    }

    /// <summary>
    /// Writes <c>${COLLECTION_DIR}</c> where <paramref name="kept"/>, a folder of this collection's
    /// own, sits: in the dialog's own hint for a hint, and otherwise in the connector itself, saved as
    /// an editor save is and applied to Claude's config when the collection is the active one. The
    /// author's own edit, in view of the preview. null when the token is written; otherwise the
    /// entry's note, which says what does answer it — Release for a path kept back, the connector's
    /// editor for a folder this dialog cannot reach.
    /// </summary>
    public string? UseDirectoryToken(KeptPath kept)
    {
        if (kept.Kind != KeptPathKind.Folder)
        {
            return Note(kept);
        }
        var token = Placeholder.DirectoryToken;
        if (HintName(kept.Field, "env") is { } variable)
        {
            foreach (var row in EnvRows.Where(r => r.Connector == kept.Connector && r.Name == variable))
            {
                row.Hint = KeptValue.Replacing(kept.Value, row.Hint, token);
            }
            return null;
        }
        if (HintName(kept.Field, "needs") is { } need)
        {
            foreach (var row in PathRows.Where(r => r.Connector == kept.Connector && PlaceholderName(r.Name) == need))
            {
                row.Hint = KeptValue.Replacing(kept.Value, row.Hint, token);
            }
            return null;
        }
        if (state.Store.Collections.GetValueOrDefault(Collection)?.Mcps.GetValueOrDefault(kept.Connector) is not { } entry
            || CollectionDocument.UsingDirectoryToken(entry.Config, kept.Field, kept.Value) is not { } config)
        {
            return Note(kept);
        }
        if (state.Upsert(kept.Connector, entry with { Config = config }, kept.Connector, Collection) is { } error)
        {
            return error;
        }
        if (Collection == state.ActiveCollection)
        {
            state.ApplyInteractively();
        }
        RefreshRows(kept.Connector);
        RaiseMarkGates();
        Raise(nameof(Preview));
        Raise(nameof(Intent));
        return null;
    }

    private bool CanWriteDirectoryToken(string connector, string field, string folder)
    {
        if (HintName(field, "env") is { } variable)
        {
            return EnvRows.Any(r => r.Connector == connector && r.Name == variable);
        }
        if (HintName(field, "needs") is { } need)
        {
            return PathRows.Any(r => r.Connector == connector && PlaceholderName(r.Name) == need);
        }
        return state.Store.Collections.GetValueOrDefault(Collection)?.Mcps.GetValueOrDefault(connector) is { } entry
            && CollectionDocument.UsingDirectoryToken(entry.Config, field, folder) is not null;
    }

    /// <summary>The name in <c>env.NAME.hint</c> or <c>needs.NAME.hint</c>, the hints the dialog itself holds.</summary>
    private static string? HintName(string field, string section)
    {
        var prefix = section + ".";
        const string suffix = ".hint";
        return field.StartsWith(prefix, StringComparison.Ordinal) && field.EndsWith(suffix, StringComparison.Ordinal)
               && field.Length > prefix.Length + suffix.Length
            ? field[prefix.Length..^suffix.Length]
            : null;
    }

    /// <summary>
    /// The rows of <paramref name="connector"/> after its config changed under the dialog: an
    /// environment row shows the value it now holds, and an argument row that no longer reads as it
    /// did goes, since what it showed is not in the connector any more.
    /// </summary>
    private void RefreshRows(string connector)
    {
        if (state.Store.Collections.GetValueOrDefault(Collection)?.Mcps.GetValueOrDefault(connector) is not { } entry)
        {
            return;
        }
        var variables = Env(entry.Config);
        var env = EnvRows.Select(row => row.Connector == connector && variables.TryGetValue(row.Name, out var value) && value != row.Value
            ? new EnvRow(row.Connector, row.Name, value, row.Share, row.Hint)
            : row).ToList();
        var arguments = Arguments(entry.Config);
        var now = arguments.Select((argument, index) => (Pointer: new JsonPointer(
                ["args", index.ToString(System.Globalization.CultureInfo.InvariantCulture)]), Argument: argument))
            .ToDictionary(p => p.Pointer, p => p.Argument);
        var paths = PathRows.Where(row => row.Connector != connector
            || (now.TryGetValue(row.Pointer, out var argument) && argument == row.Value)).ToList();
        ReplaceRows(env, paths);
        // A row that went takes its answer with it, or the lost mark it answered could be answered
        // by no other tick.
        var kept = paths.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in answers.Keys.Where(id => !kept.Contains(id)).ToList())
        {
            answers.Remove(gone);
        }
    }

    private void RaiseMarkGates()
    {
        Raise(nameof(UnresolvedMarks));
        Raise(nameof(KeptPaths));
        Raise(nameof(CanPublish));
        Raise(nameof(CanExport));
        Raise(nameof(CanFinish));
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
    /// The document itself, as the editor would show it. Every byte that leaves this machine is in
    /// here, kept paths included, so the author reads them before releasing any
    /// (<see cref="KeptPaths"/>). When the ticks can no longer be placed — the collection changed
    /// under the open dialog, or a ticked path is also copied unticked — there is no document, and
    /// the preview says why rather than showing one that would not be written.
    /// </summary>
    public string Preview
    {
        get
        {
            try
            {
                return state.ExportDocument(Collection, Intent, Connectors).Encode().EditorText();
            }
            catch (PublishIntentException refused)
            {
                return AppState.Friendly(refused);
            }
        }
    }

    /// <summary>
    /// The text of every path the rows mark: what Publish or Export must not send as written
    /// anywhere else in the document, and what the dialog's Publish records as this machine's list
    /// of marked paths — the author's reviewed answer, replacing whatever was kept before.
    /// </summary>
    private IReadOnlySet<string> ReviewedValues =>
        Intent.PathMarks.Values.SelectMany(marks => marks.Values)
            .Select(mark => mark.Value).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

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

    /// <summary>The first thing still waiting for the author, as the note the dialog shows for it.</summary>
    private string? FirstUnanswered
    {
        get
        {
            if (UnresolvedMarks.FirstOrDefault() is { } lost)
            {
                return UnresolvedMarkNote(lost.Connector, lost.Name);
            }
            return KeptPaths.FirstOrDefault() is { } kept ? Note(kept) : null;
        }
    }

    /// <summary>What the author released and has not ticked since: a ticked path is kept back again.</summary>
    private IReadOnlySet<string> LetGo
    {
        get
        {
            var letGo = new HashSet<string>(released, StringComparer.Ordinal);
            letGo.ExceptWith(ReviewedValues);
            return letGo;
        }
    }

    /// <summary>
    /// Publish, or re-publish with what the dialog now says. A folder that is not the one on record
    /// starts publishing again there, which is how the failed-write banner's Choose Folder moves
    /// a collection. null on success. Refused with the first note while a mark is unresolved or a
    /// kept path unanswered, behind the disabled button: what the rows say would send the path as
    /// written. What the author released goes on record with the ticks.
    /// </summary>
    public string? Publish()
    {
        if (FirstUnanswered is { } note)
        {
            return note;
        }
        if (ChosenFolder is not { } chosen)
        {
            return NoFolderError;
        }
        if (!state.IsPublished(Collection)
            || !string.Equals(state.CollectionsCache.Published.GetValueOrDefault(Collection)?.Folder, chosen, StringComparison.Ordinal))
        {
            var failure = state.StartPublishing(Collection, chosen, Intent, ReviewedValues, LetGo);
            // Starting to publish mints the collection's origin, even when the write it then
            // attempts fails, and the footer is the one line that shows it.
            Raise(nameof(OriginShort));
            Raise(nameof(FooterSentence));
            return failure;
        }
        // The same folder, already publishing: the ticks go on record, and then the document is
        // written whether or not it changed. Pressing Publish again is how a write that failed is
        // retried, and by then nothing about the document is different — only the folder is.
        state.UpdatePublishIntent(Collection, Intent, ReviewedValues, LetGo);
        return state.Republish(Collection);
    }

    /// <summary>
    /// The same document, written once, binding nothing. null on success. Refused with the first
    /// note while anything waits, as <see cref="Publish"/> is.
    /// </summary>
    public string? Export(string path) => FirstUnanswered ?? state.WriteExport(Collection, Intent, path, Connectors, ReviewedValues, LetGo);

    /// <summary>
    /// The dialog's one verb: publish into the chosen folder, or export to <paramref name="path"/>,
    /// which the save dialog answers and only an export reads. null on success.
    /// </summary>
    public string? Finish(string? path = null) => SheetMode == Mode.Publish
        ? Publish()
        : Export(path ?? throw new ArgumentNullException(nameof(path), "An export is written where the save dialog says."));

    // MARK: rows

    /// <summary>The environment variables the exporter will read, from the same place it reads them: a remote connector's are its passthrough env, a local one's are the config's own.</summary>
    private static IReadOnlyDictionary<string, string> Env(JsonValue config) =>
        RemotePattern.Decode(config) is { } remote ? remote.PassthroughEnv : FormMapper.Analyze(config).Model.Env;

    /// <summary>Only a local connector's arguments are the author's own. A remote connector's are built by the launcher on each machine, so there is nothing there to mark.</summary>
    private static IReadOnlyList<string> Arguments(JsonValue config) =>
        RemotePattern.Decode(config) is not null ? [] : FormMapper.Analyze(config).Model.Args;

    /// <summary>
    /// An argument worth offering as machine-specific: it is written as a path, by the same rule
    /// the Collections window's target column uses, or something by that name is on this disk. A
    /// flag or a URL is neither.
    /// </summary>
    private static bool LooksLikeAPath(string argument)
    {
        if (Placeholder.ContainsMarker(argument))
        {
            return false;
        }
        if (CollectionsModel.IsExplicitPath(argument))
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

    /// <summary>A connector's recorded marks by the text each was made on, in NFC; where two share a text, the first in pointer order speaks for both.</summary>
    private static Dictionary<string, PublishIntent.PathMark> MarksByValue(IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark> marks)
    {
        var byValue = new Dictionary<string, PublishIntent.PathMark>(StringComparer.Ordinal);
        foreach (var (_, mark) in SortedByPointer(marks))
        {
            if (mark.Value is { } value)
            {
                byValue.TryAdd(KeptValue.Nfc(value), mark);
            }
        }
        return byValue;
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
