/// What the popover has asked the Collections window to do the moment it opens. The popover's
/// banner can only open the window; the sheet that should be in front of it afterwards is carried
/// here, because the window is the only surface that owns those sheets.
///
/// Mirror: windows/src/ConnectorControl.Core/State/CollectionsWindowRequest.cs
public enum CollectionsWindowRequest: Equatable, Sendable {
    /// Review & Apply: the window selects this collection and shows the Review sheet.
    case review(collection: String)
    /// A publish blocked for review: the window selects this collection and shows the Publish
    /// sheet, which is where a moved mark is placed again.
    case publish(collection: String)
}
