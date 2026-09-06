namespace ConnectorControl.Core.Services;

/// <summary>The Mac app's UserDefaults keys (spec §6.5). Setters persist immediately.</summary>
public interface ISettings
{
    string? MasterStoreDir { get; set; }
    string? ClaudeConfigPath { get; set; }
    string? ClaudeLaunchTarget { get; set; }
    int BackupKeepCount { get; set; }
    bool NotifyExternalChanges { get; set; }
    bool ConfirmBeforeRestart { get; set; }
    bool ConfirmBeforeQuit { get; set; }
    DateTime? LastApplyDate { get; set; }
    bool AclSweepDone { get; set; }
    bool AutoUpdate { get; set; }
    bool TrayTipShown { get; set; }

    /// <summary>
    /// Setters never throw. When persisting a change fails (e.g. the store
    /// directory is unwritable), the in-memory value is kept regardless, and
    /// this holds the OS error message from that failure; the next
    /// successful save clears it back to null.
    /// </summary>
    string? LastSaveError { get; }

    /// <summary>Re-read the file (external edits).</summary>
    void Reload();
}
