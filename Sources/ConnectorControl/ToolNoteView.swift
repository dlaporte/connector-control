import SwiftUI
import ConnectorControlCore
import ConnectorControlState

/// The tool note: what is wrong, the advice line when there is one
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

/// One Settings ▸ Claude ▸ Tools row, one line tall: the name, and its version or what is wrong. How to fix
/// it is in the group's footer. Layout only: the facts are the ToolRow's.
struct ToolRowView: View {
    let row: ToolRow

    var body: some View {
        HStack {
            Text(row.name)
                .font(.system(.body, design: .monospaced))
            Spacer()
            Text(row.statusText)
                .foregroundStyle(row.isProblem ? Color.orange : Color.secondary)
                .lineLimit(1)
        }
    }
}

/// One line of the Tools footer: the family's tools, its link, "or run" and the command in the code face;
/// under it, for a tool only the login shell sees, the advice.
struct ToolFooterLineView: View {
    let line: ToolFooterLine

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(shown)
            if let advice = line.advice {
                Text(advice)
            }
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    /// One text, so the line wraps as a sentence: the link's title as a link, the command in the code face.
    private var shown: AttributedString {
        line.runs.reduce(into: AttributedString()) { text, run in
            guard run.isSetApart else { return text += AttributedString(run.text) }
            if run.text == line.linkTitle {
                var link = AttributedString(run.text)
                link.link = line.linkURL
                text += link
            } else {
                var command = AttributedString(run.shownText)
                command.font = .system(.caption, design: .monospaced)
                text += command
            }
        }
    }
}
