import XCTest
@testable import ConnectorControlCore

final class DefaultsKeyTests: XCTestCase {
    func testTheListCoversEverySettingTheAppReads() {
        // Keep in step with `AppSettings`; a key missing here is a setting the seam forgets.
        XCTAssertEqual(Set(DefaultsKey.allCases.map(\.rawValue)), [
            "masterStoreDir", "claudeAppPath", "backupKeepCount", "notifyExternalChanges",
            "confirmBeforeRestart", "confirmBeforeQuit", "lastApplyDate", "sweepVersion",
        ])
    }
}
