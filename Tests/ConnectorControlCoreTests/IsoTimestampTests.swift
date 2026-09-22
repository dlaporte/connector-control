import XCTest
@testable import ConnectorControlCore

/// windows/tests/ConnectorControl.Core.Tests/IsoTimestampTests.cs
final class IsoTimestampTests: XCTestCase {
    func testAnInstantIsWrittenAsISO8601UTCToTheSecond() {
        XCTAssertEqual(IsoTimestamp.string(from: Date(timeIntervalSince1970: 1_789_999_331)), "2026-09-21T14:02:11Z")
        // Every field is zero-padded, and a fractional second is dropped rather than rounded up.
        XCTAssertEqual(IsoTimestamp.string(from: Date(timeIntervalSince1970: 1_767_225_605.75)), "2026-01-01T00:00:05Z")
    }
}
