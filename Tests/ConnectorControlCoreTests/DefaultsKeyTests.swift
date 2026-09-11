import XCTest
@testable import ConnectorControlCore

final class DefaultsKeyTests: XCTestCase {
    func testTheListCoversEverySettingTheAppReads() {
        // Keep in step with SettingsView's @AppStorage declarations and AppState's
        // UserDefaults reads; a key missing here is a setting the seam forgets.
        XCTAssertEqual(Set(DefaultsKey.allCases.map(\.rawValue)), [
            "masterStoreDir", "claudeAppPath", "backupKeepCount", "notifyExternalChanges",
            "confirmBeforeRestart", "confirmBeforeQuit", "lastApplyDate", "permissionsSweepDone",
            "aclSweepDone",
        ])
    }
}
