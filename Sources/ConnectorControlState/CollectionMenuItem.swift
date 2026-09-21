/// One entry of the collection chip's menu; the active one carries the check mark, a synced one
/// the chain, and one whose source has changes the amber dot after it.
public struct CollectionMenuItem: Identifiable, Equatable, Sendable {
    public let name: String
    public let isActive: Bool
    public let isSynced: Bool
    public let hasPendingUpdate: Bool

    public init(name: String, isActive: Bool, isSynced: Bool = false, hasPendingUpdate: Bool = false) {
        self.name = name
        self.isActive = isActive
        self.isSynced = isSynced
        self.hasPendingUpdate = hasPendingUpdate
    }

    public var id: String { name }
}
