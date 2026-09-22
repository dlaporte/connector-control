using System.ComponentModel;

namespace ConnectorControl.Core.State;

/// <summary>
/// The Collections window, minus pixels: the collections as items in the left pane, the selected
/// collection's connectors as rows in the right one, the detail line above them, and a toolbar
/// whose every button follows the selection. Everything is derived from AppState; the model owns
/// only what the window itself knows — which collection is showing and which rows are ticked.
///
/// Mirror: Sources/ConnectorControlState/CollectionsModel.swift
/// </summary>
public sealed class CollectionsModel : ObservableObject, IDisposable
{
    public const string WindowTitle = "Collections";
    public const string ImportButton = "Import…";
    public const string SubscribeButton = "Subscribe…";
    public const string PublishButton = "Publish…";
    public const string RefreshButton = "Refresh";
    public const string MakeLocalCopyButton = "Make Local Copy…";
    public const string NewButton = "New…";
    public const string RenameAction = "Rename…";
    public const string DeleteAction = "Delete…";
    public const string StopPublishingAction = "Stop Publishing";
    public const string StopSyncingAction = "Stop Syncing (keeps a local copy)";
    public const string ActiveSuffix = " · active";
    public const string UnlocatedDetail = "synced · file not located on this machine";
    /// <summary>
    /// The source's status in the detail line. The cache records no timestamp, so this says what
    /// is true of the file, not how long ago it last changed.
    /// </summary>
    public const string UpdateAvailableStatus = "update available";
    public const string UpToDateStatus = "up to date";
    /// <summary>
    /// The two answers to the published-document question. Keep is the default: a file the team
    /// reads is not something to remove by pressing Return.
    /// </summary>
    public const string RemoveFileButton = "Remove";
    public const string KeepFileButton = "Keep";
    public const string RemoteType = "remote";
    /// <summary>The row's pencil, which names no connector: the row it sits on is the answer. The flyout's <c>ConnectorRow.EditTooltip</c> spells the name out, because that menu has no rows.</summary>
    public const string EditTooltip = "Edit";
    /// <summary>The sidebar's double-click, and the same action in its context menu.</summary>
    public const string MakeActiveAction = "Make Active";
    /// <summary>The lock at the head of a synced collection's row.</summary>
    public const string LockedGlyphTooltip = "Read-only: synced from the collection's author";

    public static string ExportButton(int count) => $"Export {count}…";

    /// <summary>
    /// The sidebar's chain glyph, or null when there is no chain to explain: a local collection
    /// has no source, and a synced one whose file is still to be found has no path to name. The
    /// sentence is the flyout chip's, borrowed rather than copied — one fact, one wording.
    ///
    /// Takes the item rather than the path so that "which items have a tooltip" stays here; a
    /// converter over an optional path would be the same rule, kept somewhere worse.
    /// </summary>
    public static string? SyncedGlyphTooltip(Item item) =>
        item.Source is { } source ? FlyoutModel.SourceTooltipFormat(source) : null;

    public static string LocalType(string command) => $"local · {command}";

    public static string LocalDetail(int count) => $"local · {count} connectors";

    /// <summary><paramref name="source"/> is the document's path on this machine, never the sidecar's origin, which is a UUID.</summary>
    public static string SyncedDetail(string source, string status) => $"synced from {source} · read-only · {status}";

    /// <summary>"this PC" is the platform-forced half of this sentence; the Mac mirror says "this Mac".</summary>
    public static string PublishedDetail(string folder) => $"publishes to {folder} from this PC";

    public static string DeletePublishedFileQuestion(string fileName) => $"Also remove {fileName} from the folder?";

    /// <summary>
    /// One collection in the left pane. A published collection carries no mark of its own there —
    /// IsPublished is what the detail line says, not a sidebar glyph.
    /// </summary>
    /// <param name="Source">
    /// Where a synced collection's document is, as far as this machine knows: the path it is
    /// bound to, or the name the sidecar recorded while the file is still to be found. Null for
    /// a local collection, which has no source, and for a synced one the sidecar never named.
    /// <c>AppState.SourceLocation</c> is the rule, shared with the flyout's chip and menu, so the
    /// sidebar's chain and the chip cannot name the same collection differently.
    /// </param>
    public sealed record Item(string Name, CollectionKind Kind, bool IsActive, bool IsPublished,
        bool HasPendingUpdate, bool IsLocated, string? Source = null)
    {
        public string Id => Name;
    }

    /// <summary>
    /// One connector of the selected collection. Checked is the window's own state — an export
    /// tick, not anything the store holds — so it is the one field the model fills in itself.
    /// </summary>
    public sealed record Row(string Name, bool Enabled, string? Caution, bool IsLocked, bool Checked, string TypeText)
    {
        public string Id => Name;
    }

    /// <summary>Property names this window actually depends on — everything else AppState raises is noise for it.</summary>
    private static readonly string[] RelevantProperties =
    [
        nameof(AppState.Store), nameof(AppState.CollectionsFile), nameof(AppState.CollectionsCache),
        nameof(AppState.PendingUpdates), nameof(AppState.SourceErrors), nameof(AppState.PublishError),
    ];

    private readonly AppState state;
    private readonly IDialogs dialogs;
    private readonly HashSet<string> checkedNames = new(StringComparer.Ordinal);
    private string? lastError;
    /// <summary>What the view last picked, which may name a collection that no longer exists; Selected resolves it.</summary>
    private string? selection;
    /// <summary>
    /// Which collection the ticks above belong to. The window shows one collection at a time, and
    /// a tick must not survive into another one that happens to hold a connector of that name.
    /// </summary>
    private string? checkedCollection;
    /// <summary>
    /// The two panes, kept rather than rebuilt on every read. WPF regenerates every container when
    /// ItemsSource is handed a new list, which drops keyboard focus from the row that had it — so
    /// a list is replaced, and announced, only when what it holds has actually changed. The Mac
    /// needs none of this: SwiftUI's List diffs its rows by id and keeps an unchanged row's view.
    /// </summary>
    private IReadOnlyList<Item> items = [];
    private IReadOnlyList<Row> rows = [];

    public CollectionsModel(AppState state, IDialogs dialogs)
    {
        this.state = state;
        this.dialogs = dialogs;
        items = ComputeItems();
        rows = ComputeRows();
        state.PropertyChanged += OnStateChanged;
    }

    /// <summary>What the last action AppState refused reported, cleared by the next one that succeeds.</summary>
    public string? LastError { get => lastError; private set => Set(ref lastError, value); }

    // MARK: selection

    /// <summary>
    /// The collection the right pane is showing. It defaults to the active one and falls back to
    /// it whenever the chosen name stops being a collection — deleted here, or renamed from
    /// anywhere else.
    /// </summary>
    /// <remarks>
    /// An assignment that would show the collection already showing does nothing at all — no
    /// raise, no cleared ticks, not even the name remembered. The sidebar's two-way binding writes
    /// its selection straight back, and a raise here fed it until the stack ran out; remembering
    /// the name would pin the window to a collection it was only showing because it was the
    /// active one.
    /// </remarks>
    public string? Selected
    {
        get => SelectedCollection;
        set
        {
            if (EffectiveCollection(value) == SelectedCollection)
            {
                ForgetUnresolvedSelection();
                return;
            }
            selection = value;
            checkedNames.Clear();
            checkedCollection = null;
            RefreshRows();
            RaiseSelectionDependents();
        }
    }

    private string SelectedCollection => EffectiveCollection(selection);

    /// <summary>
    /// Lets go of a remembered name that no longer names a collection. Nothing on screen changes —
    /// the window was already showing the fallback — so nothing is raised. Without it, a collection
    /// renamed away and later renamed back would pull the window to it unprompted, because the
    /// name would start resolving again. Called from the no-op branch of <see cref="Selected"/>
    /// and on every store change, so a view that never writes its selection back is covered too.
    /// </summary>
    private void ForgetUnresolvedSelection()
    {
        if (selection is { } remembered && !state.Store.Collections.ContainsKey(remembered))
        {
            selection = null;
        }
    }

    /// <summary>
    /// Everything that follows the collection on show, and nothing that does not — in particular
    /// not <see cref="Items"/>, whose content the selection never touches.
    /// </summary>
    private void RaiseSelectionDependents()
    {
        Raise(nameof(Selected));
        Raise(nameof(DetailLine));
        Raise(nameof(BannerText));
        Raise(nameof(BannerButton));
        Raise(nameof(HasBanner));
        Raise(nameof(CanExport));
        Raise(nameof(CanPublish));
        Raise(nameof(CanRefresh));
        Raise(nameof(CanMakeLocalCopy));
        Raise(nameof(CanStopSyncing));
        Raise(nameof(CanStopPublishing));
        Raise(nameof(CanDelete));
    }

    /// <summary>What a chosen name resolves to: itself while it is a collection, the active one otherwise.</summary>
    private string EffectiveCollection(string? chosen) =>
        chosen is { } name && state.Store.Collections.ContainsKey(name) ? name : state.ActiveCollection;

    /// <summary>The ticks, but only while the collection they were made in is still the one showing.</summary>
    private IReadOnlySet<string> ActiveChecks =>
        checkedCollection == SelectedCollection ? checkedNames : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Moves the window to a collection this model just created or renamed, keeping the ticks with
    /// it — unlike Selected, which is the user picking a different collection.
    /// </summary>
    private void Retarget(string name)
    {
        if (checkedCollection is not null)
        {
            checkedCollection = name;
        }
        selection = name;
        RefreshRows();
        RaiseSelectionDependents();
    }

    // MARK: panes

    public IReadOnlyList<Item> Items => items;

    public IReadOnlyList<Row> Rows => rows;

    private List<Item> ComputeItems()
    {
        var active = state.ActiveCollection;
        return state.CollectionNames
            .Select(name => new Item(name, state.KindOf(name), name == active, state.IsPublished(name),
                state.PendingUpdates.ContainsKey(name), state.IsLocated(name), state.SourceLocation(name)))
            .ToList();
    }

    /// <summary>
    /// Both panes, each replaced and announced only when its content differs. The records are
    /// value-equal, so a rebuild that says the same thing leaves the list — and the focus on it —
    /// where it was.
    /// </summary>
    private void RefreshPanes()
    {
        var fresh = ComputeItems();
        if (!fresh.SequenceEqual(items))
        {
            items = fresh;
            Raise(nameof(Items));
        }
        RefreshRows();
    }

    private void RefreshRows()
    {
        var fresh = ComputeRows();
        if (!fresh.SequenceEqual(rows))
        {
            rows = fresh;
            Raise(nameof(Rows));
            Raise(nameof(CheckedNames));
        }
    }

    private List<Row> ComputeRows()
    {
        var collection = SelectedCollection;
        var locked = state.IsSynced(collection);
        var checks = ActiveChecks;
        var mcps = state.Store.Collections.TryGetValue(collection, out var held)
            ? held.Mcps
            : new Dictionary<string, McpEntry>(StringComparer.Ordinal);
        // Ordinal, which is how the flyout sorts the same connectors of the same collection: two
        // surfaces over one list must agree on its order.
        return mcps.Keys
            .Order(StringComparer.Ordinal)
            .Select(name => new Row(name, mcps[name].Enabled, state.ConnectorCaution(name, collection),
                locked, checks.Contains(name), TypeTextOf(mcps[name].Config)))
            .ToList();
    }

    /// <summary>
    /// The type column: the bridge the remote form recognises, or the launcher this connector
    /// runs, named the way it would be typed rather than by its full path.
    /// </summary>
    private static string TypeTextOf(JsonValue config) =>
        RemotePattern.Detect(config) is not null
            ? RemoteType
            : LocalType(LauncherName(FormMapper.Analyze(config).Model.Command));

    /// <summary>
    /// The last component of a command, splitting on both separators rather than this platform's:
    /// a collection carries connectors authored on either, and a Mac command's launcher is still
    /// worth naming on a PC. Split by hand, because the path APIs on the two platforms disagree
    /// about which separators count.
    /// </summary>
    private static string LauncherName(string command)
    {
        var parts = command.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : command;
    }

    public string DetailLine
    {
        get
        {
            var collection = SelectedCollection;
            if (state.IsSynced(collection))
            {
                return LocatedSource(collection) is { } source
                    ? SyncedDetail(source, SyncStatus(collection))
                    : UnlocatedDetail;
            }
            var count = state.Store.Collections.TryGetValue(collection, out var held) ? held.Mcps.Count : 0;
            var line = LocalDetail(count);
            if (collection == state.ActiveCollection)
            {
                line += ActiveSuffix;
            }
            // Only this machine's binding says where the document goes, so only this machine's
            // window says it publishes. Another machine's publish record is not a fact about this one.
            if (state.CollectionsCache.Published.TryGetValue(collection, out var binding))
            {
                line += " · " + PublishedDetail(binding.Folder);
            }
            return line;
        }
    }

    /// <summary>
    /// The synced document as this machine can name it, or null when it cannot: not synced, no
    /// binding, or a sidecar entry that records neither a path nor a file name. The last of those
    /// takes a hand-edited or foreign collections file — every writer here sets a file name — but
    /// the decoder accepts one, and there is nothing to refresh or to report about a document
    /// nobody can point at.
    /// </summary>
    /// <remarks>
    /// Still its own rule — the detail line has a sentence of its own for an unlocated file — but
    /// the naming is <c>AppState.SourceLocation</c>'s, so the derivation lives in one place.
    /// </remarks>
    private string? LocatedSource(string collection) =>
        state.IsLocated(collection) ? state.SourceLocation(collection) : null;

    /// <summary>What the source is doing, in precedence order: what went wrong outranks what is waiting.</summary>
    private string SyncStatus(string collection)
    {
        if (state.SourceErrors.TryGetValue(collection, out var failure))
        {
            return failure;
        }
        return state.PendingUpdates.ContainsKey(collection) ? UpdateAvailableStatus : UpToDateStatus;
    }

    // MARK: banner strip

    /// <summary>
    /// The banner above the rows, or null. Unlike the flyout's slot, which speaks for whichever
    /// collection has news, this answers only for the collection the window is showing: a strip
    /// over one collection's rows saying something about another one would be a lie.
    /// </summary>
    private CollectionBanner? Banner =>
        state.CollectionBanner is { } banner
            && CollectionBannerPresentation.Collection(banner) == SelectedCollection
            ? banner
            : null;

    public string? BannerText => Banner is { } banner ? CollectionBannerPresentation.Text(banner, state) : null;

    public string? BannerButton => Banner is { } banner ? CollectionBannerPresentation.Button(banner) : null;

    /// <summary>The Mac binds the optional above directly; XAML needs a bool for the strip's visibility.</summary>
    public bool HasBanner => Banner is not null;

    /// <summary>
    /// The strip's button. True says the news is an update, so the view has only to put the Review
    /// dialog in front of the selected collection. False says the view decides by the banner's
    /// kind: a file or folder dialog, handed to <see cref="LocateSource"/> or
    /// <see cref="ChoosePublishFolder"/>, or — for a publish blocked for review — the Publish
    /// dialog, since another folder is no answer to that.
    /// </summary>
    public bool BannerAction() => Banner is CollectionBanner.UpdateAvailable;

    /// <summary>
    /// The Locate button's file, for the collection the window is showing. Null on success, else
    /// the message; also null when the strip is not asking for a file, so a dialog left open past
    /// the news it belonged to cannot point anything anywhere.
    /// </summary>
    public string? LocateSource(string path) =>
        Banner is CollectionBanner.Locate locate ? state.LocateSource(locate.Collection, path) : null;

    /// <summary>
    /// The Choose Folder button's folder, for the collection the window is showing. Null as
    /// <see cref="LocateSource"/> returns null. Under a publish blocked for review the folder is
    /// refused inside <c>AppState.ChangePublishFolder</c>, which answers with the reason.
    /// </summary>
    public string? ChoosePublishFolder(string folder) => Banner switch
    {
        CollectionBanner.PublishFailed failed => state.ChangePublishFolder(failed.Collection, folder),
        CollectionBanner.PublishBlocked blocked => state.ChangePublishFolder(blocked.Collection, folder),
        _ => null,
    };

    // MARK: toolbar

    public bool CanExport => !state.IsSynced(SelectedCollection) && ActiveChecks.Count > 0;

    /// <summary>
    /// Any local collection, published or not. Reopening the dialog on a published one shows what
    /// its record says — the folder, every shared value, every marked path — and pressing Publish
    /// again updates the record and rewrites the document. That is the only way to change what is
    /// shared or to mark a path again, so it stays offered beside Stop Publishing. A synced
    /// collection has an author elsewhere and nothing here to publish.
    /// </summary>
    public bool CanPublish => !state.IsSynced(SelectedCollection);

    /// <summary>Refresh reads the bound document, so it needs one this machine can name.</summary>
    public bool CanRefresh => LocatedSource(SelectedCollection) is not null;

    public bool CanMakeLocalCopy => state.IsSynced(SelectedCollection);

    public bool CanStopSyncing => state.IsSynced(SelectedCollection);

    public bool CanStopPublishing => state.IsPublished(SelectedCollection);

    /// <summary>
    /// The last local collection stays, because only a local one takes a new connector, and the
    /// last collection of any kind stays, because the store always has an active one. A synced
    /// collection is never the last local one, so only the second rule reaches it.
    /// </summary>
    public bool CanDelete
    {
        get
        {
            var collection = SelectedCollection;
            return state.IsSynced(collection)
                ? state.CollectionNames.Count > 1
                : state.LocalCollectionNames.Count > 1;
        }
    }

    /// <summary>
    /// Through the rows rather than the tick set, so a tick on a connector that has since
    /// vanished from the collection is dropped instead of exported.
    /// </summary>
    public IReadOnlyList<string> CheckedNames => Rows.Where(r => r.Checked).Select(r => r.Name).ToList();

    // MARK: rows

    /// <summary>A synced collection's rows cannot be exported, so they cannot be ticked either.</summary>
    public void SetChecked(string name, bool on)
    {
        var collection = SelectedCollection;
        if (state.IsSynced(collection))
        {
            return;
        }
        if (checkedCollection != collection)
        {
            checkedNames.Clear();
            checkedCollection = collection;
        }
        if (on)
        {
            checkedNames.Add(name);
        }
        else
        {
            checkedNames.Remove(name);
        }
        RefreshRows();
        Raise(nameof(CanExport));
    }

    /// <summary>The row switch, in the collection the window is showing rather than the active one.</summary>
    public void SetEnabled(string name, bool on)
    {
        state.SetEnabled(name, on, SelectedCollection);
        LastError = null;
    }

    /// <summary>
    /// The pencil: the same connector in two collections is two windows, so the target carries the
    /// collection this window is showing. The Mac calls this <c>editTarget(for:)</c>; here the
    /// returned type already owns that name.
    /// </summary>
    public EditTarget EditTargetFor(string row)
    {
        var collection = SelectedCollection;
        var entry = state.Store.Collections.TryGetValue(collection, out var held) && held.Mcps.TryGetValue(row, out var found)
            ? found
            : new McpEntry(JsonValue.Object());
        return EditTarget.Existing(row, entry, collection);
    }

    /// <summary>The names the export sheet writes, in the order the rows show them.</summary>
    public IReadOnlyList<string> ExportIntentForChecked() => CheckedNames;

    // MARK: collection actions

    public void Create()
    {
        if (dialogs.PromptForName(AppState.NewCollectionTitle, "") is not { } typed)
        {
            return;
        }
        if (Report(state.CreateCollection(typed)))
        {
            Retarget(typed.TrimSpaces());
        }
    }

    public void Rename()
    {
        var collection = SelectedCollection;
        if (dialogs.PromptForName(AppState.RenameCollectionTitle, collection) is not { } typed)
        {
            return;
        }
        // The store trimmed the name the same way; following it keeps the window on the collection
        // the user just renamed rather than dropping back to the active one.
        if (Report(state.RenameCollection(collection, typed)))
        {
            Retarget(typed.TrimSpaces());
        }
    }

    public void Delete()
    {
        var collection = SelectedCollection;
        if (!dialogs.Confirm(AppState.DeleteCollectionMessage(collection), null, AppState.DeleteButton, destructive: true))
        {
            return;
        }
        // The store refuses to delete the last local collection, and the last one of any kind.
        // Asked here as well as in the toolbar, so a refusal cannot arrive after the publishing
        // below has already stopped. The store reports it: it refuses before it touches anything,
        // so asking it early is a no-op that still produces the right message.
        if (!CanDelete)
        {
            Report(state.DeleteCollection(collection));
            return;
        }
        if (PublishedFileName(collection) is { } fileName)
        {
            state.StopPublishing(collection, AskAboutPublishedFile(fileName));
        }
        if (Report(state.DeleteCollection(collection)))
        {
            Selected = null;   // back to the active collection
        }
    }

    /// <summary>Stop Publishing: the collection stays, and only the document in the folder is in question.</summary>
    public void StopPublishing()
    {
        var collection = SelectedCollection;
        if (!state.IsPublished(collection))
        {
            return;
        }
        // Nothing on this machine writes the document when there is no binding for it, so there is
        // no file here to offer to remove. Nor is there anything to ask while the last write
        // failed: the folder that refused it would refuse the delete too, so the question would be
        // one whose Remove cannot be honoured. The banner's own Stop Publishing says the same by
        // passing false outright.
        // Only a failed write puts the folder out of reach. A publish blocked for review never
        // touched it, so its document can still be removed and the question still stands.
        var failedWrite = state.PublishError is { } error
            && error.Collection == collection && error.Kind == PublishErrorKind.WriteFailed;
        var deleteFile = !failedWrite
            && PublishedFileName(collection) is { } fileName
            && AskAboutPublishedFile(fileName);
        state.StopPublishing(collection, deleteFile);
        LastError = null;
    }

    /// <summary>
    /// Stop Syncing keeps every connector, every filled value and every switch, so there is
    /// nothing to warn about and nothing to confirm.
    /// </summary>
    public void StopSyncing()
    {
        state.StopSyncing(SelectedCollection);
        LastError = null;
    }

    public void Refresh()
    {
        state.RefreshSource(SelectedCollection);
        LastError = null;
    }

    /// <summary>The whole synced collection again as a local one the user can edit.</summary>
    public void MakeLocalCopy()
    {
        var collection = SelectedCollection;
        if (!state.IsSynced(collection))
        {
            return;
        }
        if (dialogs.PromptForName(AppState.NewCollectionTitle, collection) is not { } typed)
        {
            return;
        }
        if (Report(state.MakeLocalCopyOfCollection(collection, typed)))
        {
            Retarget(typed.TrimSpaces());
        }
    }

    public void SwitchTo(string name)
    {
        state.SwitchCollection(name);
        LastError = null;
    }

    // MARK: helpers

    /// <summary>The document this machine writes for the collection, or null when nothing here publishes it.</summary>
    private string? PublishedFileName(string collection) =>
        state.CollectionsCache.Published.ContainsKey(collection)
        && state.CollectionsFile.Collections.TryGetValue(collection, out var entry)
        && entry.Publish is { } record
            ? record.Slug + "." + CollectionDocument.FileExtension
            : null;

    /// <summary>Default no: the view's default button is Keep, and this model only records the answer.</summary>
    private bool AskAboutPublishedFile(string fileName) =>
        dialogs.Confirm(DeletePublishedFileQuestion(fileName), null, RemoveFileButton, KeepFileButton, destructive: false);

    private bool Report(string? error)
    {
        LastError = error;
        return error is null;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (RelevantProperties.Any(name => Affects(e, name)))
        {
            ForgetUnresolvedSelection();
            RefreshPanes();
            RaiseSelectionDependents();
        }
    }

    public void Dispose() => state.PropertyChanged -= OnStateChanged;
}
