import Foundation

/// A closure that must run on the main actor.
public typealias MainActorAction = @MainActor () -> Void

/// The main thread as four closures: `marshal` POSTS a
/// closure to it and never blocks (the FileWatchers call it from their own
/// queue, the tool probe from its background work, the restart completion
/// from wherever it lands), `delay` schedules one there later, `now` is the
/// clock, and `background` runs work off it (a global queue in the app).
/// The app uses `live()`; tests hand in a `MarshalQueue`, a `DelayQueue`, a
/// fixed date and a `BackgroundQueue`, and decide themselves when "later",
/// "now" and "in the background" are.
///
/// `marshal` must POST, never run its closure inline: `FileWatcher` reads its
/// own state with `queue.sync` from inside the marshalled closure, so an
/// inline marshal called from the watcher's queue would deadlock. That is why
/// there is no `inline()` host here, unlike the C# `AppHost.Inline()`.
public struct AppHost: Sendable {
    public let marshal: @Sendable (@escaping MainActorAction) -> Void
    public let delay: @Sendable (TimeInterval, @escaping MainActorAction) -> Void
    public let now: @Sendable () -> Date
    public let background: @Sendable (@escaping @Sendable () -> Void) -> Void

    public init(marshal: @escaping @Sendable (@escaping MainActorAction) -> Void,
                delay: @escaping @Sendable (TimeInterval, @escaping MainActorAction) -> Void,
                now: @escaping @Sendable () -> Date,
                background: @escaping @Sendable (@escaping @Sendable () -> Void) -> Void) {
        self.marshal = marshal
        self.delay = delay
        self.now = now
        self.background = background
    }

    /// DispatchQueue.main for both posts — the one place the main actor is
    /// entered from outside it in the state layer — and the utility global
    /// queue for background work.
    public static func live() -> AppHost {
        AppHost(
            marshal: { work in
                DispatchQueue.main.async { MainActor.assumeIsolated(work) }
            },
            delay: { seconds, work in
                DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { MainActor.assumeIsolated(work) }
            },
            now: { Date() },
            background: { work in
                DispatchQueue.global(qos: .utility).async(execute: work)
            })
    }
}
