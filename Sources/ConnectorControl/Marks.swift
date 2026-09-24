import SwiftUI
import ConnectorControlState

/// The lock leading a synced collection's row, dimmed so it reads as a mark rather than as a
/// control. A glyph on its own reads as nothing, so it speaks the sentence its tooltip shows.
struct LockMark: View {
    /// Shared with the editor's field locks, which are smaller but the same mark.
    static let opacity = 0.55

    let label: String

    var body: some View {
        Image(systemName: "lock.fill")
            .imageScale(.small)
            .foregroundStyle(.secondary)
            .opacity(LockMark.opacity)
            .help(label)
            .accessibilityLabel(label)
    }
}

/// The caution glyph: advisory only, never a control. With a sentence it shows and speaks it;
/// without one it sits beside text that already says it, and a screen reader skips it.
struct CautionMark: View {
    let text: String?

    init(_ text: String?) {
        self.text = text
    }

    var body: some View {
        let glyph = Image(systemName: PopoverModel.toolWarningGlyph)
            .imageScale(.small)
            .foregroundStyle(.orange)
        if let text {
            glyph.help(text).accessibilityLabel(text)
        } else {
            glyph.accessibilityHidden(true)
        }
    }
}

/// An update waiting at the source of the collection this sits beside: the amber dot, which says
/// so to a screen reader and in its tooltip.
struct PendingDot: View {
    var body: some View {
        Circle()
            .fill(.orange)
            .frame(width: 6, height: 6)
            .help(PopoverModel.pendingSpokenLabel)
            .accessibilityLabel(PopoverModel.pendingSpokenLabel)
    }
}
