import Foundation

/// Mirror: windows/src/ConnectorControl.Core/Transport/ControlClient.cs
///
/// The tool's way to the app: connect, and when nothing answers — and starting is allowed — start the
/// app in the background and wait for it, up to `startTimeout`.
public final class ControlClient: @unchecked Sendable {
    public static let startingText = "Starting Connector Control…"
    public static let notRunningError = "Connector Control isn’t running."
    public static let unreachableError = "Connector Control isn’t running and couldn’t be started."
    public static let droppedError = "Connector Control closed the connection before answering."
    public static let startTimeout: TimeInterval = 15
    public static let pollInterval: TimeInterval = 0.1
    /// The most a line from the app may hold: far past any answer it gives, short of a stream that
    /// never ends a line.
    public static let maxAnswerBytes = 64 * 1_048_576

    public enum Failure: Error, Equatable, Sendable {
        /// Nothing answered, and starting the app is not allowed.
        case notRunning
        /// Nothing answered, and starting the app did not bring it up in time.
        case couldNotStart

        public var message: String { self == .notRunning ? ControlClient.notRunningError : ControlClient.unreachableError }
    }

    private let connectOnce: @Sendable () -> ControlStream?
    private let launch: (@Sendable () -> Bool)?
    private let announce: (@Sendable () -> Void)?
    private let sleep: @Sendable (TimeInterval) -> Void
    private let now: @Sendable () -> TimeInterval

    /// `connectOnce` tries the endpoint once; `launch` starts the app, nil where starting is not
    /// allowed; `announce` says it is being started — the command line prints `startingText`, the
    /// relay says nothing on Claude's stream. All are @Sendable: the relay calls them on its own
    /// queue, not where they were made. `now` reads a monotonic clock, in seconds, so a change to the
    /// clock cannot stretch the wait.
    public init(connectOnce: @escaping @Sendable () -> ControlStream?, launch: (@Sendable () -> Bool)?,
                announce: (@Sendable () -> Void)?,
                sleep: @escaping @Sendable (TimeInterval) -> Void = { Thread.sleep(forTimeInterval: $0) },
                now: @escaping @Sendable () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.connectOnce = connectOnce
        self.launch = launch
        self.announce = announce
        self.sleep = sleep
        self.now = now
    }

    /// A connection to the app; throws `Failure` when there is none to be had.
    public func connect() throws -> ControlStream {
        if let stream = connectOnce() { return stream }
        guard let launch else { throw Failure.notRunning }
        // Said only once something has been started: a tool that starts nothing says only that it could not.
        guard launch() else { throw Failure.couldNotStart }
        announce?()
        let deadline = now() + ControlClient.startTimeout
        while now() < deadline {
            sleep(ControlClient.pollInterval)
            if let stream = connectOnce() { return stream }
        }
        throw Failure.couldNotStart
    }
}
