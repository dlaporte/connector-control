namespace ConnectorControl.Core.State;

/// <summary>
/// What the banner slot under the error banner has to say about a collection, if anything.
/// <see cref="AppState"/> derives exactly one of these from the pending updates, the bindings
/// and the last publish failure; the models turn it into a sentence and a button title, so the
/// wording lives with the model that shows it rather than here.
///
/// Mirror: Sources/ConnectorControlState/CollectionBanner.swift
/// </summary>
public abstract record CollectionBanner
{
    private CollectionBanner()
    {
    }

    /// <summary>A synced collection's source has changes waiting to be reviewed.</summary>
    public sealed record UpdateAvailable(string Collection, string Summary) : CollectionBanner;

    /// <summary>A synced collection's document has never been found on this machine.</summary>
    public sealed record Locate(string Collection, string FileName) : CollectionBanner;

    /// <summary>The last attempt to write a published collection's document failed.</summary>
    public sealed record PublishFailed(string Collection, string Message) : CollectionBanner;

    /// <summary>
    /// Publishing refused to write, for the author to review — a marked path that has moved, say.
    /// Another folder answers nothing here: it would re-bind the collection, write nothing there
    /// and abandon the document the old folder still holds. The Publish dialog is the answer.
    /// </summary>
    public sealed record PublishBlocked(string Collection, string Message) : CollectionBanner;
}

/// <summary>
/// Which way a publish did not land. The kind decides which banner shows, because the two want
/// opposite answers from the user.
///
/// Mirror: Sources/ConnectorControlState/CollectionBanner.swift
/// </summary>
public enum PublishErrorKind
{
    /// <summary>The write itself failed: the folder is gone, read-only or full.</summary>
    WriteFailed,

    /// <summary>Publishing stopped before writing, because something needs the author's review first.</summary>
    BlockedForReview,
}

/// <summary>
/// The collection whose last publish did not land, why, and which kind of not-landing it was.
/// <paramref name="Kind"/> defaults to a failed write, which is what every caller meant before the
/// kind existed. The Mac spells this as a struct of the same name.
/// </summary>
public sealed record CollectionPublishError(
    string Collection, string Message, PublishErrorKind Kind = PublishErrorKind.WriteFailed);
