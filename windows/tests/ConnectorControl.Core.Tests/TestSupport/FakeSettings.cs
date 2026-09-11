using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.Tests.TestSupport;

public sealed class FakeSettings : ISettings
{
    public string? MasterStoreDir { get; set; }
    public string? ClaudeConfigPath { get; set; }
    public string? ClaudeLaunchTarget { get; set; }
    public int BackupKeepCount { get; set; } = 20;
    public bool NotifyExternalChanges { get; set; } = true;
    public bool ConfirmBeforeRestart { get; set; } = true;
    public bool ConfirmBeforeQuit { get; set; } = true;
    public DateTime? LastApplyDate { get; set; }
    public int SweepVersion { get; set; }
    public bool AutoUpdate { get; set; }
    public bool TrayTipShown { get; set; }
    public string? DeclinedUpdateVersion { get; set; }
    /// <summary>Settable so a test can simulate a failed settings save (FlyoutModel.ErrorMessage's last-resort banner).</summary>
    public string? LastSaveError { get; set; }
}
