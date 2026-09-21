/// One entry of the collection chip's menu; the active one carries the check mark.
public struct CollectionMenuItem: Identifiable, Equatable, Sendable {
    public let name: String
    public let isActive: Bool

    public init(name: String, isActive: Bool) {
        self.name = name
        self.isActive = isActive
    }

    public var id: String { name }
}
