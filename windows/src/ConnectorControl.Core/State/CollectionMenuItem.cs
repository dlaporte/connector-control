namespace ConnectorControl.Core.State;

/// <summary>
/// One entry of the collection chip's menu; the active one carries the check mark, a synced one
/// the chain, and one whose source has changes the amber dot after it.
/// </summary>
/// <param name="Source">
/// Where this machine reads the collection's document, by <c>AppState.SourceLocation</c>'s rule,
/// so the menu can name every synced collection's source and not only the active one.
/// </param>
public sealed record CollectionMenuItem(
    string Name, bool IsActive, bool IsSynced = false, bool HasPendingUpdate = false, string? Source = null);
