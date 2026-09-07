import XCTest
@testable import ConnectorControlCore

final class DefaultsKeyTests: XCTestCase {
    // Fixed throwaway domains, cleared before and after: cfprefsd keeps an
    // empty plist per suite name in ~/Library/Preferences, so a fresh UUID per
    // run would leave two files behind every time.
    private let legacyName = "com.dlaporte.connector-control.tests.legacy"
    private let targetName = "com.dlaporte.connector-control.tests.target"

    override func setUp() {
        UserDefaults.standard.removePersistentDomain(forName: legacyName)
        UserDefaults.standard.removePersistentDomain(forName: targetName)
    }

    override func tearDown() {
        UserDefaults.standard.removePersistentDomain(forName: legacyName)
        UserDefaults.standard.removePersistentDomain(forName: targetName)
    }

    /// Every setting a legacy domain holds moves — confirmBeforeQuit included,
    /// the one the hand-written list used to leave out.
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
