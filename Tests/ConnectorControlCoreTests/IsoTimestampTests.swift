import XCTest
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/IsoTimestampTests.cs
final class IsoTimestampTests: XCTestCase {
    func testAnInstantIsWrittenAsISO8601UTCToTheSecond() {
        XCTAssertEqual(IsoTimestamp.string(from: Date(timeIntervalSince1970: 1_789_999_331)), "2026-09-21T14:02:11Z")
        // Every field is zero-padded, and a fractional second is dropped rather than rounded up.
        XCTAssertEqual(IsoTimestamp.string(from: Date(timeIntervalSince1970: 1_767_225_605.75)), "2026-01-01T00:00:05Z")
    }

    func testALocalCalendarDateIsTheDayThisMachineIsLivingThrough() {
        // Cross-checked against the UTC formatter shifted by this machine's own offset, so the
        // assertion holds in every zone rather than only the one that wrote it.
        let instant = Date(timeIntervalSince1970: 1_789_999_331)
        let shifted = instant.addingTimeInterval(TimeInterval(TimeZone.current.secondsFromGMT(for: instant)))
        XCTAssertEqual(IsoTimestamp.localDate(from: instant), String(IsoTimestamp.string(from: shifted).prefix(10)))
        // Zero-padded to ten characters whatever the zone does to the day.
        XCTAssertEqual(IsoTimestamp.localDate(from: Date(timeIntervalSince1970: 1_767_225_605)).count, 10)
    }
}
