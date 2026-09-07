import Combine

/// What Settings ▸ General needs from Sparkle (catalog §4.2). Sparkle itself
/// owns the schedule, the download, the staging and every update dialog.
public protocol Updater: AnyObject {
    /// False under bare `swift run` (no bundle); every control is then disabled.
    var isAvailable: Bool { get }
    /// "1.3.0", "1.3.0 (10300)", or "development build".
    var versionDisplay: String { get }
    /// Sparkle persists this itself (SUAutomaticallyUpdate).
    var automaticallyDownloadsUpdates: Bool { get set }
    /// Fires when Sparkle changes the flag from its own dialog.
    var automaticallyDownloadsUpdatesPublisher: AnyPublisher<Bool, Never> { get }
    /// Settings ▸ Check for Updates…; Sparkle shows the result.
    func checkForUpdates()
}
