namespace ConnectorControl.Core;

public sealed record ReconcileOutcome(MasterStore Store, bool StoreChanged);
