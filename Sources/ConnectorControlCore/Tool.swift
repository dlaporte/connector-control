import Foundation

/// The four launchers a connector's command can name — what `npx mcp-remote`
/// and most local servers run through. When one is missing from the PATH
/// Claude Desktop uses, Claude shows only "server disconnected"
/// (spec 2026-09-05-tool-probe §3.1).
public enum Tool: String, CaseIterable, Sendable {
    case npx, node, uvx, uv

    /// The basename as typed in a config (`npx`, never `npx.cmd`).
    public var name: String { rawValue }

    public var family: ToolFamily {
        switch self {
        case .npx, .node: return .nodeJS
        case .uvx, .uv: return .uv
        }
    }

    /// Case-insensitive lookup by basename.
    public init?(name: String) {
        guard let tool = Tool.allCases.first(where: { $0.rawValue == name.lowercased() }) else {
            return nil
        }
        self = tool
    }
}

/// What installs a tool: Node.js brings node and npx; uv brings uv and uvx.
public enum ToolFamily: Sendable {
    case nodeJS, uv

    public var linkTitle: String {
        switch self {
        case .nodeJS: return "Install Node.js"
        case .uv: return "Install uv"
        }
    }

    public var linkURL: URL {
        switch self {
        case .nodeJS: return URL(string: "https://nodejs.org/en/download")!
        case .uv: return URL(string: "https://docs.astral.sh/uv/getting-started/installation/")!
        }
    }

    /// The macOS install command; the Windows port shows its own package manager's
    /// command instead (spec §6 D3). Homebrew installs into the PATH floor Claude
    /// Desktop adds for itself, so running this clears the note.
    public var installCommand: String {
        switch self {
        case .nodeJS: return "brew install node"
        case .uv: return "brew install uv"
        }
    }
}

/// Where a probe found a tool, if anywhere (spec §3.2.3).
public enum ToolStatus: Equatable, Sendable {
    /// State C: nowhere this probe looked.
    case notFound
    /// State A: on the PATH this app was launched with, or in the PATH floor
    /// Claude Desktop adds for itself.
    case found(path: String, version: String?)
    /// State B: only on the login shell's PATH (nvm, a user's own bin
    /// directory): Claude Desktop may not see it.
    case foundInShellOnly(path: String, version: String?)
}
