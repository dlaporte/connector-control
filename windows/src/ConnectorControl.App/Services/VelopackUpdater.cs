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
    private readonly Func<string, string?, UpdateVerifier.RunningIdentity, string, string, string?> verify;
    private readonly Func<string?, UpdateVerifier.RunningIdentity, string?> verifyInstalled;
    private readonly Action<string> log;
    private readonly string? savedUpdaterPath;

    /// <summary>Where, in the app's data folder, the installed Update.exe is saved while an update is checked.</summary>
    public const string SavedUpdaterFileName = "Update.exe.before-update";

    public VelopackUpdater() : this(RepoUrl)
    {
    }

    public VelopackUpdater(string repoUrl)
        : this(prerelease => new GithubSource(repoUrl, null, prerelease), locator: null,
            savedUpdaterPath: Path.Combine(ServiceFactory.DefaultDataDir, SavedUpdaterFileName))
    {
    }

    /// <summary>
    /// Test seam. <paramref name="source"/> builds the feed for a given "include prereleases"
    /// flag (GithubSource in the app); <paramref name="locator"/> describes the install
    /// (Velopack's TestVelopackLocator in tests; null = inspect the real install layout);
    /// <paramref name="verify"/> replaces <see cref="UpdateVerifier.Verify"/> in tests,
    /// <paramref name="verifyInstalled"/> replaces <see cref="UpdateVerifier.VerifyInstalledUpdater"/>
    /// and <paramref name="identify"/> replaces <see cref="UpdateVerifier.ExpectedIdentity"/>;
    /// <paramref name="log"/> replaces <see cref="App.Log"/>, which writes to the live data folder;
    /// <paramref name="savedUpdaterPath"/> is where the installed Update.exe is saved during a check (null: kept in
    /// memory only, as tests that never reach a download want).
    /// </summary>
    internal VelopackUpdater(
        Func<bool, IUpdateSource> source,
        IVelopackLocator? locator,
        Func<string, string?, UpdateVerifier.RunningIdentity, string, string, string?>? verify = null,
        Func<string?, UpdateVerifier.RunningIdentity, string?>? verifyInstalled = null,
        Func<string, UpdateVerifier.RunningIdentity>? identify = null,
        Action<string>? log = null,
        string? savedUpdaterPath = null)
    {
        this.savedUpdaterPath = savedUpdaterPath;
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
        var logTo = log ?? App.Log;
        this.log = logTo;
        this.verify = verify ?? ((package, updateExe, running, current, feed) => UpdateVerifier.Verify(package, updateExe, running, current, feed, logTo));
        this.verifyInstalled = verifyInstalled ?? ((updateExe, running) => UpdateVerifier.VerifyInstalledUpdater(updateExe, running, logTo));
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
        var knownGoodUpdater = PrepareInstalledUpdater(identity);
        await manager.DownloadUpdatesAsync(info, cancelToken: cancellationToken).ConfigureAwait(false);
        FinishCheck(info, knownGoodUpdater, identity);
    }

    /// <summary>
    /// Before a download. Velopack may run the installed Update.exe during it, so it must already be ours. If it is
    /// not, and a copy saved by an earlier check is there, that check never finished — the app was quit or ended
    /// while it ran, after Velopack had put the package's Update.exe in place — so the saved one is put back, every
    /// package in packages\ (none of which that check passed) is deleted, and that is logged; then the installed one
    /// is checked again. Once it passes, it is read and saved to disk, staged, so a check that is interrupted in its
    /// turn can be recovered the same way. Returns the bytes to restore if this check refuses the package.
    /// </summary>
    internal byte[]? PrepareInstalledUpdater(UpdateVerifier.RunningIdentity identity)
    {
        var location = manager!.Location;
        if (verifyInstalled(location.UpdateExePath, identity) is { } installedProblem)
        {
            if (!RestoreSavedUpdater(location) || verifyInstalled(location.UpdateExePath, identity) is not null)
            {
                throw new UpdateVerificationException(installedProblem);
            }
        }
        var knownGoodUpdater = location.UpdateExePath is { } updateExe && File.Exists(updateExe) ? File.ReadAllBytes(updateExe) : null;
        SaveUpdater(knownGoodUpdater);
        return knownGoodUpdater;
    }

    /// <summary>The package check, then the saved Update.exe goes: the check ended, in acceptance or refusal, and the
    /// restore it was kept for has happened or is not needed. Anything else that ends it keeps the copy for next time.</summary>
    internal void FinishCheck(UpdateInfo info, byte[]? knownGoodUpdater, UpdateVerifier.RunningIdentity identity)
    {
        try
        {
            VerifyDownloaded(info, knownGoodUpdater, identity);
        }
        catch (UpdateVerificationException)
        {
            DeleteSavedUpdater();
            throw;
        }
        DeleteSavedUpdater();
    }

    /// <summary>Writes <paramref name="updater"/> beside its final name, then moves it there, so the saved copy is never
    /// half-written. No updater installed, or no place to save it: any older copy goes, so it cannot be restored over a
    /// newer one. A save that fails is logged; the check still has the copy in memory.</summary>
    private void SaveUpdater(byte[]? updater)
    {
        if (savedUpdaterPath is null)
        {
            return;
        }
        if (updater is null)
        {
            DeleteSavedUpdater();
            return;
        }
        var staged = savedUpdaterPath + ".partial";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savedUpdaterPath)!);
            File.WriteAllBytes(staged, updater);
            File.Move(staged, savedUpdaterPath, overwrite: true);
        }
        catch (Exception ex)
        {
            FileSystemErrors.TryDelete(staged);
            log($"The installed updater could not be saved before an update ({savedUpdaterPath}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DeleteSavedUpdater()
    {
        if (savedUpdaterPath is not null)
        {
            FileSystemErrors.TryDelete(savedUpdaterPath);
        }
    }

    /// <summary>Puts the saved Update.exe back and deletes every package left in packages\; true when it did.</summary>
    private bool RestoreSavedUpdater(Velopack.Locators.IVelopackLocator location)
    {
        if (savedUpdaterPath is null || !File.Exists(savedUpdaterPath) || location.UpdateExePath is not { } updateExe)
        {
            return false;
        }
        try
        {
            File.Copy(savedUpdaterPath, updateExe, overwrite: true);
        }
        catch (Exception ex)
        {
            log($"The updater saved before an interrupted update could not be put back ({updateExe}): {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        if (location.PackagesDir is { } packages && Directory.Exists(packages))
        {
            foreach (var package in Directory.EnumerateFiles(packages, "*.nupkg").Concat(Directory.EnumerateFiles(packages, "*.partial")))
            {
                FileSystemErrors.TryDelete(package);
            }
        }
        DeleteSavedUpdater();
        log($"An update check was interrupted: the updater saved before it was put back at {updateExe}, and the packages it had not passed were deleted.");
        return true;
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
        // Everything the check reads from the locator is read inside the try: a locator that throws (Velopack's own
        // test locator does for any path it was not given) makes this a refusal like any other failure, never an
        // exception that skips the clean-up below.
        string? packagePath = null;
        string? updateExe = null;
        Exception? updaterUnknown = null;
        string? problem;
        try
        {
            packagePath = LocalPackagePath(location.PackagesDir, info.TargetFullRelease.FileName);
            try
            {
                updateExe = location.UpdateExePath;
            }
            catch (Exception ex)
            {
                updaterUnknown = ex;
                throw;
            }
            // Full versions, prerelease labels included: see UpdateVerifier.VersionProblem.
            problem = verify(packagePath, updateExe, identity, manager.CurrentVersion?.ToString() ?? "", info.TargetFullRelease.Version.ToString());
        }
        catch (Exception ex)
        {
            problem = UpdateVerifier.Unreadable(ex);
        }
        if (problem is null)
        {
            return;
        }
        if (packagePath is null)
        {
            log("The refused update package could not be located, so it was not deleted.");
        }
        else
        {
            FileSystemErrors.TryDelete(packagePath);
            if (File.Exists(packagePath))
            {
                // Velopack no longer applies it at launch, but it must not wait in packages\ unnoticed.
                log($"The refused update package could not be deleted: {packagePath}");
            }
        }
        if (updaterUnknown is not null)
        {
            log($"The updater could not be put back after a refused update: its location is unknown ({updaterUnknown.GetType().Name}: {updaterUnknown.Message})");
        }
        else if (updateExe is not null)
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
}
