import AppKit
import ConnectorControlState

/// Mac only: the Windows counterpart is windows/src/ConnectorControl.App/Views/ConfirmDialog.xaml(.cs).
///
/// What the alert for a change Claude asks for draws: the title as the alert's own message, and in its
/// accessory view the message with every name in the monospaced face, what would run in a block that
/// scrolls rather than wrap a line, and the closing line. Every set-apart piece is isolated where it is
/// drawn (`TextRun.shownText`), and every paragraph is laid out left to right.
@MainActor
enum ConfirmationAccessory {
    static let width: CGFloat = 380
    /// The code block's height cap. Platform idiom: 200 points, as the Windows dialog's block is 200
    /// device-independent pixels, the same height at the same scale.
    static let codeHeight: CGFloat = 200

    /// The alert's message: the title as plain text, each name isolated. `NSAlert`'s message is a plain
    /// string, so the names in it cannot take another face; the Windows dialog's title can.
    static func title(_ dialog: ConfirmationDialog) -> String {
        dialog.title.map(\.shownText).joined()
    }

    /// The accessory: the message, what would run and the closing line, each when there is one.
    static func view(_ dialog: ConfirmationDialog) -> NSView {
        var rows: [NSView] = []
        if !dialog.message.isEmpty { rows.append(label(dialog.message, color: .labelColor)) }
        if !dialog.code.isEmpty { rows.append(codeBlock(dialog)) }
        if let footer = dialog.footer { rows.append(label(footer, color: .secondaryLabelColor)) }
        let stack = NSStackView(views: rows)
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 8
        for row in rows { row.widthAnchor.constraint(equalToConstant: width).isActive = true }
        stack.setFrameSize(stack.fittingSize)
        return stack
    }

    /// `runs` as one left-to-right paragraph at the alert's informative size: words in the system face,
    /// set-apart pieces in the monospaced one, isolated.
    static func label(_ runs: [TextRun], color: NSColor) -> NSTextField {
        let size = NSFont.smallSystemFontSize
        let paragraph = NSMutableParagraphStyle()
        paragraph.baseWritingDirection = .leftToRight
        paragraph.alignment = .left
        let text = NSMutableAttributedString()
        for run in runs {
            let font = run.isSetApart ? NSFont.monospacedSystemFont(ofSize: size, weight: .regular) : NSFont.systemFont(ofSize: size)
            text.append(NSAttributedString(string: run.shownText,
                                           attributes: [.font: font, .foregroundColor: color, .paragraphStyle: paragraph]))
        }
        let field = NSTextField(labelWithAttributedString: text)
        field.lineBreakMode = .byWordWrapping
        field.maximumNumberOfLines = 0
        field.preferredMaxLayoutWidth = width
        return field
    }

    /// What would run: one line of it is one line here, so the block scrolls sideways rather than wrap,
    /// and a long value can never pass for a row of its own. Selectable, so it can be copied.
    static func codeBlock(_ dialog: ConfirmationDialog) -> NSView {
        let scroll = NSTextView.scrollableTextView()
        scroll.hasHorizontalScroller = true
        scroll.autohidesScrollers = true
        scroll.borderType = .bezelBorder
        guard let view = scroll.documentView as? NSTextView else { return scroll }
        view.isEditable = false
        view.isSelectable = true
        view.drawsBackground = false
        view.textContainerInset = NSSize(width: 4, height: 6)
        view.isHorizontallyResizable = true
        view.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        view.textContainer?.widthTracksTextView = false
        view.textContainer?.containerSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        let font = NSFont.monospacedSystemFont(ofSize: NSFont.smallSystemFontSize, weight: .regular)
        let paragraph = NSMutableParagraphStyle()
        paragraph.baseWritingDirection = .leftToRight
        paragraph.lineBreakMode = .byClipping
        let text = NSMutableAttributedString()
        for (index, line) in dialog.code.enumerated() {
            let color: NSColor
            switch dialog.codeKinds[index] {
            case .removed: color = .systemRed
            case .added: color = .systemGreen
            case .plain: color = .labelColor
            }
            text.append(NSAttributedString(string: index == dialog.code.count - 1 ? line : line + "\n",
                                           attributes: [.font: font, .foregroundColor: color, .paragraphStyle: paragraph]))
        }
        view.textStorage?.setAttributedString(text)
        let lineHeight = NSLayoutManager().defaultLineHeight(for: font)
        // With scroll bars always shown, the sideways one takes its height from the lines, so the block
        // makes room for it rather than hide the last line under it; an overlay scroller takes none.
        let scroller = NSScroller.preferredScrollerStyle == .legacy
            ? NSScroller.scrollerWidth(for: .regular, scrollerStyle: .legacy) : 0
        scroll.translatesAutoresizingMaskIntoConstraints = false
        scroll.heightAnchor.constraint(equalToConstant: min(codeHeight, CGFloat(dialog.code.count) * lineHeight + 16 + scroller)).isActive = true
        return scroll
    }
}
