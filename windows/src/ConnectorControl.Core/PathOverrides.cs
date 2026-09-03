namespace ConnectorControl.Core;

/// <summary>The two user settings that affect path resolution. Null or empty means "not set".</summary>
public sealed record PathOverrides(string? ClaudeConfigPath = null, string? MasterStoreDir = null);
