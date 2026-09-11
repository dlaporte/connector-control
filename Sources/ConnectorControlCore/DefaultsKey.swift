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
    /// One-time repair of files written before owner-only permissions were
    /// enforced.
    case permissionsSweepDone
    /// One-time strip of inherited ACL entries from files written before AtomicFile did it
    /// at creation. Separate from permissionsSweepDone so an install swept for modes still
    /// gets this pass once.
    case aclSweepDone
}
