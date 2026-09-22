import SwiftUI
import ConnectorControlState

/// Layout only — the row's facts arrive in a ConnectorRow and its two actions
/// go back through the closures.
struct MCPRow: View {
    /// The lock leading a synced collection's row, dimmed so it reads as a mark rather than as
    /// a control — the switch and the pencil beside it are still live.
    private static let lockOpacity = 0.55

    let row: ConnectorRow
    var onToggle: (Bool) -> Void
    var onEdit: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Toggle("", isOn: Binding(get: { row.enabled }, set: onToggle))
                .toggleStyle(.switch)
                .controlSize(.small)
                .labelsHidden()
            if row.isLocked {
                // A glyph on its own reads as nothing, and this one is only ever on screen
                // while the collection is synced, so its sentence is what it says out loud.
                Image(systemName: "lock.fill")
                    .imageScale(.small)
                    .foregroundStyle(.secondary)
                    .opacity(MCPRow.lockOpacity)
                    .help(CollectionsModel.lockedGlyphTooltip)
                    .accessibilityLabel(CollectionsModel.lockedGlyphTooltip)
            }
            Text(row.name).fontWeight(.medium)
                .lineLimit(1)
                .layoutPriority(1)
            if let warning = row.toolWarning {
                // Advisory only: the switch above stays live and the row height is
                // unchanged. The tooltip sends the user to the editor's full note.
                Image(systemName: PopoverModel.toolWarningGlyph)
                    .imageScale(.small)
                    .foregroundStyle(.orange)
                    .help(warning)
                    .accessibilityLabel(warning)
            }
            Spacer()
            Button {
                onEdit()
            } label: {
                Image(systemName: "pencil")
                    .imageScale(.medium)
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.accessoryBar)
            .help(row.editTooltip)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
    }
}
