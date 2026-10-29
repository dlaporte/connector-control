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

    /// What would run: each line in a selectable label of its own, in a box of its own that clips it, so
    /// combining marks stacked on a line, however tall, can never draw over the line above or below; at
    /// most two are drawn on a character (`ControlText`), so a stack cut off by the clip cannot hide the
    /// line's own text either. Every box is as tall as the code face's line, and taller only as far as the
    /// block's letters in another script, drawn in a taller fallback face, need (`ConfirmationDialog.extraRoom`),
    /// so Chinese, Arabic, Devanagari and Thai are drawn whole. One line of it is one line here, so the block
    /// scrolls sideways rather than wrap, and a long value can never pass for a row of its own. The block is
    /// one stop for Tab, as one text view was, and Up and Down move between its lines (`CodeLines`); a
    /// line is selected and copied on its own.
    static func codeBlock(_ dialog: ConfirmationDialog) -> NSView {
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = true
        scroll.autohidesScrollers = true
        scroll.borderType = .bezelBorder
        scroll.drawsBackground = false
        let font = NSFont.monospacedSystemFont(ofSize: NSFont.smallSystemFontSize, weight: .regular)
        let lines = CodeLines()
        let fields = dialog.shownCode.enumerated().map { index, line in
            codeLine(line, font: font, color: color(dialog.codeKinds[index]))
        }
        // One line's height from the face alone; the room past it from the letters alone, never the marks.
        let lineHeight = ceil(codeLine("Xg", font: font, color: .labelColor).intrinsicContentSize.height)
        let own = typographicBounds("Xg", font: font)
        var above: CGFloat = 0, below: CGFloat = 0
        for line in dialog.shownCode {
            let needed = typographicBounds(ControlText.withoutMarks(line), font: font)
            above = max(above, ConfirmationDialog.extraRoom(needed: needed.ascent, own: own.ascent, lineHeight: lineHeight))
            below = max(below, ConfirmationDialog.extraRoom(needed: needed.descent, own: own.descent, lineHeight: lineHeight))
        }
        let rowHeight = lineHeight + above + below
        let lineWidth = ceil(fields.map(\.intrinsicContentSize.width).max() ?? 0)
        let document = CodeDocument(frame: NSRect(x: 0, y: 0, width: lineWidth + 2 * codeInset.width,
                                                  height: CGFloat(fields.count) * rowHeight + 2 * codeInset.height))
        document.lines = lines
        for (index, field) in fields.enumerated() {
            let box = FlippedView(frame: NSRect(x: codeInset.width, y: codeInset.height + CGFloat(index) * rowHeight,
                                                width: lineWidth, height: rowHeight))
            box.clipsToBounds = true
            // The label draws past its own frame, into the room above and below it; its box is the clip.
            field.frame = NSRect(x: 0, y: above, width: lineWidth, height: lineHeight)
            field.clipsToBounds = false
            field.delegate = lines
            field.lines = lines
            box.addSubview(field)
            document.addSubview(box)
        }
        lines.fields = fields
        fields.first?.isKeyStop = true
        scroll.documentView = document
        // With scroll bars always shown, the sideways one takes its height from the lines, so the block
        // makes room for it rather than hide the last line under it; an overlay scroller takes none.
        let scroller = NSScroller.preferredScrollerStyle == .legacy
            ? NSScroller.scrollerWidth(for: .regular, scrollerStyle: .legacy) : 0
        scroll.translatesAutoresizingMaskIntoConstraints = false
        scroll.heightAnchor.constraint(equalToConstant: min(codeHeight, document.frame.height + 4 + scroller)).isActive = true
        return scroll
    }

    /// How far `text` reaches above and below its baseline in `font` and whatever fallback faces draw it: the
    /// faces' own ascent and descent, not the ink.
    static func typographicBounds(_ text: String, font: NSFont) -> (ascent: CGFloat, descent: CGFloat) {
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: [.font: font]))
        var ascent: CGFloat = 0, descent: CGFloat = 0, leading: CGFloat = 0
        _ = CTLineGetTypographicBounds(line, &ascent, &descent, &leading)
        return (ascent, descent)
    }

    /// The room between the block's border and its lines.
    static let codeInset = NSSize(width: 4, height: 6)

    /// One line of what would run: a selectable label, never wrapped, laid out left to right, that takes the
    /// keyboard when it is the block's stop.
    static func codeLine(_ line: String, font: NSFont, color: NSColor) -> CodeLineField {
        let field = CodeLineField(labelWithString: line)
        field.font = font
        field.textColor = color
        field.isSelectable = true
        field.isEditable = false
        field.maximumNumberOfLines = 1
        field.lineBreakMode = .byClipping
        field.cell?.wraps = false
        field.baseWritingDirection = .leftToRight
        field.alignment = .left
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
class FlippedView: NSView {
    override var isFlipped: Bool { true }
}

/// The code block's document: its lines' boxes, and what moves the keyboard between the lines, which lives
/// as long as they do.
final class CodeDocument: FlippedView {
    var lines: CodeLines?
}

/// One line of the code block. A selectable label refuses the keyboard; a line takes it, so the block can
/// be reached and scrolled from the keyboard. Only one line is in the key-view loop, the first until another
/// has had the keyboard, then that one, so the block is one stop for Tab.
final class CodeLineField: NSTextField {
    /// Whether this line is the block's stop for Tab.
    var isKeyStop = false
    weak var lines: CodeLines?

    override var acceptsFirstResponder: Bool { true }
    override var canBecomeKeyView: Bool { isKeyStop && !isHiddenOrHasHiddenAncestor }

    override func becomeFirstResponder() -> Bool {
        let became = super.becomeFirstResponder()
        if became { lines?.focused(self) }
        return became
    }
}

/// The keyboard in the code block: Up and Down move it to the line above or below, Page Up and Page Down a
/// block's height of lines, each scrolling the line it reaches into view; Left, Right, Home and End are the
/// line's own. The line the keyboard was last on is the block's one stop for Tab.
@MainActor
final class CodeLines: NSObject, NSTextFieldDelegate {
    var fields: [CodeLineField] = []

    func focused(_ field: CodeLineField) {
        for line in fields { line.isKeyStop = line === field }
        if let box = field.superview { box.scrollToVisible(box.bounds) }
    }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
        guard let field = control as? CodeLineField, let index = fields.firstIndex(where: { $0 === field }) else { return false }
        let page = max(1, Int((field.enclosingScrollView?.contentView.bounds.height ?? 0) / max(1, field.superview?.frame.height ?? 1)) - 1)
        let step: Int
        switch selector {
        case #selector(NSResponder.moveUp(_:)): step = -1
        case #selector(NSResponder.moveDown(_:)): step = 1
        case #selector(NSResponder.pageUp(_:)), #selector(NSResponder.scrollPageUp(_:)): step = -page
        case #selector(NSResponder.pageDown(_:)), #selector(NSResponder.scrollPageDown(_:)): step = page
        default: return false
        }
        let target = fields[max(0, min(fields.count - 1, index + step))]
        if target === field {
            focused(field)
        } else {
            field.window?.makeFirstResponder(target)
        }
        return true
    }
}
