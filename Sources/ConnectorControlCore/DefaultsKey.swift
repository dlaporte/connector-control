/// The one list of keys `UserDefaultsSettings` uses, so the settings seam
/// and its tests cannot disagree about what a setting is called or which
/// ones exist. It lives in Core rather than the app because Core is the
/// target with tests.
public enum DefaultsKey: String, CaseIterable {
    case masterStoreDir
    case claudeAppPath
    case backupKeepCount
    case notifyExternalChanges
    case confirmBeforeRestart
    case confirmBeforeQuit
    /// When the app last wrote Claude's config; drives Restart Required.
    case lastApplyDate
    /// How far `PermissionsSweep`'s one-time repair has gotten; see its header comment.
    case sweepVersion
}
