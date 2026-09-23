namespace ConnectorControl.Core.State;

/// <summary>
/// The copy clash sheet: opened only when <see cref="CollectionsModel.CheckedNamesClashing"/>
/// says the ticked connectors are not all clean, to ask what to do about the ones the destination
/// already holds. Not <see cref="ImportModel"/>, which is bound to a document path — the copy
/// already has its own engine, <see cref="CollectionsModel.CopyChecked"/>; this model only builds
/// the rows and hands its answers back to it.
///
/// Mirror: Sources/ConnectorControlState/CopyModel.swift
/// </summary>
public sealed class CopyModel : ObservableObject
{
    /// <summary>
    /// One ticked connector against the destination. A clash needs a choice; a clean arrival
    /// needs only its badge, which is why a clashing row carries none — its picker says enough.
    /// </summary>
    public sealed class Row(string name, bool clashes, ImportChoice choice, string badge) : ObservableObject
    {
        private ImportChoice choice = choice;

        public string Id { get; } = name;
        public string Name { get; } = name;

        /// <summary>True when the destination already holds this name.</summary>
        public bool Clashes { get; } = clashes;

        /// <summary>
        /// KeepBoth by default, clash or not: AppState.MakeLocalCopy's own default for a
        /// collision nothing is said about, so silence here lands the copy beside the original
        /// rather than losing it.
        /// </summary>
        public ImportChoice Choice { get => choice; set => Set(ref choice, value); }

        /// <summary>
        /// What a clean row says about itself; empty for a clashing one, whose picker is the
        /// row's answer instead of a badge.
        /// </summary>
        public string Badge { get; } = badge;
    }

    public static string Title(string destination) => $"Copy to “{destination}”";
    public const string CopyButton = "Copy";

    private readonly CollectionsModel collections;

    public string Destination { get; }
    public IReadOnlyList<Row> Rows { get; }

    public CopyModel(CollectionsModel collections, string destination)
    {
        this.collections = collections;
        Destination = destination;
        var clashing = new HashSet<string>(collections.CheckedNamesClashing(destination), StringComparer.Ordinal);
        // The ticks' own display order, not a re-sort: the sheet asks about the same rows the
        // window just showed, in the order the user saw them.
        Rows = collections.CheckedNames.Select(name =>
        {
            var clashes = clashing.Contains(name);
            return new Row(name, clashes, ImportChoice.KeepBoth, clashes ? "" : ImportModel.NewBadge);
        }).ToList();
    }

    /// <summary>
    /// Whether any row still needs an answer — the same test that decides whether this sheet is
    /// worth opening at all.
    /// </summary>
    public bool NeedsAnswers => Rows.Any(row => row.Clashes);

    /// <summary>
    /// Hands every clashing row's choice to the copy engine and leaves a clean row to it: nothing
    /// is in its way, so MakeLocalCopy needs nothing said about it. true when the copies landed.
    /// </summary>
    public bool Perform()
    {
        var choices = new Dictionary<string, ImportChoice>(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            if (row.Clashes)
            {
                choices[row.Name] = row.Choice;
            }
        }
        return collections.CopyChecked(Destination, choices);
    }
}
