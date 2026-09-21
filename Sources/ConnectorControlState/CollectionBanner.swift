/// What the banner slot under the error banner has to say about a collection, if anything.
/// `AppState` derives exactly one of these from the pending updates, the bindings and the last
/// publish failure; the models turn it into a sentence and a button title, so the wording lives
/// with the model that shows it rather than here.
///
/// Mirror: windows/src/ConnectorControl.Core/State/CollectionBanner.cs
public enum CollectionBanner: Equatable, Sendable {
    /// A synced collection's source has changes waiting to be reviewed.
    case updateAvailable(collection: String, summary: String)
    /// A synced collection's document has never been found on this machine.
    case locate(collection: String, fileName: String)
    /// The last attempt to write a published collection's document failed.
    case publishFailed(collection: String, message: String)
}
