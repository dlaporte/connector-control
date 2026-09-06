import Combine
@testable import ConnectorControlState

final class FakeUpdater: Updater {
    var isAvailable = true
    var versionDisplay = "1.2.2"
    private(set) var checks = 0
    private let autoUpdateSubject = CurrentValueSubject<Bool, Never>(true)

    var automaticallyDownloadsUpdates: Bool {
        get { autoUpdateSubject.value }
        set { autoUpdateSubject.send(newValue) }
    }

    var automaticallyDownloadsUpdatesPublisher: AnyPublisher<Bool, Never> {
        autoUpdateSubject.eraseToAnyPublisher()
    }

    func checkForUpdates() { checks += 1 }
}
