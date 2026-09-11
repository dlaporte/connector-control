import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

@MainActor
final class UserDefaultsSettingsTests: XCTestCase {
    // One fixed throwaway domain, cleared before and after: cfprefsd keeps an
    // empty plist per suite name in ~/Library/Preferences, so a fresh UUID per
    // run would leave one file behind every time.
    private let suiteName = "com.dlaporte.connector-control.tests.settings"

    override func setUp() {
        UserDefaults.standard.removePersistentDomain(forName: suiteName)
    }

    override func tearDown() {
        UserDefaults.standard.removePersistentDomain(forName: suiteName)
    }

    func testEveryKeyRoundTripsAndDefaultsApplyWhenAbsent() throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        let settings = UserDefaultsSettings(defaults: defaults)
        // The defaults when a key is absent.
        XCTAssertNil(settings.masterStoreDir)
        XCTAssertNil(settings.claudeAppPath)
        XCTAssertEqual(settings.backupKeepCount, 20)
        XCTAssertTrue(settings.notifyExternalChanges)
        XCTAssertTrue(settings.confirmBeforeRestart)
        XCTAssertTrue(settings.confirmBeforeQuit)
        XCTAssertNil(settings.lastApplyDate)
        XCTAssertEqual(settings.sweepVersion, 0)

        let applied = Date(timeIntervalSince1970: 1_788_523_200)
        settings.masterStoreDir = "/tmp/synced"
        settings.claudeAppPath = "/Applications/Claude Beta.app"
        settings.backupKeepCount = 7
        settings.notifyExternalChanges = false
        settings.confirmBeforeRestart = false
        settings.confirmBeforeQuit = false
        settings.lastApplyDate = applied
        settings.sweepVersion = 2

        // Read back through a second instance over the same suite: the values are on disk, under DefaultsKey's names.
        let again = UserDefaultsSettings(defaults: defaults)
        XCTAssertEqual(again.masterStoreDir, "/tmp/synced")
        XCTAssertEqual(again.claudeAppPath, "/Applications/Claude Beta.app")
        XCTAssertEqual(again.backupKeepCount, 7)
        XCTAssertFalse(again.notifyExternalChanges)
        XCTAssertFalse(again.confirmBeforeRestart)
        XCTAssertFalse(again.confirmBeforeQuit)
        XCTAssertEqual(again.lastApplyDate, applied)
        XCTAssertEqual(again.sweepVersion, 2)
        XCTAssertEqual(defaults.string(forKey: DefaultsKey.masterStoreDir.rawValue), "/tmp/synced")

        // nil removes the key.
        settings.masterStoreDir = nil
        settings.claudeAppPath = nil
        settings.lastApplyDate = nil
        XCTAssertNil(defaults.object(forKey: DefaultsKey.masterStoreDir.rawValue))
        XCTAssertNil(defaults.object(forKey: DefaultsKey.claudeAppPath.rawValue))
        XCTAssertNil(defaults.object(forKey: DefaultsKey.lastApplyDate.rawValue))
    }

    /// windows/tests/ConnectorControl.Core.Tests/SettingsStoreTests.cs
    /// UnknownKeysAndWrongTypesAreTolerated. UserDefaults has no on-disk schema
    /// to corrupt, so the Mac risk is narrower: a value of the wrong Swift
    /// type under a known key must fall back to the documented default
    /// instead of crashing the `as?` cast, and a key this app never wrote
    /// must survive untouched.
    func testUnknownKeysAndWrongTypesAreTolerated() throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.set(1, forKey: "future.x")                                      // a key this app does not know
        defaults.set("lots", forKey: DefaultsKey.backupKeepCount.rawValue)       // Int expected
        defaults.set("no", forKey: DefaultsKey.notifyExternalChanges.rawValue)   // Bool expected
        defaults.set("not a date", forKey: DefaultsKey.lastApplyDate.rawValue)   // Date expected
        let settings = UserDefaultsSettings(defaults: defaults)
        XCTAssertEqual(settings.backupKeepCount, 20, "wrong type falls back to the default")
        XCTAssertTrue(settings.notifyExternalChanges, "wrong type falls back to the default")
        XCTAssertNil(settings.lastApplyDate, "wrong type falls back to the default")

        settings.sweepVersion = 2   // a write to an unrelated key must not disturb the unknown one
        XCTAssertEqual(defaults.object(forKey: "future.x") as? Int, 1)
    }
}
