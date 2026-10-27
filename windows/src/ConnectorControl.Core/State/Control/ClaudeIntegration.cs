namespace ConnectorControl.Core.State;

/// <summary>
/// Connector Control's own entry in Claude's config: what it is called, what it runs, and the
/// sentences that go with it. The name itself is reserved in Core, where the readers of Claude's
/// config and of collection documents live; everything else reads it from here.
///
/// Mirror: Sources/ConnectorControlState/Control/ClaudeIntegration.swift
/// </summary>
public static class ClaudeIntegration
{
    public const string EntryName = MasterStore.ReservedConnectorName;
    public const string ReservedNameError = MasterStore.ReservedConnectorNameError;
    public const string ClashNote = "A connector named “connector-control” already exists. Rename it to turn this on.";
    public const string OfferTitle = "Let Claude manage Connector Control?";
    public const string OfferBody = "Claude could list, switch and edit your connectors for you. This adds a connector named connector-control to Claude.";
    public const string OfferNotNowButton = "Not Now";
    public const string OfferTurnOnButton = "Turn On";
    /// <summary>The argument that runs the bundled tool as the stdio relay Claude launches.</summary>
    public const string RelayArgument = "mcp";

    /// <summary>The entry itself: the bundled tool by its full path, run as the relay. Claude never
    /// looks the tool up on PATH, so installing the command-line tool or not makes no difference to
    /// it.</summary>
    public static JsonValue Entry(string toolPath) =>
        JsonValue.Object(("command", JsonValue.String(toolPath)), ("args", JsonValue.Array([JsonValue.String(RelayArgument)])));
}
