import Combine
import Foundation
import Sparkle
import ConnectorControlState

/// Created not-started; started only from a real app bundle — bare `swift run`
/// has none and Sparkle requires one.
@MainActor
final class SparkleUpdater: Updater {
    private let controller = SPUStandardUpdaterController(
        startingUpdater: false, updaterDelegate: nil, userDriverDelegate: nil)

    private(set) var isAvailable = false

    func startIfBundled() {
        guard Bundle.main.isBundled, !isAvailable else { return }
        controller.startUpdater()
        isAvailable = true
    }

    var versionDisplay: String { AppVersion.display(info: Bundle.main.infoDictionary) }

    var automaticallyDownloadsUpdates: Bool {
        get { controller.updater.automaticallyDownloadsUpdates }
        set { controller.updater.automaticallyDownloadsUpdates = newValue }
    }

    var automaticallyDownloadsUpdatesPublisher: AnyPublisher<Bool, Never> {
        controller.updater.publisher(for: \.automaticallyDownloadsUpdates).eraseToAnyPublisher()
    }

    func checkForUpdates() { controller.checkForUpdates(nil) }
}
