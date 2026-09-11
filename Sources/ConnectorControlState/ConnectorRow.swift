/// MCPRow's data: a switch, the name, an advisory caution glyph, and a
/// pencil button. A value: SwiftUI identifies the row by name, and toggling
/// goes through `PopoverModel.setEnabled`.
public struct ConnectorRow: Identifiable, Equatable, Sendable {
    public let name: String
    public let enabled: Bool
    /// The caution glyph's tooltip, or nil for no glyph: this connector's
    /// launcher is not where Claude Desktop looks. Advisory only — the row
    /// still toggles.
    public let toolWarning: String?

    public init(name: String, enabled: Bool, toolWarning: String?) {
        self.name = name
        self.enabled = enabled
        self.toolWarning = toolWarning
    }

    public var id: String { name }

    public var editTooltip: String { "Edit “\(name)”" }
}
