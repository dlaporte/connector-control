/// Why a reload is running — controls reconciliation authority and which
/// notifications may fire (catalog §1.6).
public enum ReloadTrigger: Sendable {
    /// Launch, popover open, or the Claude-config watcher.
    case routine
    /// Store adoption with the user watching or on our own write's echo
    /// (Backups ▸ Restore, store repoint, deleted store file): store wins
    /// totally, no notifications.
    case quietStoreAdoption
    /// The store watcher saw an outside write to mcps.json (sync tool, another
    /// machine): adopt it and announce the consequences.
    case externalStoreAdoption
}
