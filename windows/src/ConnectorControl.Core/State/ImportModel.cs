using System.Globalization;

namespace ConnectorControl.Core.State;

/// <summary>
/// The Import sheet: one collection document, and the two exclusive things that can be done with
/// it — copies into a collection the user owns, or a collection of its own that stays in sync with
/// the file. Every row says what would happen to that connector before anything happens, because
/// an import lands commands Claude will run.
///
/// Mirror: Sources/ConnectorControlState/ImportModel.swift
/// </summary>
public sealed class ImportModel : ObservableObject
{
    public const string Title = "Import connectors";
    public const string AddModeDetail = "Copies. No link to the file afterwards.";
    public const string SyncModeTitle = "Keep as its own collection, in sync with this file";
    public const string SyncModeDetail = "Read-only except your secrets and switches. Changes at the source arrive for review.";
    public const string NewBadge = "new · arrives off";
    public const string PresentBadge = "already present · skipped";
    public const string ReplaceKeepsValues = "keeps your filled values";
    public const string AddTitle = "Add";
    public const string ReplaceTitle = "Replace";
    public const string KeepBothTitle = "Keep both";
    public const string SkipTitle = "Skip";

    /// <summary>Stands in for the document's author when it travelled without one.</summary>
    public const string UnknownAuthor = "unknown author";
    public const string CancelButton = "Cancel";
    /// <summary>
    /// A screen reader's name for the name field in sync mode, which carries no label of its
    /// own. The mockup draws "Name:" beside it; this is the longer form a reader needs without
    /// the sentence above the field for context.
    /// </summary>
    public const string SyncNameLabel = "Collection name";

    public static string AddModeTitle(string collection) => $"Add to a collection: {collection}";

    public static string SourceLine(string document, string author, int connectors) =>
        $"“{document}” by {author} · {connectors} connectors";

    public static string SkippedBadge(string reason) => $"skipped: {reason}";

    /// <summary>A screen reader's name for the bare tick beside a connector.</summary>
    public static string IncludeLabel(string connector) => $"Include {connector}";

    /// <summary>
    /// A screen reader's name for a collision's choice picker. Without one it reads out the
    /// badge beside it, which says the row is skipped — the opposite of what the picker is for.
    /// </summary>
    public static string CollisionPickerLabel(string connector) => $"What to do with {connector}";

    public static string ImportButton(int count) =>
        "Import " + count.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// What a collision offers, in the order the row's picker lists them. <see cref="ImportChoice.Add"/>
    /// is not among them: a row with nothing in its way shows <see cref="NewBadge"/> instead of a
    /// picker, so the view binds this list rather than deciding for itself which cases a
    /// collision has.
    /// </summary>
    public static readonly IReadOnlyList<ImportChoice> CollisionChoices =
        [ImportChoice.Replace, ImportChoice.KeepBoth, ImportChoice.Skip];

    /// <summary>
    /// A row's badge, in precedence order: a connector this platform cannot run says so first,
    /// then one the target already holds, then a new arrival.
    /// </summary>
    private static string Badge(string? excludedReason, bool present) =>
        excludedReason is not null ? SkippedBadge(excludedReason) : present ? PresentBadge : NewBadge;

    /// <summary>
    /// One choice's picker label. Total over the enum, so a row's picker needs no logic of its
    /// own; Add is the answer for a name nothing here already holds.
    /// </summary>
    public static string ChoiceTitle(ImportChoice choice) => choice switch
    {
        ImportChoice.Add => AddTitle,
        ImportChoice.Replace => ReplaceTitle,
        ImportChoice.KeepBoth => KeepBothTitle,
        ImportChoice.Skip => SkipTitle,
        _ => throw new ArgumentOutOfRangeException(nameof(choice)),
    };

    public enum Mode
    {
        AddToCollection,
        KeepInSync,
    }

    /// <summary>
    /// One connector of the document against the collection it would land in. <see cref="Present"/>
    /// is what the badge says and what <see cref="Choice"/> answers; an excluded connector cannot
    /// be included at all, since this platform has no way to run it. A class, not a record: the
    /// sheet edits <see cref="Include"/> and <see cref="Choice"/> in place through two-way
    /// bindings, where the Mac mutates a struct through its index. Those two raise
    /// PropertyChanged so the model can re-raise the count and the button that follow them; the
    /// Mac needs none of that, because its rows sit in a @Published array and the array itself
    /// is what announces the edit.
    /// </summary>
    public sealed class Row(string name, bool include, bool present, ImportChoice choice,
                            string? excludedReason, IReadOnlyList<string> needs, string? needsCaution,
                            string badge) : ObservableObject
    {
        private bool include = include;
        private ImportChoice choice = choice;

        public string Id { get; } = name;
        public string Name { get; } = name;
        public bool Include { get => include; set => Set(ref include, value); }
        public bool Present { get; } = present;
        public ImportChoice Choice { get => choice; set => Set(ref choice, value); }
        public string? ExcludedReason { get; } = excludedReason;
        public IReadOnlyList<string> Needs { get; } = needs;

        /// <summary>
        /// The caution glyph's tooltip, or null for no glyph — the same sentence a connector of
        /// the collection this row lands in already carries for the same condition, rather than a
        /// second wording of it. Filled by the model, as <c>CollectionsModel.Row.Caution</c> is.
        /// </summary>
        public string? NeedsCaution { get; } = needsCaution;

        /// <summary>
        /// What the row says about itself beside its name: nothing this platform can run, a name
        /// the target already holds, or a new arrival. Built where the row is, so the template
        /// binds one string instead of needing a converter over the skipped-badge factory.
        /// </summary>
        public string Badge { get; } = badge;
    }

    private readonly AppState state;
    private readonly RenderedCollection? rendered;
    private Mode mode = Mode.AddToCollection;
    private string targetCollection;
    private string syncName;
    private IReadOnlyList<Row> rows = [];

    public ImportModel(AppState state, string path)
    {
        this.state = state;
        Path = path;
        var full = System.IO.Path.GetFullPath(path);
        var (document, _, failure) = AppState.ReadDocument(full);
        // The target picker is filled either way, so a sheet that cannot read its document still
        // shows the collection the user was in.
        var locals = state.LocalCollectionNames;
        targetCollection = locals.Contains(state.ActiveCollection, StringComparer.Ordinal)
            ? state.ActiveCollection
            : locals.Count > 0 ? locals[0] : string.Empty;
        if (document is null)
        {
            LoadError = failure;
            DocumentName = System.IO.Path.GetFileName(full);
            syncName = string.Empty;
            return;
        }
        DocumentName = document.Name;
        Author = document.Author;
        rendered = document.Render();
        // The document's name, suffixed until it is one no collection here already answers to.
        syncName = FreeName(document.Name, state.CollectionNames);
        RebuildRows();
    }

    public string Path { get; }

    /// <summary>The document's own name, or the file's when it could not be read.</summary>
    public string DocumentName { get; } = string.Empty;

    public string? Author { get; }

    /// <summary>
    /// Why this document cannot be imported at all, null when it read. The sheet shows it in
    /// place of the rows and Import stays out of reach.
    /// </summary>
    public string? LoadError { get; }

    /// <summary>The Mac binds the optional above directly; XAML needs a bool for the body's visibility.</summary>
    public bool HasLoadError => LoadError is not null;

    /// <summary>The Mac calls this <c>mode</c>; here the nested enum already owns that name.</summary>
    public Mode ImportMode { get => mode; set => Set(ref mode, value); }

    /// <summary>
    /// Which collection the copies land in. Changing it rebuilds the rows: a different target
    /// collides with different connectors, so the badges and the ticks have to follow it.
    /// </summary>
    public string TargetCollection
    {
        get => targetCollection;
        set
        {
            if (Set(ref targetCollection, value))
            {
                RebuildRows();
            }
        }
    }

    public string SyncName { get => syncName; set => Set(ref syncName, value); }

    /// <summary>
    /// Setting this listens to the new rows and lets the old ones go, so a tick or a choice
    /// reaches <see cref="ImportCount"/> and <see cref="CanImport"/> without the sheet asking.
    /// The rows live and die with this model, so a replacement is the only release needed.
    /// </summary>
    public IReadOnlyList<Row> Rows
    {
        get => rows;
        private set
        {
            foreach (var row in rows)
            {
                row.PropertyChanged -= OnRowChanged;
            }
            Set(ref rows, value);
            foreach (var row in value)
            {
                row.PropertyChanged += OnRowChanged;
            }
        }
    }

    /// <summary>Everything the footer says is counted off the rows, and nothing above them is.</summary>
    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Raise(nameof(ImportCount));
        Raise(nameof(CanImport));
    }

    /// <summary>
    /// "“Data team” by Acme Data Platform · 4 connectors" — what the file says about itself. The
    /// Mac calls this <c>sourceLine</c>; here the static factory already owns that name.
    /// </summary>
    public string SourceSentence => SourceLine(DocumentName, Author ?? UnknownAuthor, Rows.Count);

    /// <summary>The collections copies may land in. A synced collection answers to its own document, so it is never one of them.</summary>
    public IReadOnlyList<string> LocalCollections => state.LocalCollectionNames;

    /// <summary>
    /// What the Import button counts: the rows that are ticked in add mode, and everything this
    /// platform can carry in sync mode, where the whole document comes across or none of it. A
    /// ticked row set to Skip lands nothing, so the button must not promise it:
    /// <see cref="Perform"/> sends Skip for exactly these, and a count that disagreed would say
    /// "Import 3" over two connectors arriving.
    /// </summary>
    public int ImportCount => ImportMode == Mode.AddToCollection
        ? Rows.Count(row => row.Include && row.ExcludedReason is null && row.Choice != ImportChoice.Skip)
        : Rows.Count(row => row.ExcludedReason is null);

    public bool CanImport
    {
        get
        {
            if (LoadError is not null)
            {
                return false;
            }
            // A document every connector of which this platform excludes still subscribes: what
            // the author ships next may be something this machine can run.
            return ImportMode == Mode.AddToCollection
                ? ImportCount > 0 && TargetCollection.Length > 0
                : SyncName.TrimSpaces().Length > 0;
        }
    }

    /// <summary>
    /// Lands what the sheet says: copies into the target, or a collection of its own bound to the
    /// file. null on success, else the message to show.
    /// </summary>
    public string? Perform()
    {
        if (LoadError is not null)
        {
            return LoadError;
        }
        if (ImportMode == Mode.KeepInSync)
        {
            return state.Subscribe(Path, SyncName);
        }
        var choices = new Dictionary<string, ImportChoice>(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            choices[row.Name] = row.Include && row.ExcludedReason is null ? row.Choice : ImportChoice.Skip;
        }
        return state.ImportCopies(Path, TargetCollection, choices, state.Today);
    }

    /// <summary>
    /// One row per connector the document carries, including the ones this platform excludes — a
    /// connector that cannot come across is worth seeing and saying why. Ticked by default unless
    /// something already answers to that name in the target, which is the one case where importing
    /// takes a decision from the user.
    /// </summary>
    private void RebuildRows()
    {
        if (rendered is null)
        {
            Rows = [];
            return;
        }
        var held = state.Store.Collections.TryGetValue(TargetCollection, out var target)
            ? target.Mcps
            : new Dictionary<string, McpEntry>(StringComparer.Ordinal);
        var names = new HashSet<string>(rendered.Connectors.Keys, StringComparer.Ordinal);
        names.UnionWith(rendered.Excluded.Keys);
        Rows = names.Order(StringComparer.Ordinal).Select(name =>
        {
            var reason = rendered.Excluded.GetValueOrDefault(name);
            var present = held.ContainsKey(name);
            // First appearance in the config the import would write, not the Needs map's own
            // order: the map is unordered, and reading the markers is what makes this row's
            // sentence the one the connector will carry once it has landed.
            IReadOnlyList<string> needs = rendered.Connectors.TryGetValue(name, out var connector)
                ? Placeholder.UnfilledNamesIn(connector.Config)
                : [];
            return new Row(name, reason is null && !present, present,
                           present ? ImportChoice.Replace : ImportChoice.Add, reason, needs,
                           needs.Count == 0 ? null : AppState.NeedsValueCaution(string.Join(", ", needs)),
                           Badge(reason, present));
        }).ToList();
    }

    /// <summary><paramref name="name"/>, or "name 2", "name 3", … — the first one nothing in <paramref name="taken"/> answers to.</summary>
    private static string FreeName(string name, IReadOnlyList<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.Ordinal);
        if (!used.Contains(name))
        {
            return name;
        }
        var suffix = 2;
        while (used.Contains(name + " " + suffix.ToString(CultureInfo.InvariantCulture)))
        {
            suffix++;
        }
        return name + " " + suffix.ToString(CultureInfo.InvariantCulture);
    }
}
