import Foundation
@testable import ConnectorControlState

/// Captures AppHost.background work so the test runs it, on its own thread,
/// when it chooses: a tool probe then never waits for a global-queue thread.
/// XCTest runs these tests one at a time, so nothing starves one here; the
/// seam is the C# harness's (where the parallel suite parks pool threads in
/// sleeps, the Windows CI flakes), kept in lock step. A queue rather than
/// inline work, so a test can still tell that the work was handed off and not
/// run on the caller's thread.
final class BackgroundQueue: @unchecked Sendable {
    private let lock = NSLock()
    private var queue: [@Sendable () -> Void] = []

    var pending: Int { lock.withLock { queue.count } }

    func add(_ work: @escaping @Sendable () -> Void) {
        lock.withLock { queue.append(work) }
    }

    /// Runs everything queued so far (and anything that queues); returns how many ran.
    @discardableResult
    func runAll() -> Int {
        var ran = 0
        while let work = lock.withLock({ queue.isEmpty ? nil : queue.removeFirst() }) {
            work()
            ran += 1
        }
        return ran
    }
}
