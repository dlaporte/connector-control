import SwiftUI
import ConnectorControlState

/// Layout only — the row's facts arrive in a ConnectorRow and its one action
/// goes back through the closure.
struct MCPRow: View {
    let row: ConnectorRow
    var onToggle: (Bool) -> Void

    var body: some View {
        HStack(spacing: 10) {
            Toggle("", isOn: Binding(get: { row.enabled }, set: onToggle))
                .toggleStyle(.switch)
                .controlSize(.small)
                .labelsHidden()
                // The name beside it is a separate element; the switch has to say whose it is.
                .accessibilityLabel(row.name)
            if row.isLocked {
                // Only ever on screen while the collection is synced; the switch beside it is
                // still live.
                LockMark(label: row.lockTooltip)
            }
            Text(row.name).fontWeight(.medium)
                .lineLimit(1)
                .layoutPriority(1)
            if let warning = row.toolWarning {
                // Advisory only: the switch above stays live and the row height is
                // unchanged. The tooltip sends the user to the editor's full note.
                CautionMark(warning)
            }
            Spacer()
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
    }
}
