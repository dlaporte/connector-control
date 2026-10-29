import XCTest
@testable import ConnectorControlTransport

/// Mirror: windows/tests/ConnectorControl.Core.Tests/Transport/ControlClientTests.cs
final class ControlClientTests: XCTestCase {
    final class NullStream: ControlStream, @unchecked Sendable {
        func read(maximum: Int) -> Data { Data() }
        func write(_ data: Data) -> Bool { true }
        func close() {}
        func closeWrite() {}
    }

    /// What the client's closures count and the clock they read: a class, since the client's closures
    /// are @Sendable and may not change a captured variable.
    final class Seen: @unchecked Sendable {
        /// Seconds on a monotonic clock, which the client reads as `now`.
        var clock: TimeInterval = 0
        var attempts = 0
        var launches = 0
        var announced = 0
    }

    func testAnAppThatAnswersIsUsedAsItIs() throws {
        let seen = Seen()
        let client = ControlClient(connectOnce: { NullStream() }, launch: { seen.launches += 1; return true },
                                   announce: { XCTFail("nothing to announce") })
        _ = try client.connect()
        XCTAssertEqual(seen.launches, 0)
    }

    func testNothingAnsweringWhereStartingIsNotAllowedIsNotRunning() {
        let client = ControlClient(connectOnce: { nil }, launch: nil, announce: { XCTFail("nothing is started") })
        XCTAssertThrowsError(try client.connect()) { XCTAssertEqual($0 as? ControlClient.Failure, .notRunning) }
        XCTAssertEqual(ControlClient.Failure.notRunning.message, ControlClient.notRunningError)
    }

    func testTheAppIsStartedOnceAnnouncedOnceAndWaitedFor() throws {
        let seen = Seen()
        let client = ControlClient(connectOnce: { seen.attempts += 1; return seen.attempts == 4 ? NullStream() : nil },
                                   launch: { seen.launches += 1; return true }, announce: { seen.announced += 1 },
                                   sleep: { seen.clock += $0 }, now: { seen.clock })
        _ = try client.connect()
        XCTAssertEqual([seen.attempts, seen.launches, seen.announced], [4, 1, 1])
    }

    func testAnAppThatDoesNotComeUpIn15SecondsIsUnreachable() {
        let seen = Seen()
        let client = ControlClient(connectOnce: { nil }, launch: { true }, announce: nil,
                                   sleep: { seen.clock += $0 }, now: { seen.clock })
        XCTAssertThrowsError(try client.connect()) { XCTAssertEqual($0 as? ControlClient.Failure, .couldNotStart) }
        XCTAssertEqual(seen.clock, ControlClient.startTimeout, accuracy: ControlClient.pollInterval * 1.5)
        XCTAssertEqual(ControlClient.Failure.couldNotStart.message, ControlClient.unreachableError)
    }

    func testAnAppThatCannotBeStartedIsReportedAtOnce() {
        let seen = Seen()
        let client = ControlClient(connectOnce: { nil }, launch: { false }, announce: { seen.announced += 1 },
                                   sleep: { seen.clock += $0 }, now: { seen.clock })
        XCTAssertThrowsError(try client.connect()) { XCTAssertEqual($0 as? ControlClient.Failure, .couldNotStart) }
        XCTAssertEqual(seen.clock, 0, "no wait for an app that did not start")
        XCTAssertEqual(seen.announced, 0, "nothing was started, so nothing is announced")
    }

    /// An elevated tool starts nothing, whatever `launch` allows: the app would run elevated too, out of
    /// reach of everything the person runs as themselves. It says why, never that it is starting the app.
    func testAnElevatedToolStartsNothingAndSaysWhy() {
        let seen = Seen()
        let client = ControlClient(connectOnce: { seen.attempts += 1; return nil }, launch: { seen.launches += 1; return true },
                                   announce: { seen.announced += 1 }, elevated: true)
        XCTAssertThrowsError(try client.connect()) { XCTAssertEqual($0 as? ControlClient.Failure, .elevated) }
        XCTAssertEqual([seen.attempts, seen.launches, seen.announced], [1, 0, 0])
        XCTAssertEqual(ControlClient.Failure.elevated.message, ControlClient.elevatedError)
    }

    func testAnElevatedToolStillUsesAnAppThatAnswers() throws {
        let client = ControlClient(connectOnce: { NullStream() }, launch: nil, announce: nil, elevated: true)
        _ = try client.connect()
    }
}
