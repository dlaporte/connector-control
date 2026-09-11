import Foundation

/// NSRunningApplication + ClaudeRestarter as a seam.
@MainActor
public protocol ClaudeProcess: AnyObject {
    var isRunning: Bool { get }
    /// The running Claude's launch date, or nil when not running / unknown.
    var launchDate: Date? { get }
    /// Gracefully quit Claude (never force-kill), wait up to 15 s, relaunch.
    /// Calls `completion` exactly once, on any thread, with nil on success or
    /// the user-facing error message. Callers marshal the completion themselves.
    func restart(completion: @escaping @Sendable (String?) -> Void)
}
