/// UNUserNotificationCenter (catalog §1.8, §1.9) as a seam.
@MainActor
public protocol Notifier: AnyObject {
    /// Post a notification; `category == Notifications.restartCategory` adds the
    /// Restart Claude action button.
    func notify(title: String, body: String, category: String?)
    /// Runs on the main actor when the user clicks the Restart Claude action.
    /// AppState sets it at init and clears it in `dispose()`.
    var onRestartAction: MainActorAction? { get set }
}
