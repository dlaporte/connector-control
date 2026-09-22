/// What one connector of an imported document does about the connector the target collection
/// already has under that name. `add` is the case where nothing is in the way; `skip` is the
/// default for a collision, so an import never overwrites anything the user did not ask it to.
///
/// Mirror: windows/src/ConnectorControl.Core/State/ImportChoice.cs
public enum ImportChoice: String, Equatable, Sendable {
    case add, replace, keepBoth, skip
}
