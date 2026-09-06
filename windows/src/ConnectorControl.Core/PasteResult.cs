namespace ConnectorControl.Core;

/// <summary>The recovered connector name (when the paste carried one) and its config.</summary>
public sealed record PasteResult(string? Name, JsonValue Config);
