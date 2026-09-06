namespace ConnectorControl.Core.Services;

/// <summary>An available update. <c>Token</c> is the updater's own handle (opaque to Core).</summary>
public sealed record UpdateCheck(string Version, string? NotesMarkdown, object Token);
