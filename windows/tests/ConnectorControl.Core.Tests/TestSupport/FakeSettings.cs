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
    public bool AclSweepDone { get; set; }
    public bool AutoUpdate { get; set; } = true;
    public bool TrayTipShown { get; set; }
    /// <summary>Always null: an in-memory fake has nothing to fail at. Phase 3 never reads it (see Global Constraints).</summary>
    public string? LastSaveError => null;
    public int Reloads { get; private set; }

    public void Reload() => Reloads++;
}
