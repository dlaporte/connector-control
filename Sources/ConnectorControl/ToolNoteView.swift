import SwiftUI
import ConnectorControlCore

/// The tool note (spec §3.4): what is wrong, the advice line when there is one
/// (the shell-only state), then the install line.
struct ToolNoteView: View {
    let note: ToolNote

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(note.text)
                .font(.caption)
                .foregroundStyle(.orange)
            if let advice = note.advice {
                Text(advice)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            ToolNoteInstallLine(note: note)
        }
        .fixedSize(horizontal: false, vertical: true)
    }
}

/// The install line: the family's link, the words "or run", then the command.
struct ToolNoteInstallLine: View {
    let note: ToolNote

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 4) {
            Link(note.linkTitle, destination: note.linkURL)
                .font(.caption)
            (Text(ToolNote.orRun + " ")
                + Text(note.installCommand).font(.system(.caption, design: .monospaced)))
                .font(.caption)
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
        }
    }
}

/// One Settings ▸ Claude ▸ Tools row (spec §3.5): name, status, and — when
/// there is something to do — the install line (and, for a tool only the
/// shell can see, the sentence that says so).
struct ToolRowView: View {
    let tool: Tool
    let status: ToolStatus?

    private var isProblem: Bool {
        switch status {
        case .notFound?, .foundInShellOnly?: return true
        default: return false
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(tool.name)
                    .font(.system(.body, design: .monospaced))
                Spacer()
                Text(ToolNote.statusText(status))
                    .foregroundStyle(isProblem ? Color.orange : Color.secondary)
            }
            if let note = ToolNote.make(tool: tool, status: status) {
                if case .foundInShellOnly? = status {
                    Text(note.text)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if let advice = note.advice {
                    Text(advice)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                ToolNoteInstallLine(note: note)
            }
        }
    }
}
