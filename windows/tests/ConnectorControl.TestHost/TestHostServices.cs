using ConnectorControl.Core;
using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;

namespace ConnectorControl.TestHost;

/// <summary>
/// Mirror: Sources/ConnectorControlTestHost/TestHostServices.swift
///
/// The platform seams of a host with no platform: no Claude, no notifications but a line on stderr, no
/// dialogs, no launchers found, no login item, no Claude install and no updates.
/// </summary>
internal static class TestHostServices
{
    public const string SeedConfig = """{"mcpServers":{"fetch":{"command":"uvx","args":["mcp-server-fetch"]},"time":{"command":"uvx","args":["mcp-server-time"]}}}""";
    public const string NoClaude = "The test host has no Claude Desktop to restart.";
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
    public bool Confirm(string message, string? informativeText, string primaryTitle, string cancelTitle = "Cancel", bool destructive = false,
                        bool cancelIsDefault = false) => false;

    public string? PromptForName(string title, string initial) => null;

    public void Inform(string message, string? informativeText)
    {
    }

    public bool OfferUpdate(string newVersion, string currentVersion, string? notesMarkdown) => false;
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
