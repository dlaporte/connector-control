import Foundation

/// What a thread a test started sets, read on the test's own thread.
final class Box<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: Value?

    var value: Value? { lock.withLock { stored } }

    func set(_ value: Value) { lock.withLock { stored = value } }
}
