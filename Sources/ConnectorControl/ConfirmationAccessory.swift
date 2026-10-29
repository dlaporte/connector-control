import AppKit
import ConnectorControlState

/// Mac only: the Windows counterpart is windows/src/ConnectorControl.App/Views/ConfirmDialog.xaml(.cs).
///
/// What the alert for a change Claude asks for draws: the title as the alert's own message, and in its
/// accessory view the message with every name in the monospaced face, what would run in a block that
/// scrolls rather than wrap a line, a label a line each clipped to its own line, and the closing line.
/// Every set-apart piece is isolated where it is drawn (`TextRun.shownText`), every code line is drawn in
/// the order it is stored (`ConfirmationDialog.shownCode`), and every paragraph is laid out left to right.
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

    /// What would run: each line in a selectable label of its own, one line tall and clipped to it, so
    /// combining marks stacked on a line, however tall, can never draw over the line above or below; at
    /// most two are drawn on a character (`ControlText`), so a stack cut off by the clip cannot hide the
    /// line's own text either. One line of it is one line here, so the block scrolls sideways rather than
    /// wrap, and a long value can never pass for a row of its own. A line is selected and copied on its own.
    static func codeBlock(_ dialog: ConfirmationDialog) -> NSView {
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = true
        scroll.autohidesScrollers = true
        scroll.borderType = .bezelBorder
        scroll.drawsBackground = false
        let font = NSFont.monospacedSystemFont(ofSize: NSFont.smallSystemFontSize, weight: .regular)
        let fields = dialog.shownCode.enumerated().map { index, line in
            codeLine(line, font: font, color: color(dialog.codeKinds[index]))
        }
        // One line's height from the font alone, never from what a line holds, so marks cannot make a line taller.
        let lineHeight = ceil(codeLine("Xg", font: font, color: .labelColor).intrinsicContentSize.height)
        let lineWidth = ceil(fields.map(\.intrinsicContentSize.width).max() ?? 0)
        let document = FlippedView(frame: NSRect(x: 0, y: 0, width: lineWidth + 2 * codeInset.width,
                                                 height: CGFloat(fields.count) * lineHeight + 2 * codeInset.height))
        for (index, field) in fields.enumerated() {
            field.frame = NSRect(x: codeInset.width, y: codeInset.height + CGFloat(index) * lineHeight, width: lineWidth, height: lineHeight)
            document.addSubview(field)
        }
        scroll.documentView = document
        // With scroll bars always shown, the sideways one takes its height from the lines, so the block
        // makes room for it rather than hide the last line under it; an overlay scroller takes none.
        let scroller = NSScroller.preferredScrollerStyle == .legacy
            ? NSScroller.scrollerWidth(for: .regular, scrollerStyle: .legacy) : 0
        scroll.translatesAutoresizingMaskIntoConstraints = false
        scroll.heightAnchor.constraint(equalToConstant: min(codeHeight, document.frame.height + 4 + scroller)).isActive = true
        return scroll
    }

    /// The room between the block's border and its lines.
    static let codeInset = NSSize(width: 4, height: 6)

    /// One line of what would run: a selectable label, never wrapped, laid out left to right, and clipped
    /// to its own bounds, which the block sets to one line's height.
    static func codeLine(_ line: String, font: NSFont, color: NSColor) -> NSTextField {
        let field = NSTextField(labelWithString: line)
        field.font = font
        field.textColor = color
        field.isSelectable = true
        field.isEditable = false
        field.maximumNumberOfLines = 1
        field.lineBreakMode = .byClipping
        field.cell?.wraps = false
        field.baseWritingDirection = .leftToRight
        field.alignment = .left
        field.clipsToBounds = true
        return field
    }

    /// A line's colour: a removed line red and an added one green, as its own "−" or "+" also says.
    static func color(_ kind: ConfirmationDialog.LineKind) -> NSColor {
        switch kind {
        case .removed: return .systemRed
        case .added: return .systemGreen
        case .plain: return .labelColor
        }
    }
}

/// A view whose origin is its top left, so the code block's lines run down from the top as they are read.
final class FlippedView: NSView {
    override var isFlipped: Bool { true }
}
