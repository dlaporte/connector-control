@testable import ConnectorControlState

final class FakeNotifier: Notifier {
    struct Sent: Equatable {
        let title: String
        let body: String
        let category: String?
    }

    private(set) var sent: [Sent] = []
    var onRestartAction: MainActorAction?

    func notify(title: String, body: String, category: String?) {
        sent.append(Sent(title: title, body: body, category: category))
    }

    func clearSent() { sent.removeAll() }

    /// Simulates the user clicking the notification's Restart Claude button.
    @MainActor
    func activateRestart() { onRestartAction?() }
}
