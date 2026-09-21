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

    public ReviewModel(AppState state, string collection)
    {
        this.state = state;
        Collection = collection;
        Rebuild();
    }

    public string Collection { get; }

    public IReadOnlyList<Row> Rows { get => rows; private set => Set(ref rows, value); }

    /// <summary>The Mac calls this <c>title</c>; here the static factory already owns that name.</summary>
    public string SheetTitle => Title(Collection);

    /// <summary>The same sentence the banner and the toast carry, so the sheet the user just opened says what the thing they clicked said.</summary>
    public string Summary => state.PendingUpdates.TryGetValue(Collection, out var diff) ? diff.Summary() : string.Empty;

    /// <summary>
    /// True when the update landed. A sheet whose update has already been applied elsewhere
    /// reports success too: there is nothing left to do and nothing went wrong.
    /// </summary>
    public bool Apply()
    {
        var error = state.ApplyPendingUpdate(Collection);
        Rebuild();
        RaiseAll();
        return error is null;
    }

    /// <summary>Added first, then removed, then changed, each alphabetical — the order the summary sentence reads in, so the list under it is in the same order.</summary>
    private void Rebuild()
    {
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
