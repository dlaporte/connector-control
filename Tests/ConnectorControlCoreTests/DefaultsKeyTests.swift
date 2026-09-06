import XCTest
@testable import ConnectorControlCore

final class DefaultsKeyTests: XCTestCase {
    private var legacyName = ""
    private var targetName = ""

    override func setUp() {
        let id = UUID().uuidString
        legacyName = "com.dlaporte.connector-control.tests.legacy.\(id)"
        targetName = "com.dlaporte.connector-control.tests.target.\(id)"
    }

    override func tearDown() {
        UserDefaults.standard.removePersistentDomain(forName: legacyName)
        UserDefaults.standard.removePersistentDomain(forName: targetName)
    }

    /// The regression: a "Quit without asking" choice made under one of the
    /// app's earlier names must survive the rename like every other setting.
    func testEverySettingIsMigratedAndAnExistingValueIsKept() throws {
        let legacy = try XCTUnwrap(UserDefaults(suiteName: legacyName))
        let target = try XCTUnwrap(UserDefaults(suiteName: targetName))
        legacy.set(false, forKey: DefaultsKey.confirmBeforeQuit.rawValue)
        legacy.set(false, forKey: DefaultsKey.confirmBeforeRestart.rawValue)
        legacy.set(5, forKey: DefaultsKey.backupKeepCount.rawValue)
        target.set(20, forKey: DefaultsKey.backupKeepCount.rawValue)   // chosen since the upgrade: wins

        DefaultsKey.migrate(from: legacy, into: target)

        XCTAssertEqual(target.object(forKey: DefaultsKey.confirmBeforeQuit.rawValue) as? Bool, false)
        XCTAssertEqual(target.object(forKey: DefaultsKey.confirmBeforeRestart.rawValue) as? Bool, false)
        XCTAssertEqual(target.object(forKey: DefaultsKey.backupKeepCount.rawValue) as? Int, 20)
        XCTAssertNil(target.object(forKey: DefaultsKey.claudeAppPath.rawValue), "keys the legacy domain lacks are not invented")
    }

    func testTheListCoversEverySettingTheAppReads() {
        // Keep in step with SettingsView's @AppStorage declarations and AppState's
        // UserDefaults reads; a key missing here is a key the migration drops.
        XCTAssertEqual(Set(DefaultsKey.allCases.map(\.rawValue)), [
            "masterStoreDir", "claudeAppPath", "backupKeepCount", "notifyExternalChanges",
            "confirmBeforeRestart", "confirmBeforeQuit", "lastApplyDate", "permissionsSweepDone",
        ])
    }
}
