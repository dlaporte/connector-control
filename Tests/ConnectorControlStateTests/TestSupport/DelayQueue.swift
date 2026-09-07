import Foundation
@testable import ConnectorControlState

/// Captures AppHost.delay calls so tests decide when "later" happens.
@MainActor
final class DelayQueue {
    private(set) var pending: [(delay: TimeInterval, action: MainActorAction)] = []

    nonisolated func add(_ delay: TimeInterval, _ action: @escaping MainActorAction) {
        MainActor.assumeIsolated { pending.append((delay, action)) }
    }

    /// Runs the oldest pending action (a periodic action may re-add itself).
    func runNext() {
        let next = pending.removeFirst()
        next.action()
    }
}
