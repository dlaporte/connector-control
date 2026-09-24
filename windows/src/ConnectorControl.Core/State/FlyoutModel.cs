using System.Collections.ObjectModel;
using System.ComponentModel;
using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.State;

/// <summary>
/// The flyout, minus pixels: header, error banner, rows, footer, and every action it wires.
///
/// Mirror: Sources/ConnectorControlState/PopoverModel.swift (named for the Mac surface)
/// </summary>
public sealed class FlyoutModel : ObservableObject, IDisposable
{
    public const string Title = Product.Name;
    public const string SettingsTooltip = "Settings";
    public const string QuitTooltip = "Quit Connector Control";
    public const string EmptyText = "No connectors configured yet.";
    public const string RetryTitle = "Apply Failed — Retry";
    public const string RestartTitle = "Restart Required";
    public const string ReviewAndApplyButton = "Review & Apply";
    public const string ChooseFolderButton = "Choose Folder";
    public const string ManageTitle = "Manage Collections";
    /// <summary>Segoe Fluent Icons: Warning (exclamationmark.arrow.circlepath's nearest) and Refresh (arrow.clockwise).</summary>
    public const string RetryGlyph = "\ue7ba";
    public const string RestartGlyph = "\ue72c";
    /// <summary>Segoe Fluent Icons: Warning, on a row whose launcher is missing. The same code point the retry footer uses, named separately so changing one does not move the other.</summary>
    public const string ToolWarningGlyph = "\ue7ba";
    /// <summary>
    /// The same glyph under the name the other surfaces call it by: the row caution it was
    /// written for is one of several the app now draws with it — an unfilled placeholder, a
    /// connector authored elsewhere, a document still to be located. One value, so the two
    /// cannot drift; two names, so neither call site has to lie about what it is marking.
    /// </summary>
    public const string CautionGlyph = ToolWarningGlyph;
    /// <summary>
    /// The amber dot's spoken form, beside the title that uses it so a view finds it here. The
    /// words are the Collections window's status for the same condition, borrowed rather than
    /// written twice, as <c>ConnectorRow.LockTooltip</c> borrows that window's lock sentence.
    /// </summary>
    public const string PendingSpokenLabel = CollectionsModel.UpdateAvailableStatus;
    /// <summary>
    /// What a Mac menu row says in place of the amber dot: its Menu row draws one title and one
    /// image, and the image is the chain, so a pending update has to be words. It is here for
    /// parity only. The flyout draws a real dot per row and speaks <see cref="PendingSpokenLabel"/>
    /// for it, so a Windows view that also used <see cref="MenuTitle"/> would mark each pending
    /// row twice.
    /// </summary>
    public const string PendingMenuMark = " · " + PendingSpokenLabel;
    /// <summary>Shown in the error banner when nothing worse is: the store's folder refused the owner-only permission.</summary>
    public const string StoreNotPrivateCaution = "The master list could not be made private: its folder refused the permission change, so connector secrets in it are readable by anyone who can read that folder.";

    public static string SettingsNotSavedCaution(string detail) => $"Settings could not be saved ({detail}); changes apply until the app quits.";

    public static string LocateButton(string fileName) => $"Locate {fileName}";

    /// <summary>
    /// One row of the collections menu, as the Mac draws it: the chain is the row's single image
    /// there, so a collection with news says so in the title. Here for parity; the flyout keeps
    /// its dot, for the reason <see cref="PendingMenuMark"/> gives.
    /// </summary>
    public static string MenuTitle(CollectionMenuItem item) =>
        item.HasPendingUpdate ? item.Name + PendingMenuMark : item.Name;

    /// <summary>
    /// One row's chain tooltip, naming where that collection's document is — every synced row,
    /// not only the active one the chip names. Null for a row with no chain to explain.
    /// </summary>
    public static string? MenuTooltip(CollectionMenuItem item) =>
        item.Source is { } source ? SourceTooltipFormat(source) : null;

    /// <summary>The chain glyph's tooltip. Named with the <c>Format</c> suffix because this side cannot carry a static and an instance member (<see cref="SourceTooltip"/>) under one name.</summary>
    public static string SourceTooltipFormat(string source) => $"Synced from {source}";

    /// <summary>Property names Rebuild actually depends on — everything else AppState raises is noise for this view.</summary>
    private static readonly string[] RelevantProperties =
    [
        nameof(AppState.Store), nameof(AppState.ToolStatuses), nameof(AppState.LastError),
        nameof(AppState.StoreNotPrivate), nameof(AppState.ApplyRetryNeeded), nameof(AppState.NeedsClaudeRestart),
        nameof(AppState.CollectionsFile), nameof(AppState.CollectionsCache), nameof(AppState.PendingUpdates),
        nameof(AppState.PublishError),
    ];

    private readonly AppState state;
    private readonly ISettings settings;
    private readonly Dictionary<string, ConnectorRow> rowsByName = new(StringComparer.Ordinal);
    private IReadOnlyList<CollectionMenuItem> collectionItems = [];

    public FlyoutModel(AppState state, ISettings settings)
    {
        this.state = state;
        this.settings = settings;
        Rows = [];
        state.PropertyChanged += OnStateChanged;
        Rebuild();
    }

    public string Subtitle => state.HeaderSubtitle;

    /// <summary>The chip's text is the bare name; the ▾, the chain and the dot are the view's glyphs.</summary>
    public string ActiveCollection => state.ActiveCollection;

    /// <summary>The chain beside the chip, and the lock on every row below it.</summary>
    public bool ActiveCollectionIsSynced => state.ActiveCollectionIsSynced;

    /// <summary>
    /// The amber dot beside the chip: the collection in front of the user has news at its
    /// source. Another collection's pending update is the menu's dot, not the chip's.
    /// </summary>
    public bool ActiveHasPendingUpdate => state.PendingUpdates.ContainsKey(state.ActiveCollection);

    /// <summary>
    /// The chain's tooltip: where the active collection's document sits on this machine, or the
    /// name the sidecar recorded while the file has still to be found. Null for a local
    /// collection, which has no source, and for a synced one the sidecar never named. A binding
    /// that needs a plain string coalesces it; the chain is drawn only for a synced collection,
    /// which is the case that has something to say.
    /// </summary>
    public string? SourceTooltip =>
        state.SourceLocation(state.ActiveCollection) is { } source ? SourceTooltipFormat(source) : null;

    public IReadOnlyList<CollectionMenuItem> CollectionItems => collectionItems;

    public string? ErrorMessage => state.LastError
        ?? (state.StoreNotPrivate ? StoreNotPrivateCaution : null)
        ?? (settings.LastSaveError is { } e ? SettingsNotSavedCaution(e) : null);

    public bool HasError => ErrorMessage is not null;

    public CollectionBanner? CollectionBanner => state.CollectionBanner;

    /// <summary>
    /// The flyout's slot speaks for whichever collection has news, active or not — unlike the
    /// Collections window's strip, which answers only for the collection it is showing.
    /// </summary>
    public string? CollectionBannerText =>
        state.CollectionBanner is { } banner ? CollectionBannerPresentation.Text(banner, state) : null;

    public string? CollectionBannerButton =>
        state.CollectionBanner is { } banner ? CollectionBannerPresentation.Button(banner) : null;

    public bool HasCollectionBanner => state.CollectionBanner is not null;

    public ObservableCollection<ConnectorRow> Rows { get; }

    public bool IsEmpty => state.Store.Mcps.Count == 0;

    public FooterKind Footer => state.ApplyRetryNeeded ? FooterKind.RetryApply
        : state.NeedsClaudeRestart ? FooterKind.RestartRequired
        : FooterKind.Hidden;

    public bool ShowFooter => Footer != FooterKind.Hidden;

    public string FooterTitle => Footer == FooterKind.RetryApply ? RetryTitle : RestartTitle;

    public string FooterGlyph => Footer == FooterKind.RetryApply ? RetryGlyph : RestartGlyph;

    /// <summary>The Mac popover's onAppear: a routine reload on every open, then the rows' launchers.</summary>
    public void Opened()
    {
        state.Reload();
        ProbeRowTools();
    }

    /// <summary>
    /// The tools the listed connectors need that are not cached yet.
    /// Nothing required, or everything cached, spawns no process;
    /// AppState coalesces a tool already in flight.
    /// </summary>
    private void ProbeRowTools()
    {
        var needed = ToolRequirement.RequiredTools(state.Store.Mcps.Values.Select(e => e.Config))
            .Where(t => !state.ToolStatuses.ContainsKey(t))
            .ToArray();
        if (needed.Length > 0)
        {
            _ = state.RefreshToolsAsync(needed);
        }
    }

    /// <summary>
    /// One row's caution-glyph tooltip, by the rule the editor and Settings also use: the
    /// entry's required tool, then that tool's cached status.
    /// </summary>
    private string? WarningFor(McpEntry entry)
    {
        if (ToolRequirement.RequiredTool(entry.Config) is not { } tool)
        {
            return null;
        }
        state.ToolStatuses.TryGetValue(tool, out var status);
        return ToolNote.RowWarning(tool, status);
    }

    /// <summary>
    /// A menu row. The ticked one is already active, and saving and applying it again would
    /// rewrite what is there and clear an error banner for nothing, so it is left alone.
    /// </summary>
    public void SwitchCollection(string name)
    {
        if (name == state.ActiveCollection)
        {
            return;
        }
        state.SwitchCollection(name);
    }

    /// <summary>
    /// The banner's button, for the two banners that need nothing from the user first: an update
    /// to review, and a publish blocked for review, each answered by a dialog in the Collections
    /// window. The bool is about order as much as outcome: on true the request is already waiting,
    /// so the view opens the Collections window and does nothing else; on false nothing has
    /// happened yet and the view runs its file dialog, then calls <see cref="LocateSource"/> or
    /// <see cref="ChoosePublishFolder"/> with what it gets. Opening the window before the call
    /// would let it take a null request.
    /// </summary>
    public bool CollectionBannerAction()
    {
        switch (state.CollectionBanner)
        {
            case State.CollectionBanner.UpdateAvailable:
                RequestReview();
                return true;
            case State.CollectionBanner.PublishBlocked blocked:
                // Another folder is no answer to this, so the banner never offers the folder dialog.
                state.CollectionsWindowRequest = new CollectionsWindowRequest.Publish(blocked.Collection);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The failed-publish banner's second button, where giving up on the folder is as reasonable
    /// an answer as choosing another one. Null for every other banner, which has one button.
    /// </summary>
    public string? CollectionBannerSecondaryButton =>
        state.CollectionBanner is { } banner ? CollectionBannerPresentation.SecondaryButton(banner) : null;

    /// <summary>
    /// Stop Publishing from the banner. The document in the folder is left where it is: this
    /// banner is raised by a folder this machine could not write to, so deleting from it is the
    /// one thing that cannot be offered. Nothing happens under any other banner.
    /// </summary>
    public void CollectionBannerSecondaryAction()
    {
        if (state.CollectionBanner is State.CollectionBanner.PublishFailed failed)
        {
            state.StopPublishing(failed.Collection, deleteFile: false);
        }
    }

    /// <summary>
    /// The Locate banner's file, for the collection that banner names. Null on success, else the
    /// message; also null when the banner has moved on since the dialog opened, because there is
    /// then no collection asking to be pointed anywhere.
    /// </summary>
    public string? LocateSource(string path) =>
        state.CollectionBanner is State.CollectionBanner.Locate locate
            ? state.LocateSource(locate.Collection, path)
            : null;

    /// <summary>
    /// The failed-publish banner's folder, for the collection that banner names. Publishing
    /// again into a new folder is what re-points it, so the recorded intent travels unchanged —
    /// the dialog is where what the document says gets edited. Null as <see cref="LocateSource"/>
    /// returns null. Under a publish blocked for review the folder is refused inside
    /// <c>AppState.ChangePublishFolder</c>, which answers with the reason, so a folder dialog
    /// reached by any route explains itself rather than doing nothing without a word.
    /// </summary>
    public string? ChoosePublishFolder(string folder) => state.CollectionBanner switch
    {
        State.CollectionBanner.PublishFailed failed => state.ChangePublishFolder(failed.Collection, folder),
        State.CollectionBanner.PublishBlocked blocked => state.ChangePublishFolder(blocked.Collection, folder),
        _ => null,
    };

    /// <summary>
    /// The banner's Review &amp; Apply, for the collection the banner names — which is not always
    /// the active one, so the name travels with the request.
    /// </summary>
    public void RequestReview()
    {
        if (state.CollectionBanner is State.CollectionBanner.UpdateAvailable update)
        {
            state.CollectionsWindowRequest = new CollectionsWindowRequest.Review(update.Collection);
        }
    }

    public void Quit() => state.QuitApp();

    /// <summary>The single footer button: retry the apply, or restart Claude.</summary>
    public void FooterAction()
    {
        if (state.ApplyRetryNeeded)
        {
            state.Apply();
        }
        else if (state.NeedsClaudeRestart)
        {
            _ = state.RestartClaudeAsync();
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (RelevantProperties.Any(name => Affects(e, name)))
        {
            Rebuild();
        }
    }

    /// <summary>Diffs Rows against the store so an in-flight toggle keeps its row object.</summary>
    private void Rebuild()
    {
        var names = state.SortedNames;
        // Locked together or not at all: the rows are the active collection's, so one being the
        // author's makes all of them so.
        var locked = state.ActiveCollectionIsSynced;
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!state.Store.Mcps.ContainsKey(Rows[i].Name))
            {
                rowsByName.Remove(Rows[i].Name);
                Rows.RemoveAt(i);
            }
        }
        for (int i = 0; i < names.Count; i++)
        {
            var name = names[i];
            var entry = state.Store.Mcps[name];
            var enabled = entry.Enabled;
            var warning = WarningFor(entry);
            if (i < Rows.Count && Rows[i].Name == name)
            {
                Rows[i].Sync(enabled, warning, locked);
            }
            else if (rowsByName.TryGetValue(name, out var existing))
            {
                Rows.Remove(existing);
                existing.Sync(enabled, warning, locked);
                Rows.Insert(i, existing);
            }
            else
            {
                var row = new ConnectorRow(state, name, enabled, warning, locked);
                rowsByName[name] = row;
                Rows.Insert(i, row);
            }
        }
        var active = state.ActiveCollection;
        collectionItems = state.CollectionNames
            .Select(n => new CollectionMenuItem(n, n == active, state.IsSynced(n), state.PendingUpdates.ContainsKey(n),
                state.SourceLocation(n)))
            .ToList();
        RaiseAll();
    }

    public void Dispose() => state.PropertyChanged -= OnStateChanged;
}
