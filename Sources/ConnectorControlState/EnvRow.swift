import Foundation

/// One environment-variable row (catalog §3.6). Rows carry a stable identity
/// while the name is edited — a dictionary key cannot, since each keystroke
/// would re-key the dictionary, re-sort the list, and drop field focus.
/// Values are masked unless `revealed` (only freshly added rows start revealed).
public struct EnvRow: Identifiable, Equatable, Sendable {
    public let id: UUID
    public var name: String
    public var value: String
    public var revealed: Bool

    public init(name: String, value: String, revealed: Bool = false) {
        id = UUID()
        self.name = name
        self.value = value
        self.revealed = revealed
    }
}
