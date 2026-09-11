namespace ConnectorControl.Core.Services;

/// <summary>The Windows counterpart of the Mac app's UserDefaults keys. Setters persist immediately.</summary>
public interface ISettings
{
    string? MasterStoreDir { get; set; }
    string? ClaudeConfigPath { get; set; }
    /// <summary>
    /// Broader than the Mac's <c>claudeAppPath</c>: the detected target can be an MSIX app
    /// identity, the stored override is always an exe path.
    /// </summary>
    string? ClaudeLaunchTarget { get; set; }
    int BackupKeepCount { get; set; }
    bool NotifyExternalChanges { get; set; }
    bool ConfirmBeforeRestart { get; set; }
    bool ConfirmBeforeQuit { get; set; }
    DateTime? LastApplyDate { get; set; }
    /// <summary>How far PermissionsSweep's one-time repair has gotten; see its header comment.</summary>
    int SweepVersion { get; set; }
    bool AutoUpdate { get; set; }
    bool TrayTipShown { get; set; }

    /// <summary>
    /// Setters never throw. When persisting a change fails (e.g. the store
    /// directory is unwritable), the in-memory value is kept regardless, and
    /// this holds the OS error message from that failure; the next
    /// successful save clears it back to null.
    /// </summary>
    string? LastSaveError { get; }
}
