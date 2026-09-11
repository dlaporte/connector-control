import Foundation
@testable import ConnectorControlState

@MainActor
final class FakeSettings: AppSettings {
    var masterStoreDir: String?
    var claudeAppPath: String?
    var backupKeepCount = 20
    var notifyExternalChanges = true
    var confirmBeforeRestart = true
    var confirmBeforeQuit = true
    var lastApplyDate: Date?
    var sweepVersion = 0
}
