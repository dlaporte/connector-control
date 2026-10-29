import AppKit
import CoreText
import XCTest
import ConnectorControlState
@testable import ConnectorControl

/// Mac only: the Windows counterpart is windows/tests/ConnectorControl.App.Tests/ConfirmationDialogTests.cs.
///
/// How the confirmation's code block lays out what would run, measured on the real AppKit views.
@MainActor
final class ConfirmationAccessoryTests: XCTestCase {
    private struct Block {
        let scroll: NSScrollView
        /// Each line's clipped box, top to bottom.
        let boxes: [NSView]
        /// Each line's label, inside its box.
        let fields: [NSTextField]
    }

    private func block(_ code: [String]) -> Block {
        let dialog = ConfirmationDialog(ConfirmationRequest(title: "t", message: "", code: code, footer: nil))
        guard let scroll = ConfirmationAccessory.codeBlock(dialog) as? NSScrollView else {
            XCTFail("the code block scrolls")
            return Block(scroll: NSScrollView(), boxes: [], fields: [])
        }
        let boxes = scroll.documentView?.subviews ?? []
        return Block(scroll: scroll, boxes: boxes, fields: boxes.compactMap { $0.subviews.first as? NSTextField })
    }

    /// Each line of what would run is drawn in a box of its own, clipped to it, and the boxes touch without
    /// overlapping, so no mark stacked on a line, however tall, can draw over the line above or below it.
    func testEachCodeLineIsClippedToItsOwnLine() {
        let code = ["url", "  − https://a.example/mcp", "  + l\u{1DD8}\u{1DD8}", "  + g\u{034D}\u{034D}", "--ports \u{0627} 80"]
        let block = block(code)
        XCTAssertEqual(block.fields.map(\.stringValue), ConfirmationDialog(ConfirmationRequest(title: "t", message: "", code: code,
                                                                                                footer: nil)).shownCode)
        guard block.boxes.count == code.count, block.fields.count == code.count, let first = block.boxes.first else {
            return XCTFail("a box and a label a line")
        }
        let height = first.frame.height
        XCTAssertGreaterThan(height, 0)
        for (index, box) in block.boxes.enumerated() {
            XCTAssertTrue(box.clipsToBounds, "line \(index) is clipped to its own box")
            XCTAssertEqual(box.frame.height, height, "every line's box is as tall as every other")
            XCTAssertEqual(box.frame.minY, first.frame.minY + CGFloat(index) * height, "the boxes touch and never overlap")
            let field = block.fields[index]
            XCTAssertTrue(field.isSelectable, "a line can still be selected and copied")
            XCTAssertFalse(field.isEditable)
            XCTAssertEqual(field.maximumNumberOfLines, 1)
            XCTAssertEqual(field.lineBreakMode, .byClipping, "a line never wraps")
            XCTAssertEqual(field.baseWritingDirection, .leftToRight)
            XCTAssertTrue(box.bounds.contains(field.frame), "the label sits inside its box")
        }
        XCTAssertEqual(block.fields[1].textColor, .systemRed)
        XCTAssertEqual(block.fields[2].textColor, .systemGreen)
        XCTAssertEqual(block.fields[0].textColor, .labelColor)
    }

    /// A line's box is as tall as the code face needs, and grows only for the letters of another script drawn
    /// in a taller fallback face: never for marks, and never past half a line more above and below.
    func testALineBoxGrowsForTallerScriptsButNotForMarks() {
        let plain = block(["url", "plain"]).boxes.first?.frame.height ?? 0
        XCTAssertGreaterThan(plain, 0)
        XCTAssertEqual(block(["url", "  + l\u{1DD8}\u{1DD8}", "  + g\u{034D}\u{034D}"]).boxes.first?.frame.height, plain,
                       "marks never make a line taller")
        let tall = block(["  + \u{0939}\u{093F}\u{0928}\u{094D}\u{0926}\u{0940}"]).boxes.first?.frame.height ?? 0
        XCTAssertGreaterThan(tall, plain, "Devanagari's fallback face needs more room than the code face")
        XCTAssertLessThanOrEqual(tall, plain + 2 * (plain / 2).rounded(.up), "bounded")
    }

    /// Ordinary text in scripts the code face does not have — Chinese, Arabic, Devanagari, Thai — is drawn whole:
    /// its ink stays clear of its box's edges. A line of stacked marks is cut off by its own box, and the line
    /// beside it stays blank.
    func testOrdinaryTextInOtherScriptsIsNotClippedAndMarksNeverReachTheNextLine() throws {
        let scripts = ["  + \u{6F22}\u{5B57}", "  + \u{0627}\u{0644}\u{0639}\u{0631}\u{0628}\u{064A}\u{0629}",
                       "  + \u{0939}\u{093F}\u{0928}\u{094D}\u{0926}\u{0940}", "  + \u{0E17}\u{0E35}\u{0E48}"]
        let stack = "  + l\u{1DD8}\u{1DD8} g\u{034D}\u{034D}"
        let block = block(scripts + [stack, " ", stack])
        guard let document = block.scroll.documentView, block.boxes.count == 7 else { return XCTFail("a box a line") }
        let ink = try inkRows(document)
        for (index, box) in block.boxes.prefix(4).enumerated() {
            let rows = ink.filter { box.frame.minY..<box.frame.maxY ~= $0 }
            XCTAssertFalse(rows.isEmpty, "line \(index) is drawn")
            XCTAssertGreaterThan(rows.min() ?? 0, box.frame.minY, "line \(index)'s ink clears its box's top")
            XCTAssertLessThan(rows.max() ?? .infinity, box.frame.maxY - 1, "line \(index)'s ink clears its box's bottom")
            // All of the line's ink is there: as tall as the faces draw it, not cut to the label's own frame.
            let field = block.fields[index]
            let ink = CTLineGetImageBounds(CTLineCreateWithAttributedString(NSAttributedString(string: field.stringValue,
                                                                                               attributes: [.font: field.font!])), nil)
            XCTAssertGreaterThanOrEqual((rows.max() ?? 0) - (rows.min() ?? 0) + 1, ink.height - 1, "line \(index) is drawn whole")
        }
        let blank = block.boxes[5].frame
        XCTAssertTrue(ink.filter { blank.minY..<blank.maxY ~= $0 }.isEmpty, "no mark reaches the line between two stacks")
    }

    /// The rows of `view`, in its own flipped points, that hold any ink.
    private func inkRows(_ view: NSView) throws -> [CGFloat] {
        let rep = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))
        view.cacheDisplay(in: view.bounds, to: rep)
        let scale = CGFloat(rep.pixelsHigh) / view.bounds.height
        var rows: [CGFloat] = []
        for y in 0..<rep.pixelsHigh {
            if (0..<rep.pixelsWide).contains(where: { (rep.colorAt(x: $0, y: y)?.alphaComponent ?? 0) > 0.05 }) {
                rows.append(CGFloat(y) / scale)
            }
        }
        return rows
    }

    /// A line far wider than the dialog is one line, and the block scrolls sideways to it.
    func testAWideLineScrollsSideways() {
        let wide = String(repeating: "--flag=value ", count: 300)
        let block = block(["command  npx", wide])
        XCTAssertTrue(block.scroll.hasHorizontalScroller)
        guard block.fields.count == 2 else { return XCTFail("one label a line") }
        XCTAssertGreaterThan(block.fields[1].frame.width, ConfirmationAccessory.width * 4, "the wide line is laid out whole")
        XCTAssertGreaterThanOrEqual(block.scroll.documentView?.frame.width ?? 0, block.boxes[1].frame.maxX)
    }

    // MARK: The keyboard

    /// The block in a window with a button before it, as the alert holds it, and the key-view loop worked out.
    private func windowed(_ code: [String]) -> (NSWindow, NSButton, Block) {
        let block = block(code)
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 420, height: 300), styleMask: [.titled], backing: .buffered, defer: true)
        let button = NSButton(title: "Don’t Allow", target: nil, action: nil)
        let stack = NSStackView(views: [button, block.scroll])
        stack.orientation = .vertical
        window.contentView = stack
        block.scroll.widthAnchor.constraint(equalToConstant: ConfirmationAccessory.width).isActive = true
        window.layoutIfNeeded()
        window.recalculateKeyViewLoop()
        return (window, button, block)
    }

    /// The label whose text the window's keyboard is on, if any: a focused label hands it to the field editor.
    private func focused(_ window: NSWindow) -> NSTextField? {
        (window.firstResponder as? NSTextView)?.delegate as? NSTextField
    }

    /// The block is one stop for Tab, as one text view was: one line is in the key-view loop, and it takes the
    /// keyboard, so a keyboard user can reach the lines and scroll them.
    func testTheBlockIsOneStopForTab() {
        let (window, button, block) = windowed(["url", "  − a", "  + b"])
        var loop: [NSView] = []
        var view: NSView? = button
        repeat {
            if let view { loop.append(view) }
            view = view?.nextValidKeyView
        } while view != nil && view !== button && loop.count < 20
        XCTAssertEqual(loop.filter { block.fields.contains($0 as? NSTextField ?? NSTextField()) }.count, 1, "one stop for the block")
        XCTAssertTrue(loop.contains(block.fields[0]), "the first line is the stop until another has had the keyboard")
        XCTAssertTrue(block.fields.allSatisfy(\.acceptsFirstResponder))
        XCTAssertTrue(window.makeFirstResponder(block.fields[0]))
        XCTAssertTrue(focused(window) === block.fields[0])
    }

    /// Up and Down move the keyboard to the line above or below and scroll it into view; Page Up and Page Down
    /// move it a block's height of lines. The line the keyboard was last on is the block's stop for Tab.
    func testUpAndDownMoveBetweenLinesAndScrollThemIntoView() throws {
        let code = (0..<40).map { "line \($0)" }
        let (window, _, block) = windowed(code)
        XCTAssertTrue(window.makeFirstResponder(block.fields[0]))
        let editor = try XCTUnwrap(window.firstResponder as? NSTextView)
        editor.doCommand(by: #selector(NSResponder.moveDown(_:)))
        XCTAssertTrue(focused(window) === block.fields[1])
        try XCTUnwrap(window.firstResponder as? NSTextView).doCommand(by: #selector(NSResponder.moveUp(_:)))
        XCTAssertTrue(focused(window) === block.fields[0])
        try XCTUnwrap(window.firstResponder as? NSTextView).doCommand(by: #selector(NSResponder.moveUp(_:)))
        XCTAssertTrue(focused(window) === block.fields[0], "the first line stays put")
        try XCTUnwrap(window.firstResponder as? NSTextView).doCommand(by: #selector(NSResponder.pageDown(_:)))
        let paged = try XCTUnwrap(focused(window))
        let index = try XCTUnwrap(block.fields.firstIndex { $0 === paged })
        XCTAssertGreaterThan(index, 1, "a page is more than a line")
        let visible = block.scroll.contentView.documentVisibleRect
        XCTAssertTrue(visible.contains(block.boxes[index].frame), "the line the keyboard moved to is scrolled into view")
        XCTAssertTrue(block.fields[index].canBecomeKeyView)
        XCTAssertFalse(block.fields[0].canBecomeKeyView, "the last line focused is the block's one stop")
        for _ in 0..<60 { try XCTUnwrap(window.firstResponder as? NSTextView).doCommand(by: #selector(NSResponder.moveDown(_:))) }
        XCTAssertTrue(focused(window) === block.fields.last, "the last line stays put")
        XCTAssertTrue(block.scroll.contentView.documentVisibleRect.contains(block.boxes[39].frame))
    }
}
