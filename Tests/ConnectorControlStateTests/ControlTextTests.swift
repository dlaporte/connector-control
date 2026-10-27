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

    func testNamesAreListedTheWayTheAppsSentencesListThem() {
        XCTAssertEqual(ControlText.quotedList(["a"]), "“a”")
        XCTAssertEqual(ControlText.quotedList(["a", "b"]), "“a” and “b”")
        XCTAssertEqual(ControlText.quotedList(["a", "b", "c"]), "“a”, “b” and “c”")
    }
}
