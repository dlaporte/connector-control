using ConnectorControl.Core.Services;
using Velopack;
using Velopack.Locators;
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

    /// <summary>UpdateManager keeps its locator protected; the verifier needs PackagesDir and UpdateExePath.</summary>
    private sealed class LocatingUpdateManager(IUpdateSource source, IVelopackLocator? locator)
        // Never a delta: applying one runs the installed Update.exe over feed-supplied bytes before anything is verified. A full package costs bandwidth and nothing else.
        : UpdateManager(source, new UpdateOptions { MaximumDeltasBeforeFallback = 0 }, locator)
    {
        public IVelopackLocator Location => Locator;
    }

    private readonly LocatingUpdateManager? manager;
    private readonly Func<string, string?, string, Version, Version, string?> verify;
    private readonly Func<string?, string, string?> verifyInstalled;

    public VelopackUpdater() : this(RepoUrl)
    {
    }

    public VelopackUpdater(string repoUrl)
        : this(prerelease => new GithubSource(repoUrl, null, prerelease), locator: null)
    {
    }

    /// <summary>
    /// Test seam. <paramref name="source"/> builds the feed for a given "include prereleases"
    /// flag (GithubSource in the app); <paramref name="locator"/> describes the install
    /// (Velopack's TestVelopackLocator in tests; null = inspect the real install layout);
    /// <paramref name="verify"/> replaces <see cref="UpdateVerifier.Verify"/> in tests and
    /// <paramref name="verifyInstalled"/> replaces <see cref="UpdateVerifier.VerifyInstalledUpdater"/>.
    /// </summary>
    internal VelopackUpdater(Func<bool, IUpdateSource> source, IVelopackLocator? locator, Func<string, string?, string, Version, Version, string?>? verify = null, Func<string?, string, string?>? verifyInstalled = null)
    {
        LocatingUpdateManager? resolved = null;
        var followsPrereleases = false;
        try
        {
            var stable = new LocatingUpdateManager(source(false), locator);
            if (stable.IsInstalled)
            {
                // The version comes from current\sq.version, i.e. from `vpk pack --packVersion`:
                // a preview (1.3.0-preview.N) follows prereleases, a release (1.3.0) does not.
                followsPrereleases = stable.CurrentVersion?.IsPrerelease ?? false;
                resolved = followsPrereleases ? new LocatingUpdateManager(source(true), locator) : stable;
            }
        }
        catch (Exception)
        {
            // Velopack inspects the install layout in its constructor; any failure
            // means "not an installed build". The updater must never block startup.
            resolved = null;
            followsPrereleases = false;
        }
        manager = resolved;
        FollowsPrereleases = followsPrereleases;
        VersionDisplay = manager?.CurrentVersion?.ToString() ?? DevelopmentBuild;
        this.verify = verify ?? UpdateVerifier.Verify;
        this.verifyInstalled = verifyInstalled ?? UpdateVerifier.VerifyInstalledUpdater;
    }

    /// <summary>Spec §6.7: true for a preview install (prerelease version), so update checks include prereleases.</summary>
    public bool FollowsPrereleases { get; }

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

    public async Task DownloadAsync(UpdateCheck update, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (manager is null || update.Token is not UpdateInfo info)
        {
            return;
        }
        var location = manager.Location;
        var runningExe = Environment.ProcessPath
            ?? throw new UpdateVerificationException("This app's own location is unknown, so an update cannot be checked" + UpdateVerifier.NotInstalledSuffix);
        // Velopack may run the installed Update.exe during a download; it must already be ours,
        // and a copy of it is kept so a refused package cannot leave its own updater behind.
        if (verifyInstalled(location.UpdateExePath, runningExe) is { } installedProblem)
        {
            throw new UpdateVerificationException(installedProblem);
        }
        var knownGoodUpdater = location.UpdateExePath is { } updateExe && File.Exists(updateExe) ? File.ReadAllBytes(updateExe) : null;
        await manager.DownloadUpdatesAsync(info, percent => progress?.Report(percent), cancellationToken).ConfigureAwait(false);
        VerifyDownloaded(info, knownGoodUpdater, runningExe);
    }

    /// <summary>
    /// Velopack has checked the feed checksum and already overwritten Update.exe from the package.
    /// Before anything is staged, that Update.exe and every binary in the package must be signed by
    /// this app's own publisher and carry the advertised version. A refused package is deleted and
    /// the previous Update.exe put back, so neither can be picked up by a later apply or uninstall.
    /// </summary>
    internal void VerifyDownloaded(UpdateInfo info, byte[]? knownGoodUpdater, string? runningExe = null)
    {
        if (manager is null)
        {
            return;
        }
        var location = manager.Location;
        var packagePath = Path.Combine(location.PackagesDir ?? "", info.TargetFullRelease.FileName);
        var running = Numeric(manager.CurrentVersion);
        var feed = Numeric(info.TargetFullRelease.Version);
        if (verify(packagePath, location.UpdateExePath, runningExe ?? Environment.ProcessPath ?? "", running, feed) is { } problem)
        {
            try { File.Delete(packagePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (location.UpdateExePath is { } updateExe)
            {
                // Put back the updater that was there before the download; when there was none,
                // the one there now came from the refused package and goes too.
                try
                {
                    if (knownGoodUpdater is not null) { File.WriteAllBytes(updateExe, knownGoodUpdater); }
                    else if (File.Exists(updateExe)) { File.Delete(updateExe); }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw new UpdateVerificationException(problem);
        }
    }

    private static Version Numeric(SemanticVersion? version) =>
        version is null ? new Version(0, 0, 0) : new Version(version.Major, version.Minor, version.Patch);

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
