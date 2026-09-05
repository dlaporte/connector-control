import Foundation

/// The editor note and the Settings row text for one tool (spec §3.4, §3.5,
/// strings §5). Both platforms carry these strings verbatim.
public struct ToolNote: Equatable, Sendable {
    public static let orRun = "or run"
    public static let checkingText = "Checking…"
    public static let foundText = "Found"
    public static let notFoundText = "Not found"
    public static let shellOnlyStatusText = "Not visible to Claude Desktop"
    public static let shellOnlyAdvice =
        "Install it with Homebrew or the official installer, which put it where Claude Desktop looks; version managers such as nvm install elsewhere."
    public static let settingsHeader = "Tools"
    public static let settingsCaption =
        "Connectors that run through npx, node, uvx or uv need them installed where Claude Desktop can find them."

    /// Line 1: what is wrong.
    public let text: String
    /// Line 2, shell-only state only: how to move the tool to where Claude
    /// Desktop looks. nil in every other state (spec §3.4).
    public let advice: String?
    /// The install line: `linkTitle` (a link to `linkURL`), then "or run", then `installCommand`.
    public let linkTitle: String
    public let linkURL: URL
    public let installCommand: String

    public static func missingText(_ tool: Tool) -> String {
        "\(tool.name) wasn’t found, so Claude Desktop won’t be able to start this connector."
    }

    public static func shellOnlyText(_ tool: Tool, path: String) -> String {
        "\(tool.name) is at \(path) in your shell, but Claude Desktop launches connectors "
            + "with its own PATH and may not see it there."
    }

    /// nil while the status is unknown or the tool is found where Claude looks.
    /// Both problem states offer the family's ordinary install command: it puts
    /// the tool in the PATH floor, which is what makes the note go away.
    public static func make(tool: Tool, status: ToolStatus?) -> ToolNote? {
        switch status {
        case nil, .found?:
            return nil
        case .notFound?:
            return ToolNote(text: missingText(tool),
                            advice: nil,
                            linkTitle: tool.family.linkTitle,
                            linkURL: tool.family.linkURL,
                            installCommand: tool.family.installCommand)
        case .foundInShellOnly(let path, _)?:
            return ToolNote(text: shellOnlyText(tool, path: path),
                            advice: shellOnlyAdvice,
                            linkTitle: tool.family.linkTitle,
                            linkURL: tool.family.linkURL,
                            installCommand: tool.family.installCommand)
        }
    }

    /// The Settings row's right-hand text.
    public static func statusText(_ status: ToolStatus?) -> String {
        switch status {
        case nil: return checkingText
        case .found(_, let version)?: return version ?? foundText
        case .foundInShellOnly?: return shellOnlyStatusText
        case .notFound?: return notFoundText
        }
    }
}
