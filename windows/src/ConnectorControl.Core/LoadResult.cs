namespace ConnectorControl.Core;

/// <summary>Outcome of <see cref="ConfigService.LoadAndReconcile"/>. <c>ClaudeServers</c> is null when Claude's config was unreadable.</summary>
public sealed record LoadResult(
    MasterStore Store,
    IReadOnlyList<string> Notes,
    IReadOnlyDictionary<string, JsonValue>? ClaudeServers);
