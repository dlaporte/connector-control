import Foundation

extension Bundle {
    /// True under a real app bundle; false under bare `swift run` (no
    /// Info.plist, no bundle identifier) — where `UNUserNotificationCenter.current()`
    /// crashes and Sparkle has nothing to update.
    var isBundled: Bool { bundleIdentifier != nil }
}
