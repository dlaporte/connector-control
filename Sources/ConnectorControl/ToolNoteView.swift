import SwiftUI
import ConnectorControlCore
import ConnectorControlState

/// The tool note: what is wrong, the advice line when there is one
/// (the shell-only state), then the install line. `showsText`/`textStyle` let
/// `ToolRowView` embed this for its advice + install line without repeating
/// them, while keeping its own status text as the row's "what is wrong" line.
struct ToolNoteView: View {
    let note: ToolNote
    var showsText: Bool = true
    var textStyle: Color = .orange

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            if showsText {
                Text(note.text)
                    .font(.caption)
                    .foregroundStyle(textStyle)
            }
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

/// One Settings ▸ Claude ▸ Tools row: name, status, and — when
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
                // Only the shell-only state repeats the note's own text line;
                // the row's status text above already says "Not found".
                ToolNoteView(note: note, showsText: row.isShellOnly, textStyle: .secondary)
            }
        }
    }
}
