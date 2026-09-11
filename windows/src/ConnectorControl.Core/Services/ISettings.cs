namespace ConnectorControl.Core.Services;

/// <summary>The Windows counterpart of the Mac app's UserDefaults keys. Setters persist immediately.</summary>
public interface ISettings
{
    /// <summary>Custom master-list directory; null means the default (and removes the key).</summary>
    string? MasterStoreDir { get; set; }
    /// <summary>Windows-only: a custom path to Claude's config file; null means the default location.</summary>
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
    /// <summary>Windows-only: whether a background (non-interactive) update check downloads and stages the update automatically.</summary>
    bool AutoUpdate { get; set; }
    /// <summary>Windows-only: whether the first-run tray tip has already been shown.</summary>
    bool TrayTipShown { get; set; }
    /// <summary>
    /// The version most recently declined via a non-interactive update offer; null while none is
    /// declined. It stays recorded until the user declines another version — declining a version
    /// only suppresses a future offer of that exact version; a different version is always
    /// offered. Persisted so the decline survives a relaunch — the same way the Mac's Sparkle
    /// persists "Skip This Version".
    /// </summary>
    string? DeclinedUpdateVersion { get; set; }

    /// <summary>
    /// Setters never throw. When persisting a change fails (e.g. the store
    /// directory is unwritable), the in-memory value is kept regardless, and
    /// this holds the OS error message from that failure; the next
    /// successful save clears it back to null.
    /// </summary>
    string? LastSaveError { get; }
}
