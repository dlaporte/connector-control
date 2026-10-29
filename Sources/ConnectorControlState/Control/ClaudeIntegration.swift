import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ClaudeIntegration.cs
///
/// Connector Control's own entry in Claude's config: what it is called, what it runs, and the
/// sentences that go with it. The name itself is reserved in Core, where the readers of Claude's
/// config and of collection documents live; everything else reads it from here.
public enum ClaudeIntegration {
    public static let entryName = MasterStore.reservedConnectorName
    public static let reservedNameError = MasterStore.reservedConnectorNameError
    public static let clashNote = "A connector named “connector-control” already exists. Rename it to turn this on."
    /// Turning the integration on, or installing the command-line tool, from an app that runs from where
    /// it will not stay. Platform idiom: only a Mac app can; Windows installs into a fixed folder and never answers it.
    public static let moveToApplicationsError = "Move Connector Control to Applications, then try again."
    /// The app could not serve its control endpoint, so neither Claude nor the command-line tool reaches it.
    public static let endpointUnavailableText = "Claude and the command-line tool can’t reach Connector Control. Quit any other copy that is running, then restart this one."
    public static let offerTitle = "Let Claude manage Connector Control?"
    public static let offerBody = "Claude could list, switch and edit your connectors for you. This adds a connector named connector-control to Claude."
    public static let offerNotNowButton = "Not Now"
    public static let offerTurnOnButton = "Turn On"
    /// The argument that runs the bundled tool as the stdio relay Claude launches.
    public static let relayArgument = "mcp"

    /// The entry itself: the bundled tool by its full path, run as the relay. Claude never looks
    /// the tool up on PATH, so installing the command-line tool or not makes no difference to it.
    public static func entry(toolPath: String) -> JSONValue {
        .object(["command": .string(toolPath), "args": .array([.string(relayArgument)])])
    }
}
