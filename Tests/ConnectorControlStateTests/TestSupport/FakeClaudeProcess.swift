import Foundation
@testable import ConnectorControlState

final class FakeClaudeProcess: ClaudeProcess {
    var isRunning = false
    var launchDate: Date?
    var restartResult: String?
    private(set) var restartCalls = 0
    /// Runs inside restart(completion:) so a test can simulate the relaunch (new launchDate).
    var onRestart: (() -> Void)?

    func restart(completion: @escaping @Sendable (String?) -> Void) {
        restartCalls += 1
        onRestart?()
        completion(restartResult)
    }
}
