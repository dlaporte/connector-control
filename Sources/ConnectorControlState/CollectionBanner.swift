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
    /// The last attempt to write a published collection's document failed. Another folder is an
    /// answer, which is why this banner offers Choose Folder….
    case publishFailed(collection: String, message: String)
    /// Publishing refused to write, for the author to review — a marked path that has moved, say.
    /// Another folder answers nothing here: it would re-bind the collection, write nothing there
    /// and abandon the document the old folder still holds. The Publish sheet is the answer.
    case publishBlocked(collection: String, message: String)
}

/// Which way a publish did not land. The kind decides which banner shows, because the two want
/// opposite answers from the user.
///
/// Mirror: windows/src/ConnectorControl.Core/State/CollectionBanner.cs
public enum PublishErrorKind: Equatable, Sendable {
    /// The write itself failed: the folder is gone, read-only or full.
    case writeFailed
    /// Publishing stopped before writing, because something needs the author's review first.
    case blockedForReview
}

/// The collection whose last publish did not land, why, and which kind of not-landing it was.
/// `kind` defaults to a failed write, which is what every caller meant before the kind existed.
///
/// Mirror: windows/src/ConnectorControl.Core/State/CollectionBanner.cs
public struct CollectionPublishError: Equatable, Sendable {
    public let collection: String
    public let message: String
    public let kind: PublishErrorKind

    public init(collection: String, message: String, kind: PublishErrorKind = .writeFailed) {
        self.collection = collection
        self.message = message
        self.kind = kind
    }
}
