import XCTest
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlTextTests.cs
final class ControlTextTests: XCTestCase {
    func testLabelledLinesLineUpTheirValues() {
        XCTAssertEqual(ControlText.labelled([("Active collection", "Harvard"), ("Claude integration", "on"), ("", "more")]), [
            "Active collection    Harvard",
            "Claude integration   on",
            "                     more",
        ])
    }

    func testATablePadsEveryColumnButTheLast() {
        XCTAssertEqual(ControlText.table(header: ["", "NAME", "RUNS"], rows: [["on", "aws-mcp", "aws-mcp.us-east-1.api.aws"],
                                                                              ["off", "café", "npx x"]]), [
            "      NAME      RUNS",
            "on    aws-mcp   aws-mcp.us-east-1.api.aws",
            "off   café      npx x",
        ])
    }

    /// A character outside the Basic Multilingual Plane is one scalar here and two UTF-16 units on
    /// Windows, where widths count runes: both count it once, so both pad alike.
    func testACharacterBeyondTheBasicPlaneIsOneColumnWide() {
        XCTAssertEqual(ControlText.width("a😀b"), 3)
        XCTAssertEqual(ControlText.labelled([("😀", "x"), ("ab", "y")]), ["😀    x", "ab   y"])
        XCTAssertEqual(ControlText.table(header: nil, rows: [["𝔸𝔹", "1"], ["abc", "2"]]), ["𝔸𝔹    1", "abc   2"])
    }

    func testNamesAreListedTheWayTheAppsSentencesListThem() {
        XCTAssertEqual(ControlText.quotedList(["a"]), "“a”")
        XCTAssertEqual(ControlText.quotedList(["a", "b"]), "“a” and “b”")
        XCTAssertEqual(ControlText.quotedList(["a", "b", "c"]), "“a”, “b” and “c”")
    }
}
