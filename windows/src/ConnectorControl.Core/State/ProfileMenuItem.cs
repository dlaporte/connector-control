namespace ConnectorControl.Core.State;

/// <summary>One entry of the profile chip's menu; the active one carries the check mark.</summary>
public sealed record ProfileMenuItem(string Name, bool IsActive);
