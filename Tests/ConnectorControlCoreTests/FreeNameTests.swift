import XCTest
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/FreeNameTests.cs
final class FreeNameTests: XCTestCase {
    func testAFreeNameIsKeptAsItIs() {
        XCTAssertEqual(FreeName.first("Docs", isTaken: { _ in false }), "Docs")
    }
    func testATakenNameCountsUpFromTwoWithASpace() {
        let taken: Set<String> = ["Docs", "Docs 2", "Docs 3"]
        XCTAssertEqual(FreeName.first("Docs", isTaken: taken.contains), "Docs 4")
    }
    func testAGapIsFilledFirst() {
        let taken: Set<String> = ["Docs", "Docs 3"]
        XCTAssertEqual(FreeName.first("Docs", isTaken: taken.contains), "Docs 2")
    }
}
