import SwiftUI
import ConnectorControlState

/// Catalog §2.4: layout only — the row's facts arrive in a ConnectorRow and
/// its two actions go back through the closures.
struct MCPRow: View {
    let row: ConnectorRow
    var onToggle: (Bool) -> Void
    var onEdit: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Toggle("", isOn: Binding(get: { row.enabled }, set: onToggle))
                .toggleStyle(.switch)
                .controlSize(.small)
                .labelsHidden()
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
