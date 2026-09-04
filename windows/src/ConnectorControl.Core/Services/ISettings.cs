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

    /// <summary>Re-read the file (external edits).</summary>
    void Reload();
}
