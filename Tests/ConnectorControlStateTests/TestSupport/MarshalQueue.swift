import Foundation
@testable import ConnectorControlState

/// A stand-in for DispatchQueue.main: closures posted from the watchers' queue,
/// the probe's queue or a restart completion are held until the test pumps,
/// so the test thread stays the single owner of AppState exactly like the
/// main actor does in the app.
final class MarshalQueue: @unchecked Sendable {
    private let lock = NSLock()
    private var queue: [MainActorAction] = []

    var pending: Int { lock.withLock { queue.count } }

    func post(_ action: @escaping MainActorAction) {
        lock.withLock { queue.append(action) }
    }

    /// Runs everything queued so far (and anything they queue); returns how many ran.
    @MainActor
    @discardableResult
    func pump() -> Int {
        var ran = 0
        while let action = lock.withLock({ queue.isEmpty ? nil : queue.removeFirst() }) {
            action()
            ran += 1
        }
        return ran
    }

    /// Pumps until the condition holds or the timeout passes.
    @MainActor
    func pumpUntil(_ condition: () -> Bool, timeout: TimeInterval) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while true {
            pump()
            if condition() { return true }
            if Date() >= deadline { return false }
            Thread.sleep(forTimeInterval: 0.05)
        }
    }
}
