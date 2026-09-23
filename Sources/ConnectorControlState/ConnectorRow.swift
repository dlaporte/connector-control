/// MCPRow's data: a switch, the name, and an advisory caution glyph. A value:
/// SwiftUI identifies the row by name, and toggling goes through
/// `PopoverModel.setEnabled`.
public struct ConnectorRow: Identifiable, Equatable, Sendable {
    public let name: String
    public let enabled: Bool
    /// The caution glyph's tooltip, or nil for no glyph: this connector's
    /// launcher is not where Claude Desktop looks. Advisory only — the row
    /// still toggles.
    public let toolWarning: String?
    /// Leads the row with a lock: this connector belongs to the author of a synced collection's
    /// document. The switch stays live; everything else is read-only.
    public let isLocked: Bool

    public init(name: String, enabled: Bool, toolWarning: String?, isLocked: Bool = false) {
        self.name = name
        self.enabled = enabled
        self.toolWarning = toolWarning
        self.isLocked = isLocked
    }

    public var id: String { name }

    /// The lock's tooltip, the same sentence the Collections window's rows show for the same
    /// fact, borrowed rather than written twice.
    public var lockTooltip: String { CollectionsModel.lockedGlyphTooltip }
}
