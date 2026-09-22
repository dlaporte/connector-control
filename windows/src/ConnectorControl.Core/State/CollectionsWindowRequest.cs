namespace ConnectorControl.Core.State;

/// <summary>
/// What the flyout has asked the Collections window to do the moment it opens. The flyout's menu
/// and banner can only open the window; the dialog that should be in front of it afterwards is
/// carried here, because the window is the only surface that owns those dialogs.
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

    /// <summary>Import…: the window runs the file picker and shows the Import dialog.</summary>
    public sealed record ImportFile : CollectionsWindowRequest;

    /// <summary>Export "&lt;active&gt;"…: the window selects the active collection and shows the Export dialog.</summary>
    public sealed record ExportActive : CollectionsWindowRequest;

    /// <summary>Review &amp; Apply…: the window selects this collection and shows the Review dialog.</summary>
    public sealed record Review(string Collection) : CollectionsWindowRequest;

    /// <summary>
    /// A publish blocked for review: the window selects this collection and shows the Publish
    /// dialog, which is where a moved mark is placed again.
    /// </summary>
    public sealed record Publish(string Collection) : CollectionsWindowRequest;
}
