namespace ConnectorControl.Core.State;

/// <summary>
/// One entry of the collection chip's menu; the active one carries the check mark, a synced one
/// the chain, and one whose source has changes the amber dot after it.
/// </summary>
public sealed record CollectionMenuItem(string Name, bool IsActive, bool IsSynced = false, bool HasPendingUpdate = false);
