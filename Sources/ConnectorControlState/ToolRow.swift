import ConnectorControlCore

/// One Settings ▸ Claude ▸ Tools row: name, status text, and the
/// install note when there is something to do.
public struct ToolRow: Equatable, Sendable {
    public let name: String
    public let statusText: String
    public let isProblem: Bool
    public let note: ToolNote?
    /// macOS only: the row also shows the note's first line.
    public let isShellOnly: Bool

    public init(name: String, statusText: String, isProblem: Bool, note: ToolNote?, isShellOnly: Bool) {
        self.name = name
        self.statusText = statusText
        self.isProblem = isProblem
        self.note = note
        self.isShellOnly = isShellOnly
    }

    public static func make(tool: Tool, status: ToolStatus?) -> ToolRow {
        let note = ToolNote.make(tool: tool, status: status)
        // A note exists exactly for the two problem states; `advice` is set
        // only in the shell-only one (ToolNote.make), so both facts read
        // straight off the note instead of re-switching on `status`.
        return ToolRow(name: tool.name, statusText: ToolNote.statusText(status),
                       isProblem: note != nil, note: note, isShellOnly: note?.advice != nil)
    }
}
