namespace ConnectorControl.Core;

public sealed record ReconcileOutcome(MasterStore Store, bool StoreChanged)
{
    /// <summary>The names the file's additions are kept under, in the collection <see cref="Reconciler.Reconcile"/> took them into.</summary>
    public IReadOnlyList<string> Ingested { get; init; } = [];
}
