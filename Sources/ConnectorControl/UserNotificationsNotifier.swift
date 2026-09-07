import Foundation
@preconcurrency import UserNotifications
import ConnectorControlState

/// Catalog §1.8–§1.9. UNUserNotificationCenter.current() crashes under bare
/// `swift run` (no app bundle), so every call is gated on hasAppBundle.
final class UserNotificationsNotifier: Notifier {
    static let hasAppBundle = Bundle.main.bundleIdentifier != nil

    var onRestartAction: MainActorAction?
    /// The center holds its delegate weakly; this notifier retains the bridge.
    private var handler: NotificationActionHandler?

    /// Registers the category whose Restart Claude button routes back into AppState.
    init() {
        guard UserNotificationsNotifier.hasAppBundle else { return }
        let center = UNUserNotificationCenter.current()
        let restart = UNNotificationAction(identifier: Notifications.restartAction, title: Notifications.restartButton)
        center.setNotificationCategories([
            UNNotificationCategory(identifier: Notifications.restartCategory, actions: [restart], intentIdentifiers: [])])
        let handler = NotificationActionHandler { [weak self] in self?.onRestartAction?() }
        center.delegate = handler
        self.handler = handler
    }

    func notify(title: String, body: String, category: String?) {
        guard UserNotificationsNotifier.hasAppBundle else { return }
        let center = UNUserNotificationCenter.current()
        center.requestAuthorization(options: [.alert]) { granted, _ in
            guard granted else { return }
            let content = UNMutableNotificationContent()
            content.title = title
            content.body = body
            if let category { content.categoryIdentifier = category }
            center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        }
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
