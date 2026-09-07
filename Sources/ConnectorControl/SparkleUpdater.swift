import Combine
import Foundation
import Sparkle
import ConnectorControlState

/// Catalog §1.2 step 5 and §4.2. Created not-started; started only from a
/// real app bundle — bare `swift run` has none and Sparkle requires one.
final class SparkleUpdater: Updater {
    private let controller = SPUStandardUpdaterController(
        startingUpdater: false, updaterDelegate: nil, userDriverDelegate: nil)

    private(set) var isAvailable = false

    func startIfBundled() {
        guard Bundle.main.bundleIdentifier != nil, !isAvailable else { return }
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
