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
}

/// <summary>
/// The collection whose last publish failed, and why. The Mac keeps this as an anonymous
/// <c>(collection, message)</c> tuple on AppState; a property needs a named type here, so the
/// pair gets one.
/// </summary>
public sealed record CollectionPublishError(string Collection, string Message);
