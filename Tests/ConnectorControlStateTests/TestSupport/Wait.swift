import Foundation

enum Wait {
    /// The one ceiling for a wait on real background work that is expected to
    /// finish: a watcher's event, a timer, a queued callback. Generous on
    /// purpose, as the C# suite's `Wait.Eventually` is: a wait that succeeds
    /// returns the moment its condition holds, so only a failing test ever
    /// pays it. Work a test can run itself goes through a seam instead
    /// (AppHost's background and delay); the windows that prove something
    /// does NOT happen are short and named where they are used.
    static let eventually: TimeInterval = 30
}
