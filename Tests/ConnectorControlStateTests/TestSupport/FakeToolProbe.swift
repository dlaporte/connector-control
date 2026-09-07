import Foundation
import ConnectorControlCore
@testable import ConnectorControlState

/// A machine with everything installed (`/fake/bin/<tool>`, version 1.0.0)
/// unless a test sets `statuses`. AppState calls `probe` from a global queue,
/// so every member is behind one lock.
final class FakeToolProbe: ToolProbing, @unchecked Sendable {
    private let lock = NSLock()
    private var storedStatuses: [Tool: ToolStatus] = [:]
    private var storedProbed: [Tool] = []
    private var storedBatches = 0

    var statuses: [Tool: ToolStatus] {
        get { lock.withLock { storedStatuses } }
        set { lock.withLock { storedStatuses = newValue } }
    }

    var probed: [Tool] { lock.withLock { storedProbed } }

    var batches: Int { lock.withLock { storedBatches } }

    func probe(_ tools: [Tool]) -> [Tool: ToolStatus] {
        lock.withLock {
            storedBatches += 1
            storedProbed.append(contentsOf: tools)
            var results: [Tool: ToolStatus] = [:]
            for tool in tools {
                results[tool] = storedStatuses[tool] ?? .found(path: "/fake/bin/\(tool.name)", version: "1.0.0")
            }
            return results
        }
    }
}
