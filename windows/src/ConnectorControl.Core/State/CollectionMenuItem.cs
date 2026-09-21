namespace ConnectorControl.Core.State;

/// <summary>One entry of the collection chip's menu; the active one carries the check mark.</summary>
public sealed record CollectionMenuItem(string Name, bool IsActive);
