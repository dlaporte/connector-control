import Foundation

/// One argument field; a stable identity keeps focus while the list is edited.
public struct ArgRow: Identifiable, Equatable, Sendable {
    public let id: UUID
    public var value: String

    public init(value: String) {
        id = UUID()
        self.value = value
    }
}
