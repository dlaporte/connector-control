import Foundation
import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/State/ToolFooterLine.cs
///
/// One line of the footer under Settings ▸ Claude ▸ Tools: how to install a family one of whose tools has a
/// problem. Each row says only what is wrong, so it stays one line tall; what to do is said here, once a family.
public struct ToolFooterLine: Equatable, Sendable {
    /// The whole line: the family's tools, the link's title, "or run" and the command.
    public let text: String
    /// The line with the link's title and the command set apart: the view draws the one as a link and the
    /// other in the code face.
    public let runs: [TextRun]
    public let linkTitle: String
    public let linkURL: URL
    public let installCommand: String
    /// macOS only: why a tool only the login shell sees is not enough, when one of the family's tools is in
    /// that state; nil otherwise.
    public let advice: String?

    public static func installText(_ tools: String, _ linkTitle: String, _ command: String) -> String { "\(tools): \(linkTitle) \(ToolNote.orRun) \(command)." }

    /// A line for each family with a tool that has a problem, in the rows' order; none while every tool is
    /// found or still being checked.
    public static func make(statuses: [Tool: ToolStatus]) -> [ToolFooterLine] {
        var families: [ToolFamily] = []
        var shellOnly: Set<ToolFamily> = []
        for tool in Tool.allCases {
            guard let note = ToolNote.make(tool: tool, status: statuses[tool]) else { continue }
            if !families.contains(tool.family) { families.append(tool.family) }
            if note.advice != nil { shellOnly.insert(tool.family) }
        }
        return families.map { family in
            let text = installText(family.toolNames, family.linkTitle, family.installCommand)
            return ToolFooterLine(text: text, runs: TextRun.runs(text, settingApart: [family.linkTitle, family.installCommand]),
                                  linkTitle: family.linkTitle, linkURL: family.linkURL, installCommand: family.installCommand,
                                  advice: shellOnly.contains(family) ? ToolNote.shellOnlyAdvice : nil)
        }
    }
}
