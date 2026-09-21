namespace ConnectorControl.Core.State;

/// <summary>
/// The Review &amp; Apply sheet: what one synced collection's source would change, connector by
/// connector, and the one button that lets the change land. The sheet shows the JSON both sides
/// of each change, because every entry here is a command Claude will run.
///
/// Mirror: Sources/ConnectorControlState/ReviewModel.swift
/// </summary>
public sealed class ReviewModel : ObservableObject
{
    public const string ApplyButton = "Apply";
    public const string AddedLabel = "Added";
    public const string RemovedLabel = "Removed";
    public const string ChangedLabel = "Changed";
    public const string SourceMovedMessage = "The file changed while this was open. Review it again.";

    public static string Title(string collection) => $"Update to {collection}";

    public enum Kind
    {
        Added,
        Removed,
        Changed,
    }

    /// <summary>
    /// One connector's before and after, as the editor would show them. A missing side is the
    /// side that does not exist: nothing before an addition, nothing after a removal.
    /// </summary>
    public sealed record Row(string Name, Kind Kind, string? Before, string? After)
    {
        public string Id => Name;
    }

    private readonly AppState state;
    private IReadOnlyList<Row> rows = [];
    private bool sourceMoved;
    /// <summary>The document the rows were built from. Apply compares it with what the app holds now.</summary>
    private string? sourceHash;

    public ReviewModel(AppState state, string collection)
    {
        this.state = state;
        Collection = collection;
        Rebuild();
    }

    public string Collection { get; }

    public IReadOnlyList<Row> Rows { get => rows; private set => Set(ref rows, value); }

    /// <summary>
    /// The document moved under the sheet: what is listed is no longer what would land, so Apply
    /// refuses until the rows are rebuilt. Cleared by <see cref="Refresh"/>.
    /// </summary>
    public bool SourceMoved { get => sourceMoved; private set => Set(ref sourceMoved, value); }

    /// <summary>The Mac calls this <c>title</c>; here the static factory already owns that name.</summary>
    public string SheetTitle => Title(Collection);

    /// <summary>The same sentence the banner and the toast carry, so the sheet the user just opened says what the thing they clicked said.</summary>
    public string Summary => state.PendingUpdates.TryGetValue(Collection, out var diff) ? diff.Summary() : string.Empty;

    /// <summary>
    /// Re-reads what the app holds now: the rows the sheet shows and the document they belong to.
    /// The caller reaches for this after a refused Apply, and whenever it reopens the sheet.
    /// </summary>
    public void Refresh()
    {
        SourceMoved = false;
        Rebuild();
        RaiseAll();
    }

    /// <summary>
    /// True when the update landed. A sheet whose update has already been applied elsewhere
    /// reports success too: there is nothing left to do and nothing went wrong. A document that
    /// changed while the sheet was open is refused instead — applying it would land connectors
    /// nobody read, which is the one thing the review gate exists to prevent.
    /// </summary>
    public bool Apply()
    {
        if (state.PendingSourceHash(Collection) != sourceHash)
        {
            SourceMoved = true;
            return false;
        }
        var error = state.ApplyPendingUpdate(Collection);
        Rebuild();
        RaiseAll();
        return error is null;
    }

    /// <summary>Added first, then removed, then changed, each alphabetical — the order the summary sentence reads in, so the list under it is in the same order.</summary>
    private void Rebuild()
    {
        sourceHash = state.PendingSourceHash(Collection);
        if (!state.PendingUpdates.TryGetValue(Collection, out var diff))
        {
            Rows = [];
            return;
        }
        var rendered = state.PendingDocument(Collection)?.Connectors
            ?? new Dictionary<string, RenderedConnector>(StringComparer.Ordinal);
        var current = state.Store.Collections.TryGetValue(Collection, out var held)
            ? held.Mcps
            : new Dictionary<string, McpEntry>(StringComparer.Ordinal);
        string? After(string name) => rendered.GetValueOrDefault(name)?.Config.EditorText();
        string? Before(string name) => current.GetValueOrDefault(name)?.Config.EditorText();
        Rows =
        [
            .. diff.Added.Select(name => new Row(name, Kind.Added, null, After(name))),
            .. diff.Removed.Select(name => new Row(name, Kind.Removed, Before(name), null)),
            .. diff.Changed.Select(name => new Row(name, Kind.Changed, Before(name), After(name))),
        ];
    }
}
