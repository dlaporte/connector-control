namespace ConnectorControl.Core;

/// <summary>
/// Outcome of <see cref="ConfigService.LoadAndReconcile"/>. <c>ClaudeServers</c> is null when Claude's config was
/// unreadable; <c>IngestedElsewhere</c> names what was taken in when it went somewhere other than the active collection.
/// </summary>
public sealed record LoadResult(
    MasterStore Store,
    IReadOnlyList<string> Notes,
    IReadOnlyDictionary<string, JsonValue>? ClaudeServers,
    IngestedElsewhere? IngestedElsewhere = null);
