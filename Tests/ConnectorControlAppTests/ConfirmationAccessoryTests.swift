import AppKit
import XCTest
import ConnectorControlState
@testable import ConnectorControl

/// Mac only: the Windows counterpart is windows/tests/ConnectorControl.App.Tests/ConfirmationDialogTests.cs.
///
/// How the confirmation's code block lays out what would run, measured on the real AppKit views.
@MainActor
final class ConfirmationAccessoryTests: XCTestCase {
    private func block(_ code: [String]) -> (NSScrollView, [NSTextField]) {
        let dialog = ConfirmationDialog(ConfirmationRequest(title: "t", message: "", code: code, footer: nil))
        guard let scroll = ConfirmationAccessory.codeBlock(dialog) as? NSScrollView else {
            XCTFail("the code block scrolls")
            return (NSScrollView(), [])
        }
        let fields = (scroll.documentView?.subviews ?? []).compactMap { $0 as? NSTextField }
        return (scroll, fields)
    }

    /// Each line of what would run is drawn in a view of its own, one line tall and clipped to it, so no mark
    /// stacked on a line, however tall, can draw over the line above or below it.
    func testEachCodeLineIsClippedToItsOwnLine() {
        let code = ["url", "  − https://a.example/mcp", "  + l\u{1DD8}\u{1DD8}", "  + g\u{034D}\u{034D}", "--ports \u{0627} 80"]
        let (_, fields) = block(code)
        XCTAssertEqual(fields.map(\.stringValue), ConfirmationDialog(ConfirmationRequest(title: "t", message: "", code: code,
                                                                                          footer: nil)).shownCode)
        guard let first = fields.first else { return XCTFail("one view a line") }
        let height = first.frame.height
        XCTAssertEqual(height, ceil(ConfirmationAccessory.codeLine("Xg", font: first.font!, color: .labelColor).intrinsicContentSize.height),
                       "a line is as tall as the font makes a line, whatever marks it holds")
        for (index, field) in fields.enumerated() {
            XCTAssertTrue(field.clipsToBounds, "line \(index) is clipped to its own box")
            XCTAssertEqual(field.frame.height, height, "every line is one line tall")
            XCTAssertEqual(field.frame.minY, first.frame.minY + CGFloat(index) * height, "the lines touch and never overlap")
            XCTAssertTrue(field.isSelectable, "a line can still be selected and copied")
            XCTAssertFalse(field.isEditable)
            XCTAssertEqual(field.maximumNumberOfLines, 1)
            XCTAssertEqual(field.lineBreakMode, .byClipping, "a line never wraps")
            XCTAssertEqual(field.baseWritingDirection, .leftToRight)
        }
        guard fields.count == code.count else { return XCTFail("one view a line") }
        XCTAssertEqual(fields[1].textColor, .systemRed)
        XCTAssertEqual(fields[2].textColor, .systemGreen)
        XCTAssertEqual(fields[0].textColor, .labelColor)
    }

    /// A line far wider than the dialog is one line, and the block scrolls sideways to it.
    func testAWideLineScrollsSideways() {
        let wide = String(repeating: "--flag=value ", count: 300)
        let (scroll, fields) = block(["command  npx", wide])
        XCTAssertTrue(scroll.hasHorizontalScroller)
        guard fields.count == 2 else { return XCTFail("one view a line") }
        XCTAssertGreaterThan(fields[1].frame.width, ConfirmationAccessory.width * 4, "the wide line is laid out whole")
        XCTAssertGreaterThanOrEqual(scroll.documentView?.frame.width ?? 0, fields[1].frame.maxX)
        XCTAssertEqual(fields[0].frame.height, fields[1].frame.height)
    }
}
