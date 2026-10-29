using ConnectorControl.Core;
using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace ConnectorControl.App.Services;

/// <summary>
/// Sparkle's role on Windows, on top of Velopack: GitHub Releases feed,
/// prereleases followed only by a prerelease install. Inert when the process
/// is not a Velopack install (bare `dotnet run`, tests).
/// </summary>
public sealed class VelopackUpdater : IUpdater
{
    public const string RepoUrl = "https://github.com/dlaporte/connector-control";

    /// <summary>UpdateManager keeps its locator protected; the verifier needs PackagesDir and UpdateExePath.</summary>
    private sealed class LocatingUpdateManager(IUpdateSource source, IVelopackLocator? locator)
        // Never a delta: applying one runs the installed Update.exe over feed-supplied bytes before anything is verified. A full package costs bandwidth and nothing else.
        : UpdateManager(source, new UpdateOptions { MaximumDeltasBeforeFallback = 0 }, locator)
    {
        public IVelopackLocator Location => Locator;
    }

    private readonly LocatingUpdateManager? manager;
    private readonly Func<string, UpdateVerifier.RunningIdentity> identify;
    private readonly Func<string, string?, UpdateVerifier.RunningIdentity, Version, Version, string?> verify;
    private readonly Func<string?, UpdateVerifier.RunningIdentity, string?> verifyInstalled;
    private readonly Action<string> log;

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
    /// <paramref name="verify"/> replaces <see cref="UpdateVerifier.Verify"/> in tests,
    /// <paramref name="verifyInstalled"/> replaces <see cref="UpdateVerifier.VerifyInstalledUpdater"/>
    /// and <paramref name="identify"/> replaces <see cref="UpdateVerifier.ExpectedIdentity"/>;
    /// <paramref name="log"/> replaces <see cref="App.Log"/>, which writes to the live data folder.
    /// </summary>
    internal VelopackUpdater(
        Func<bool, IUpdateSource> source,
        IVelopackLocator? locator,
        Func<string, string?, UpdateVerifier.RunningIdentity, Version, Version, string?>? verify = null,
        Func<string?, UpdateVerifier.RunningIdentity, string?>? verifyInstalled = null,
        Func<string, UpdateVerifier.RunningIdentity>? identify = null,
        Action<string>? log = null)
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
        VersionDisplay = manager?.CurrentVersion?.ToString() ?? AppVersion.DevelopmentBuild;
        this.identify = identify ?? UpdateVerifier.ExpectedIdentity;
        this.verify = verify ?? UpdateVerifier.Verify;
        this.verifyInstalled = verifyInstalled ?? UpdateVerifier.VerifyInstalledUpdater;
        this.log = log ?? App.Log;
    }

    /// <summary>True for a preview install (prerelease version), so update checks include prereleases.</summary>
    public bool FollowsPrereleases { get; }

    /// <summary>Velopack's hooks, first thing at startup. Before an uninstall, the app's entry in Claude's config,
    /// its PATH folder and its copy of the tool go (<see cref="UninstallCleanup"/>).</summary>
    public static void RunStartupHook() => StartupHook().Run();

    /// <summary>
    /// The hooks <see cref="RunStartupHook"/> runs. Velopack would otherwise apply, at launch, the newest package
    /// it finds in packages\ — a package this app's check refused, or never finished checking, included. Updates
    /// are applied only by <see cref="ApplyOnQuit"/> and <see cref="ApplyAndRestart"/>, after the check has
    /// passed. Nothing is lost: a checked package left unapplied is found again by the next check, which
    /// re-verifies it before offering it.
    /// </summary>
    internal static VelopackApp StartupHook() =>
        VelopackApp.Build().SetAutoApplyOnStartup(false).OnBeforeUninstallFastCallback(_ => UninstallCleanup.Run());

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

    public async Task DownloadAsync(UpdateCheck update, CancellationToken cancellationToken = default)
    {
        if (manager is null || update.Token is not UpdateInfo info)
        {
            return;
        }
        var location = manager.Location;
        var runningExe = Environment.ProcessPath
            ?? throw new UpdateVerificationException("This app's own location is unknown, so an update cannot be checked" + UpdateVerifier.NotInstalledSuffix);
        // The running app's identity is read once per download and decides both checks below.
        var identity = identify(runningExe);
        // Velopack may run the installed Update.exe during a download; it must already be ours,
        // and a copy of it is kept so a refused package cannot leave its own updater behind.
        if (verifyInstalled(location.UpdateExePath, identity) is { } installedProblem)
        {
            throw new UpdateVerificationException(installedProblem);
        }
        var knownGoodUpdater = location.UpdateExePath is { } updateExe && File.Exists(updateExe) ? File.ReadAllBytes(updateExe) : null;
        await manager.DownloadUpdatesAsync(info, cancelToken: cancellationToken).ConfigureAwait(false);
        VerifyDownloaded(info, knownGoodUpdater, identity);
    }

    /// <summary>
    /// Velopack has checked the feed checksum and already overwritten Update.exe from the package.
    /// Before anything is staged, that Update.exe and every binary in the package must be signed by
    /// this app's own publisher (<paramref name="identity"/>) and carry the advertised version. A
    /// refused package is deleted and the previous Update.exe put back, so neither can be picked up
    /// by a later apply or uninstall. A check that throws, for any reason, is a refusal too: the
    /// clean-up must never be skipped by an exception the check did not expect.
    /// </summary>
    internal void VerifyDownloaded(UpdateInfo info, byte[]? knownGoodUpdater, UpdateVerifier.RunningIdentity identity)
    {
        if (manager is null)
        {
            return;
        }
        var location = manager.Location;
        var packagePath = LocalPackagePath(location.PackagesDir, info.TargetFullRelease.FileName);
        string? problem;
        try
        {
            problem = verify(packagePath, location.UpdateExePath, identity, NumericVersion(manager.CurrentVersion), NumericVersion(info.TargetFullRelease.Version));
        }
        catch (Exception ex)
        {
            problem = UpdateVerifier.Unreadable(ex);
        }
        if (problem is null)
        {
            return;
        }
        FileSystemErrors.TryDelete(packagePath);
        if (File.Exists(packagePath))
        {
            // Velopack no longer applies it at launch, but it must not wait in packages\ unnoticed.
            log($"The refused update package could not be deleted: {packagePath}");
        }
        if (location.UpdateExePath is { } updateExe)
        {
            // Put back the updater that was there before the download; when there was none,
            // the one there now came from the refused package and goes too. A failure is logged, never
            // swallowed: the refused package's updater may still be in place.
            try
            {
                if (knownGoodUpdater is not null)
                {
                    File.WriteAllBytes(updateExe, knownGoodUpdater);
                }
                else if (File.Exists(updateExe))
                {
                    File.Delete(updateExe);
                }
            }
            catch (Exception ex)
            {
                log($"The updater could not be put back after a refused update ({updateExe}): {ex.GetType().Name}: {ex.Message}");
            }
        }
        throw new UpdateVerificationException(problem);
    }

    /// <summary>
    /// Where Velopack saves a downloaded package: its file name, with every character a file name cannot hold
    /// replaced by "_" (Velopack's own PathUtil.GetSafeFilename, which is internal). The check must read, and a
    /// refusal delete, exactly the file Velopack wrote.
    /// </summary>
    internal static string LocalPackagePath(string? packagesDir, string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(Path.GetFileName(fileName).Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
        return Path.Combine(packagesDir ?? "", safe);
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

    /// <summary>Major.minor.patch of a Velopack version as a System.Version; a missing version is 0.0.0.</summary>
    private static Version NumericVersion(SemanticVersion? version) =>
        new(version?.Major ?? 0, version?.Minor ?? 0, version?.Patch ?? 0);
}
