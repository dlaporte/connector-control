/// One entry of the collection chip's menu; the active one carries the check mark, a synced one
/// the chain, and one whose source has changes the amber dot after it.
///
/// Mirror: windows/src/ConnectorControl.Core/State/CollectionMenuItem.cs
public struct CollectionMenuItem: Identifiable, Equatable, Sendable {
    public let name: String
    public let isActive: Bool
    public let isSynced: Bool
    public let hasPendingUpdate: Bool
    /// Where this machine reads the collection's document, by `AppState.sourceLocation(of:)`'s
    /// rule, so the menu can name every synced collection's source and not only the active one.
    public let source: String?

    public init(name: String, isActive: Bool, isSynced: Bool = false, hasPendingUpdate: Bool = false,
                source: String? = nil) {
        self.name = name
        self.isActive = isActive
        self.isSynced = isSynced
        self.hasPendingUpdate = hasPendingUpdate
        self.source = source
    }

    public var id: String { name }
}
