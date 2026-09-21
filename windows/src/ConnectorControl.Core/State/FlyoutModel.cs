using System.Collections.ObjectModel;
using System.ComponentModel;
using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.State;

/// <summary>The popover view, minus pixels: header, error banner, rows, footer, and every action it wires.</summary>
public sealed class FlyoutModel : ObservableObject, IDisposable
{
    public const string Title = Product.Name;
    public const string AddTooltip = "Add Connector";
    public const string SettingsTooltip = "Settings";
    public const string QuitTooltip = "Quit Connector Control";
    public const string EmptyText = "No connectors configured yet — add one below.";
    public const string RetryTitle = "Apply Failed — Retry";
    public const string RestartTitle = "Restart Required";
    public const string AddDisabledTooltip = "Additions go in a local collection.";
    public const string ReviewAndApplyButton = "Review & Apply…";
    public const string ChooseFolderButton = "Choose Folder…";
    /// <summary>Segoe Fluent Icons: Warning (exclamationmark.arrow.circlepath's nearest) and Refresh (arrow.clockwise).</summary>
    public const string RetryGlyph = "\ue7ba";
    public const string RestartGlyph = "\ue72c";
    /// <summary>Segoe Fluent Icons: Warning, on a row whose launcher is missing. The same code point the retry footer uses, named separately so changing one does not move the other.</summary>
    public const string ToolWarningGlyph = "\ue7ba";
    /// <summary>Shown in the error banner when nothing worse is: the store's folder refused the owner-only permission.</summary>
    public const string StoreNotPrivateCaution = "The master list could not be made private: its folder refused the permission change, so connector secrets in it are readable by anyone who can read that folder.";

    public static string SettingsNotSavedCaution(string detail) => $"Settings could not be saved ({detail}); changes apply until the app quits.";

    public static string LocateButton(string fileName) => $"Locate {fileName}…";

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

    /// <summary>The Mac's static and an instance property of the same name can coexist there; C# forbids that, so the instance property below calls this.</summary>
    public static string CollectionChipTextFor(string active) => $"{active} ▾";

    public string CollectionChipText => CollectionChipTextFor(state.ActiveCollection);

    public IReadOnlyList<CollectionMenuItem> CollectionItems => collectionItems;

    /// <summary>Nothing can be added to a synced collection: its content is the source file's.</summary>
    public bool CanAddConnector => !state.ActiveCollectionIsSynced;

    public string? ErrorMessage => state.LastError
        ?? (state.StoreNotPrivate ? StoreNotPrivateCaution : null)
        ?? (settings.LastSaveError is { } e ? SettingsNotSavedCaution(e) : null);

    public bool HasError => ErrorMessage is not null;

    public CollectionBanner? CollectionBanner => state.CollectionBanner;

    public string? CollectionBannerText => state.CollectionBanner switch
    {
        State.CollectionBanner.UpdateAvailable update => AppState.CollectionUpdateBanner(update.Collection, update.Summary),
        State.CollectionBanner.Locate locate => AppState.CollectionLocateBanner(locate.Collection),
        // The folder is the binding's, not the banner's: publishing is what sets the error, so
        // the collection that failed always has one.
        State.CollectionBanner.PublishFailed failed => AppState.CollectionPublishFailedBanner(
            failed.Collection,
            state.CollectionsCache.Published.TryGetValue(failed.Collection, out var binding) ? binding.Folder : "",
            failed.Message),
        _ => null,
    };

    public string? CollectionBannerButton => state.CollectionBanner switch
    {
        State.CollectionBanner.UpdateAvailable => ReviewAndApplyButton,
        State.CollectionBanner.Locate locate => LocateButton(locate.FileName),
        State.CollectionBanner.PublishFailed => ChooseFolderButton,
        _ => null,
    };

    public bool HasCollectionBanner => CollectionBannerText is not null;

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

    public void SwitchCollection(string name) => state.SwitchCollection(name);

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

    /// <summary>The pencil button opens the editor only if the entry still exists in the store.</summary>
    public McpEntry? EntryFor(string name) => state.Store.Mcps.TryGetValue(name, out var entry) ? entry : null;

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
                Rows[i].Sync(enabled, warning);
            }
            else if (rowsByName.TryGetValue(name, out var existing))
            {
                Rows.Remove(existing);
                existing.Sync(enabled, warning);
                Rows.Insert(i, existing);
            }
            else
            {
                var row = new ConnectorRow(state, name, enabled, warning);
                rowsByName[name] = row;
                Rows.Insert(i, row);
            }
        }
        var active = state.ActiveCollection;
        collectionItems = state.CollectionNames
            .Select(n => new CollectionMenuItem(n, n == active, state.IsSynced(n), state.PendingUpdates.ContainsKey(n)))
            .ToList();
        RaiseAll();
    }

    public void Dispose() => state.PropertyChanged -= OnStateChanged;
}
