namespace ConnectorControl.Core.State;

/// <summary>Why a reload is running — controls reconciliation authority and which notifications may fire (catalog §1.6).</summary>
public enum ReloadTrigger
{
    /// <summary>Launch, flyout open, or the Claude-config watcher.</summary>
    Routine,

    /// <summary>Store adoption with the user watching or on our own write's echo (Backups ▸ Restore, store repoint, deleted store file): store wins totally, no notifications.</summary>
    QuietStoreAdoption,

    /// <summary>The store watcher saw an outside write to mcps.json (sync tool, another machine): adopt it and announce the consequences.</summary>
    ExternalStoreAdoption,
}
