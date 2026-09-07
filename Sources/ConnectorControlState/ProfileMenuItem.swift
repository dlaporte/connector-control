/// One entry of the profile chip's menu; the active one carries the check mark.
public struct ProfileMenuItem: Identifiable, Equatable, Sendable {
    public let name: String
    public let isActive: Bool

    public init(name: String, isActive: Bool) {
        self.name = name
        self.isActive = isActive
    }

    public var id: String { name }
}
