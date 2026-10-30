using ConnectorControl.Core;
using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;

namespace ConnectorControl.TestHost;

/// <summary>
/// Mirror: Sources/ConnectorControlTestHost/TestHostServices.swift
///
/// The platform seams of a host with no platform: settings in memory, no Claude, no notifications but a
/// line on stderr, no dialogs, no launchers found, no login item, no Claude install and no updates.
/// </summary>
internal static class TestHostServices
{
    public const string SeedConfig = """{"mcpServers":{"fetch":{"command":"uvx","args":["mcp-server-fetch"]},"time":{"command":"uvx","args":["mcp-server-time"]}}}""";
    public const string NoClaude = "The test host has no Claude Desktop to restart.";
}

/// <summary>Held in memory, as the Mac host's are, so every run starts from the same settings: nothing a
/// set_setting changed in one run carries into the next.</summary>
internal sealed class TestHostSettings : ISettings
{
    public string? MasterStoreDir { get; set; }
    public string? ClaudeConfigPath { get; set; }
    public string? ClaudeLaunchTarget { get; set; }
    public int BackupKeepCount { get; set; } = BackupManager.DefaultKeepCount;
    public bool NotifyExternalChanges { get; set; }
    public bool ConfirmBeforeRestart { get; set; } = true;
    public bool ConfirmBeforeQuit { get; set; } = true;
    public bool ClaudeIntegrationEnabled { get; set; } = true;
    public bool ConfirmClaudeChanges { get; set; } = true;
    public bool ClaudeIntegrationOfferAnswered { get; set; } = true;
    public DateTime? LastApplyDate { get; set; }
    public int SweepVersion { get; set; } = PermissionsSweep.CurrentVersion;
    public bool AutoUpdate { get; set; }
    public bool TrayTipShown { get; set; }
    public string? DeclinedUpdateVersion { get; set; }
    public string? LastSaveError => null;
}

internal sealed class TestHostClaude : IClaudeProcess
{
    public ClaudeProcessSnapshot Snapshot() => new(false, null);

    public Task<string?> RestartAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(TestHostServices.NoClaude);
}

internal sealed class TestHostNotifier : INotifier
{
    // Never raised: there is no toast to click.
    public event Action? RestartActionActivated
    {
        add { }
        remove { }
    }

    public void Notify(string title, string body, string? category = null) => Console.Error.WriteLine($"notification: {title}: {body}");
}

internal sealed class TestHostDialogs : IDialogs
{
    public bool Confirm(string message, string? informativeText, string primaryTitle, string cancelTitle = Dialogs.CancelTitle, bool destructive = false,
                        bool cancelIsDefault = false) => false;

    public string? PromptForName(string title, string initial) => null;

    public void Inform(string message, string? informativeText)
    {
    }

    public bool OfferUpdate(string newVersion, string currentVersion, string? notesMarkdown, Func<CancellationToken, Task> install) => false;

    public bool ConfirmChange(ConfirmationDialog dialog) => false;
}

internal sealed class TestHostToolProbe : IToolProbe
{
    public IReadOnlyDictionary<Tool, ToolStatus> Probe(IReadOnlyList<Tool> tools) => tools.ToDictionary(tool => tool, _ => ToolStatus.NotFound);
}

internal sealed class TestHostAutostart : IAutostart
{
    public bool IsEnabled => false;

    public void SetEnabled(bool enabled)
    {
    }
}

internal sealed class TestHostInstall : IClaudeInstall
{
    public ClaudeInstallInfo Detect() => ClaudeInstallInfo.NotFound;
}

internal sealed class TestHostUpdater : IUpdater
{
    public bool IsAvailable => false;

    public string VersionDisplay => AppVersion.DevelopmentBuild;

    public Task<UpdateCheck?> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult<UpdateCheck?>(null);

    public Task DownloadAsync(UpdateCheck update, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void ApplyOnQuit(UpdateCheck update)
    {
    }

    public void ApplyAndRestart(UpdateCheck update)
    {
    }
}
