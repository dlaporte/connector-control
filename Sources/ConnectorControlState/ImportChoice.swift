/// What one connector of an imported document does about the connector the target collection
/// already has under that name. `add` is the case where nothing is in the way. Each caller picks
/// its own default for a collision: Import leaves the row unticked, which sends `skip`, and
/// preselects Replace should it be ticked; Copy preselects Keep both, AppState's own default.
///
/// Mirror: windows/src/ConnectorControl.Core/State/ImportChoice.cs
public enum ImportChoice: String, Equatable, Sendable {
    case add, replace, keepBoth, skip
}
