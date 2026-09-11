import Foundation
@preconcurrency import UserNotifications
import ConnectorControlState

/// UNUserNotificationCenter.current() crashes under bare `swift run` (no app
/// bundle), so every call is gated on hasAppBundle.
@MainActor
final class UserNotificationsNotifier: Notifier {
    static let hasAppBundle = Bundle.main.isBundled

    var onRestartAction: MainActorAction?
    /// The center holds its delegate weakly; this notifier retains the bridge.
    private var handler: NotificationActionHandler?
    /// Whether authorization has been granted, so later notifications skip asking again; nil
    /// until a request has succeeded. Only a grant is remembered — a denial is NOT cached, so
    /// the next notification asks again, in case the user granted it later in System Settings
    /// without relaunching the app.
    private var authorization: Bool?

    /// Registers the category whose Restart Claude button routes back into AppState.
    init() {
        guard UserNotificationsNotifier.hasAppBundle else { return }
        let center = UNUserNotificationCenter.current()
        let restart = UNNotificationAction(identifier: Notifications.restartAction, title: Notifications.restartToastButton)
        center.setNotificationCategories([
            UNNotificationCategory(identifier: Notifications.restartCategory, actions: [restart], intentIdentifiers: [])])
        let handler = NotificationActionHandler { [weak self] in self?.onRestartAction?() }
        center.delegate = handler
        self.handler = handler
    }

    func notify(title: String, body: String, category: String?) {
        guard UserNotificationsNotifier.hasAppBundle else { return }
        let center = UNUserNotificationCenter.current()
        if authorization == true {
            post(title: title, body: body, category: category, to: center)
            return
        }
        // requestAuthorization's completion can land on any thread; hop back
        // to the main actor before touching `self` (now @MainActor).
        center.requestAuthorization(options: [.alert]) { [weak self] granted, _ in
            Task { @MainActor in
                guard granted else { return }   // not cached: retried on the next notification
                self?.authorization = true
                self?.post(title: title, body: body, category: category, to: center)
            }
        }
    }

    private func post(title: String, body: String, category: String?, to center: UNUserNotificationCenter) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        if let category { content.categoryIdentifier = category }
        center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
    }
}

/// NSObject bridge for UNUserNotificationCenter's delegate: routes the
/// Restart Claude notification action back to the main actor.
private final class NotificationActionHandler: NSObject, UNUserNotificationCenterDelegate {
    private let onRestartAction: @MainActor () -> Void

    init(onRestartAction: @escaping @MainActor () -> Void) {
        self.onRestartAction = onRestartAction
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        if response.actionIdentifier == Notifications.restartAction {
            let action = onRestartAction
            Task { @MainActor in action() }
        }
        completionHandler()
    }

    /// Without this, notifications are silently discarded while Connector
    /// Control is the active app (Settings or the editor focused) — exactly
    /// when a restart-pending banner is still worth showing.
    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler([.banner, .list])
    }
}
