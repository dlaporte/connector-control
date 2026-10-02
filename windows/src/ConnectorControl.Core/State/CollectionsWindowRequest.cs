namespace ConnectorControl.Core.State;

/// <summary>
/// What the flyout has asked the Collections window to do the moment it opens. The flyout's banner
/// can only open the window; the dialog that should be in front of it afterwards is carried here,
/// because the window is the only surface that owns those dialogs.
///
/// This window can be given its arguments directly, unlike the Mac's <c>WindowGroup</c>; the
/// request is mirrored anyway so the two models stay one for one.
///
/// Mirror: Sources/ConnectorControlState/CollectionsWindowRequest.swift
/// </summary>
public abstract record CollectionsWindowRequest
{
    private CollectionsWindowRequest()
    {
    }

    /// <summary>Review &amp; Apply: the window selects this collection and shows the Review dialog.</summary>
    public sealed record Review(string Collection) : CollectionsWindowRequest;

    /// <summary>
    /// A publish blocked for review: the window selects this collection and shows the Publish
    /// dialog, which is where a moved mark is placed again.
    /// </summary>
    public sealed record Publish(string Collection) : CollectionsWindowRequest;

    /// <summary>
    /// Manage Collections from the flyout's empty state: the window selects this collection, the
    /// active one, which is where its connectors are added.
    /// </summary>
    public sealed record Select(string Collection) : CollectionsWindowRequest;
}
