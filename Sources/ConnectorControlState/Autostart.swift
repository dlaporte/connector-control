/// SMAppService as a seam.
@MainActor
public protocol Autostart: AnyObject {
    /// Read fresh each time: the user may change it in System Settings.
    var isEnabled: Bool { get }
    /// macOS only: registered, but System Settings has not approved it yet.
    var requiresApproval: Bool { get }
    /// Throws an error whose `localizedDescription` is the OS text.
    func setEnabled(_ enabled: Bool) throws
}
