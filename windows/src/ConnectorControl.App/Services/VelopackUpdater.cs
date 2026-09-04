using ConnectorControl.Core.Services;
using Velopack;
using Velopack.Sources;

namespace ConnectorControl.App.Services;

/// <summary>
/// Sparkle's role (spec §6.7) on top of Velopack: GitHub Releases feed,
/// prereleases followed only by a prerelease install. Inert when the process
/// is not a Velopack install (bare `dotnet run`, tests).
/// </summary>
public sealed class VelopackUpdater : IUpdater
{
    public const string RepoUrl = "https://github.com/dlaporte/connector-control";
    private const string DevelopmentBuild = "development build";

    private readonly UpdateManager? manager;

    public VelopackUpdater() : this(RepoUrl)
    {
    }

    public VelopackUpdater(string repoUrl)
    {
        UpdateManager? resolved = null;
        try
        {
            var stable = new UpdateManager(new GithubSource(repoUrl, null, prerelease: false));
            if (stable.IsInstalled)
            {
                var prerelease = stable.CurrentVersion?.IsPrerelease ?? false;
                resolved = prerelease ? new UpdateManager(new GithubSource(repoUrl, null, prerelease: true)) : stable;
            }
        }
        catch (Exception)
        {
            // Velopack inspects the install layout in its constructor; any failure
            // means "not an installed build". The updater must never block startup.
            resolved = null;
        }
        manager = resolved;
        VersionDisplay = manager?.CurrentVersion?.ToString() ?? DevelopmentBuild;
    }

    /// <summary>Velopack's process-start hook (install/update/uninstall callbacks). Call before anything else at startup.</summary>
    public static void RunStartupHook() => VelopackApp.Build().Run();

    public bool IsAvailable => manager is not null;

    public string VersionDisplay { get; }

    public async Task<UpdateCheck?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (manager is null)
        {
            return null;
        }
        var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null)
        {
            return null;
        }
        var target = info.TargetFullRelease;
        return new UpdateCheck(target.Version.ToString(), target.NotesMarkdown, info);
    }

    public Task DownloadAsync(UpdateCheck update, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (manager is null || update.Token is not UpdateInfo info)
        {
            return Task.CompletedTask;
        }
        return manager.DownloadUpdatesAsync(info, percent => progress?.Report(percent), cancellationToken);
    }

    public void ApplyOnQuit(UpdateCheck update)
    {
        if (manager is not null && update.Token is UpdateInfo info)
        {
            manager.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: false);
        }
    }

    public void ApplyAndRestart(UpdateCheck update)
    {
        if (manager is not null && update.Token is UpdateInfo info)
        {
            manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
        }
    }
}
