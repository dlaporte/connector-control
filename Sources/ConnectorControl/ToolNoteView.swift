import SwiftUI
import ConnectorControlCore
import ConnectorControlState

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
/// shell can see, the sentence that says so). Layout only: the facts are the ToolRow's.
struct ToolRowView: View {
    let row: ToolRow

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(row.name)
                    .font(.system(.body, design: .monospaced))
                Spacer()
                Text(row.statusText)
                    .foregroundStyle(row.isProblem ? Color.orange : Color.secondary)
            }
            if let note = row.note {
                if row.isShellOnly {
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
