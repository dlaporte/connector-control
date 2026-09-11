import ConnectorControlCore

/// One Settings ▸ Claude ▸ Tools row (spec §3.5): name, status text, and the
/// install note when there is something to do.
public struct ToolRow: Equatable, Sendable {
    public let name: String
    public let statusText: String
    public let isProblem: Bool
    public let note: ToolNote?
    /// macOS only (catalog §4.4): the row also shows the note's first line.
    public let isShellOnly: Bool

    public init(name: String, statusText: String, isProblem: Bool, note: ToolNote?, isShellOnly: Bool) {
        self.name = name
        self.statusText = statusText
        self.isProblem = isProblem
        self.note = note
        self.isShellOnly = isShellOnly
    }

    public static func make(tool: Tool, status: ToolStatus?) -> ToolRow {
        let isProblem: Bool
        let isShellOnly: Bool
        switch status {
        case .notFound?:
            isProblem = true
            isShellOnly = false
        case .foundInShellOnly?:
            isProblem = true
            isShellOnly = true
        default:
            isProblem = false
            isShellOnly = false
        }
        return ToolRow(name: tool.name, statusText: ToolNote.statusText(status), isProblem: isProblem,
                       note: ToolNote.make(tool: tool, status: status), isShellOnly: isShellOnly)
    }
}
