import Foundation

/// A closure that must run on the main actor.
public typealias MainActorAction = @MainActor () -> Void

/// The main thread as three closures (port design §7.6): `marshal` POSTS a
/// closure to it and never blocks (the FileWatchers call it from their own
/// queue, the tool probe from a global queue, the restart completion from
/// wherever it lands), `delay` schedules one there later, `now` is the clock.
/// The app uses `live()`; tests hand in a `MarshalQueue`, a `DelayQueue` and
/// a fixed date, and decide themselves when "later" and "now" are.
public struct AppHost: Sendable {
    public let marshal: @Sendable (@escaping MainActorAction) -> Void
    public let delay: @Sendable (TimeInterval, @escaping MainActorAction) -> Void
    public let now: @Sendable () -> Date

    public init(marshal: @escaping @Sendable (@escaping MainActorAction) -> Void,
                delay: @escaping @Sendable (TimeInterval, @escaping MainActorAction) -> Void,
                now: @escaping @Sendable () -> Date) {
        self.marshal = marshal
        self.delay = delay
        self.now = now
    }

    /// DispatchQueue.main for both posts — the one place the main actor is
    /// entered from outside it in the state layer.
    public static func live() -> AppHost {
        AppHost(
            marshal: { work in
                DispatchQueue.main.async { MainActor.assumeIsolated(work) }
            },
            delay: { seconds, work in
                DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { MainActor.assumeIsolated(work) }
            },
            now: { Date() })
    }
}
