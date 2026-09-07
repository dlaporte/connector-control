import Foundation
import ConnectorControlCore

/// The app's UserDefaults keys (catalog §1.19) as a seam (C# ISettings).
/// Setters persist immediately and never fail. Absent keys read as the
/// catalog's defaults. Named AppSettings, not Settings: the app's SwiftUI
/// `Settings` scene would make the bare name ambiguous there.
public protocol AppSettings: AnyObject {
    /// Custom master-list directory; nil means the default (and removes the key).
    var masterStoreDir: String? { get set }
    /// Claude.app to restart; nil means `/Applications/Claude.app`.
    var claudeAppPath: String? { get set }
    var backupKeepCount: Int { get set }
    var notifyExternalChanges: Bool { get set }
    var confirmBeforeRestart: Bool { get set }
    var confirmBeforeQuit: Bool { get set }
    var lastApplyDate: Date? { get set }
    var permissionsSweepDone: Bool { get set }
}

/// `UserDefaults.standard` in the app; a suite in tests. Every key name comes
/// from `DefaultsKey`, so the migration and this class cannot drift apart.
public final class UserDefaultsSettings: AppSettings {
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    public var masterStoreDir: String? {
        get { defaults.string(forKey: DefaultsKey.masterStoreDir.rawValue) }
        set { setOrRemove(newValue, DefaultsKey.masterStoreDir) }
    }

    public var claudeAppPath: String? {
        get { defaults.string(forKey: DefaultsKey.claudeAppPath.rawValue) }
        set { setOrRemove(newValue, DefaultsKey.claudeAppPath) }
    }

    public var backupKeepCount: Int {
        get { defaults.object(forKey: DefaultsKey.backupKeepCount.rawValue) as? Int ?? 20 }
        set { defaults.set(newValue, forKey: DefaultsKey.backupKeepCount.rawValue) }
    }

    public var notifyExternalChanges: Bool {
        get { bool(DefaultsKey.notifyExternalChanges, default: true) }
        set { defaults.set(newValue, forKey: DefaultsKey.notifyExternalChanges.rawValue) }
    }

    public var confirmBeforeRestart: Bool {
        get { bool(DefaultsKey.confirmBeforeRestart, default: true) }
        set { defaults.set(newValue, forKey: DefaultsKey.confirmBeforeRestart.rawValue) }
    }

    public var confirmBeforeQuit: Bool {
        get { bool(DefaultsKey.confirmBeforeQuit, default: true) }
        set { defaults.set(newValue, forKey: DefaultsKey.confirmBeforeQuit.rawValue) }
    }

    public var lastApplyDate: Date? {
        get { defaults.object(forKey: DefaultsKey.lastApplyDate.rawValue) as? Date }
        set { setOrRemove(newValue, DefaultsKey.lastApplyDate) }
    }

    public var permissionsSweepDone: Bool {
        get { bool(DefaultsKey.permissionsSweepDone, default: false) }
        set { defaults.set(newValue, forKey: DefaultsKey.permissionsSweepDone.rawValue) }
    }

    private func bool(_ key: DefaultsKey, default fallback: Bool) -> Bool {
        defaults.object(forKey: key.rawValue) as? Bool ?? fallback
    }

    private func setOrRemove(_ value: Any?, _ key: DefaultsKey) {
        if let value {
            defaults.set(value, forKey: key.rawValue)
        } else {
            defaults.removeObject(forKey: key.rawValue)
        }
    }
}
