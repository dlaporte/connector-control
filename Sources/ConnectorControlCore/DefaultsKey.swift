import Foundation

/// The UserDefaults keys the Mac app stores its settings under — one list,
/// so the Settings window, AppState and the legacy-domain migration cannot
/// disagree about what a setting is called or which ones exist. (Before this
/// list, confirmBeforeQuit was added to the app but not to the migration. No
/// shipped build could hit that — the legacy domains predate the setting —
/// but the next such omission would be live.) It lives in Core rather than
/// the app because Core is the target with tests.
public enum DefaultsKey: String, CaseIterable {
    case masterStoreDir
    case claudeAppPath
    case backupKeepCount
    case notifyExternalChanges
    case confirmBeforeRestart
    case confirmBeforeQuit
    /// When the app last wrote Claude's config; drives Restart Required.
    case lastApplyDate
    /// One-time repair of files written before owner-only permissions were
    /// enforced. Migrated with the rest: the directory moves across with the
    /// rename, so what the old app already swept stays swept.
    case permissionsSweepDone

    /// Copies every key `legacy` holds into `target`, never overwriting a
    /// value `target` already has — a setting changed since the upgrade wins
    /// over the old copy. Used once per legacy defaults domain at launch.
    public static func migrate(from legacy: UserDefaults, into target: UserDefaults) {
        for key in allCases.map(\.rawValue) {
            if let value = legacy.object(forKey: key), target.object(forKey: key) == nil {
                target.set(value, forKey: key)
            }
        }
    }
}
