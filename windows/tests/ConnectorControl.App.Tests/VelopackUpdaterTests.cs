using System.IO.Compression;
using System.Reflection;
using ConnectorControl.App.Services;
using ConnectorControl.Core.Services;
using ConnectorControl.Core.Tests.TestSupport;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace ConnectorControl.App.Tests;

public class VelopackUpdaterTests
{
    /// <summary>Stands in for the running app's signer; the verifier seams below only record or compare it.</summary>
    private static readonly UpdateVerifier.RunningIdentity TestIdentity = new("CN=Test Publisher, O=Test Publisher, C=US", false);

    /// <summary>The framework's own DLL: Authenticode-signed on every official install, so it stands in for a file this app signed.</summary>
    private static string SignedFrameworkFile => typeof(object).Assembly.Location;

    private static readonly UpdateVerifier.RunningIdentity FrameworkIdentity = UpdateVerifier.ExpectedIdentity(SignedFrameworkFile);

    private static void RequireSignedFramework() =>
        Assert.True(FrameworkIdentity.Subject is not null, "the shared framework must be Authenticode-signed for this test");

    /// <summary>The 1.3.2 full package the feed advertises to the 1.3.1 install these tests describe.</summary>
    private static VelopackAsset Asset() =>
        new() { PackageId = "ConnectorControl", Version = new SemanticVersion(1, 3, 2), Type = VelopackAssetType.Full, FileName = "ConnectorControl-1.3.2-win-x64-full.nupkg", SHA1 = "", SHA256 = "", Size = 0 };

    /// <summary>An update source that records what the UpdateManager asks for and answers "no releases".</summary>
    private sealed class RecordingSource : IUpdateSource
    {
        public List<(string? AppId, string Channel)> FeedRequests { get; } = [];

        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            FeedRequests.Add((appId, channel));
            return Task.FromResult(new VelopackAssetFeed());
        }

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// An "installed" app as Velopack's own test locator describes it: package id ConnectorControl,
    /// the given version, and channel win-x64 — exactly what `vpk pack --packId ConnectorControl
    /// --channel win-x64` writes into current\sq.version.
    /// </summary>
    private static (VelopackUpdater Updater, RecordingSource Stable, RecordingSource Prerelease) Installed(string version, TempDir packages)
    {
        var stable = new RecordingSource();
        var prerelease = new RecordingSource();
        var locator = new TestVelopackLocator("ConnectorControl", version, packages.Path, appDir: null, rootDir: null, updateExe: null, channel: "win-x64");
        return (new VelopackUpdater(wantPrerelease => wantPrerelease ? prerelease : stable, locator), stable, prerelease);
    }

    [Fact]
    public async Task IsInertWhenNotInstalled()
    {
        // The test host is not a Velopack install: everything must be a safe no-op.
        var updater = new VelopackUpdater();
        Assert.False(updater.IsAvailable);
        Assert.False(updater.FollowsPrereleases);
        Assert.Equal("development build", updater.VersionDisplay);
        Assert.Null(await updater.CheckAsync(TestContext.Current.CancellationToken));
        var fake = new ConnectorControl.Core.Services.UpdateCheck("9.9.9", null, new object());
        await updater.DownloadAsync(fake, cancellationToken: TestContext.Current.CancellationToken);
        updater.ApplyOnQuit(fake);
        updater.ApplyAndRestart(fake);
    }

    [Fact]
    public void RepoUrlIsTheProjectRepository()
    {
        Assert.Equal("https://github.com/dlaporte/connector-control", VelopackUpdater.RepoUrl);
    }

    [Fact]
    public async Task PreviewInstallAsksThePrereleaseFeedOnItsOwnChannel()
    {
        // The prerelease flag = current version is a prerelease. A preview is packed as
        // 1.3.0-preview.N, so the installed app must query GitHub WITH prereleases and for the
        // channel it was packed with — that is the whole contract between the workflow and the app.
        using var packages = new TempDir("velopack");
        var (updater, stable, prerelease) = Installed("1.3.0-preview.1", packages);
        Assert.True(updater.IsAvailable);
        Assert.True(updater.FollowsPrereleases);
        Assert.Equal("1.3.0-preview.1", updater.VersionDisplay);
        Assert.Null(await updater.CheckAsync(TestContext.Current.CancellationToken));   // empty feed = up to date
        Assert.Empty(stable.FeedRequests);
        Assert.Equal(("ConnectorControl", "win-x64"), Assert.Single(prerelease.FeedRequests));
    }

    [Fact]
    public async Task ReleaseInstallAsksTheStableFeedOnly()
    {
        using var packages = new TempDir("velopack");
        var (updater, stable, prerelease) = Installed("1.3.0", packages);
        Assert.True(updater.IsAvailable);
        Assert.False(updater.FollowsPrereleases);
        Assert.Equal("1.3.0", updater.VersionDisplay);
        Assert.Null(await updater.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Empty(prerelease.FeedRequests);
        Assert.Equal(("ConnectorControl", "win-x64"), Assert.Single(stable.FeedRequests));
    }

    [Fact]
    public void ARefusedPackageThrowsAndIsDeletedBeforeAnythingIsStaged()
    {
        // The seam between Velopack's download and ApplyOnQuit: the verifier sees the package
        // Velopack wrote and the Update.exe it replaced, and a refusal surfaces as the exception
        // the coordinator announces. Nothing is staged and the package does not linger.
        using var packages = new TempDir("vpk");
        var updateExe = packages.File("Update.exe");
        File.WriteAllText(updateExe, "stub");
        (string Package, string? UpdateExe, UpdateVerifier.RunningIdentity Identity)? seen = null;
        var locator = new TestVelopackLocator("ConnectorControl", "1.3.1", packages.Path, appDir: null, rootDir: null, updateExe: updateExe, channel: "win-x64");
        var updater = new VelopackUpdater(_ => new RecordingSource(), locator, verify: (p, u, id, running, feed) => { seen = (p, u, id); return "refused by the test verifier"; }, verifyInstalled: (_, _) => null, log: _ => { });
        var asset = Asset();
        var packagePath = packages.File(asset.FileName);
        File.WriteAllText(packagePath, "not really a package");

        var ex = Assert.Throws<UpdateVerificationException>(() => updater.VerifyDownloaded(new UpdateInfo(asset, isDowngrade: false), knownGoodUpdater: null, TestIdentity));
        Assert.Equal("refused by the test verifier", ex.Message);
        Assert.Equal((packagePath, updateExe, TestIdentity), seen);
        Assert.False(File.Exists(packagePath), "a refused package is not left for a later apply");
    }

    [Fact]
    public void AnAcceptedPackageIsLeftInPlace()
    {
        using var packages = new TempDir("vpk");
        var updateExe = packages.File("Update.exe");
        File.WriteAllText(updateExe, "stub");
        var locator = new TestVelopackLocator("ConnectorControl", "1.3.1", packages.Path, appDir: null, rootDir: null, updateExe: updateExe, channel: "win-x64");
        var updater = new VelopackUpdater(_ => new RecordingSource(), locator, verify: (_, _, _, _, _) => null, verifyInstalled: (_, _) => null, log: _ => { });
        var asset = Asset();
        File.WriteAllText(packages.File(asset.FileName), "package");
        updater.VerifyDownloaded(new UpdateInfo(asset, isDowngrade: false), knownGoodUpdater: null, TestIdentity);
        Assert.True(File.Exists(packages.File(asset.FileName)));
    }

    [Fact]
    public void ARefusedPackagePutsThePreviousUpdaterBack()
    {
        using var packages = new TempDir("vpk");
        var updateExe = packages.File("Update.exe");
        File.WriteAllText(updateExe, "from the refused package");
        var locator = new TestVelopackLocator("ConnectorControl", "1.3.1", packages.Path, appDir: null, rootDir: null, updateExe: updateExe, channel: "win-x64");
        var updater = new VelopackUpdater(_ => new RecordingSource(), locator, verify: (_, _, _, _, _) => "refused", verifyInstalled: (_, _) => null, log: _ => { });
        var asset = Asset();
        File.WriteAllText(packages.File(asset.FileName), "package");
        Assert.Throws<UpdateVerificationException>(() => updater.VerifyDownloaded(new UpdateInfo(asset, isDowngrade: false), "the one that was there before"u8.ToArray(), TestIdentity));
        Assert.Equal("the one that was there before", File.ReadAllText(updateExe));
    }

    [Fact]
    public void ARefusedPackageRemovesAnUpdaterThatWasNotThereBefore()
    {
        using var packages = new TempDir("vpk");
        var updateExe = packages.File("Update.exe");
        File.WriteAllText(updateExe, "from the refused package");
        var locator = new TestVelopackLocator("ConnectorControl", "1.3.1", packages.Path, appDir: null, rootDir: null, updateExe: updateExe, channel: "win-x64");
        var updater = new VelopackUpdater(_ => new RecordingSource(), locator, verify: (_, _, _, _, _) => "refused", verifyInstalled: (_, _) => null, log: _ => { });
        var asset = Asset();
        File.WriteAllText(packages.File(asset.FileName), "package");
        Assert.Throws<UpdateVerificationException>(() => updater.VerifyDownloaded(new UpdateInfo(asset, isDowngrade: false), knownGoodUpdater: null, TestIdentity));
        Assert.False(File.Exists(updateExe));
    }

    [Fact]
    public void AVerifierThatThrowsIsARefusal()
    {
        // Whatever the check trips over, the package must not stay behind for a later apply, and the
        // updater it brought must not stay in place of ours.
        using var packages = new TempDir("vpk");
        var updateExe = packages.File("Update.exe");
        File.WriteAllText(updateExe, "from the refused package");
        var locator = new TestVelopackLocator("ConnectorControl", "1.3.1", packages.Path, appDir: null, rootDir: null, updateExe: updateExe, channel: "win-x64");
        var updater = new VelopackUpdater(_ => new RecordingSource(), locator, verify: (_, _, _, _, _) => throw new ArgumentException("Null character in path."), verifyInstalled: (_, _) => null, log: _ => { });
        var asset = Asset();
        File.WriteAllText(packages.File(asset.FileName), "package");

        var ex = Assert.Throws<UpdateVerificationException>(() => updater.VerifyDownloaded(new UpdateInfo(asset, isDowngrade: false), "the one that was there before"u8.ToArray(), TestIdentity));
        Assert.Contains("Null character in path.", ex.Message);
        Assert.EndsWith(UpdateVerifier.NotInstalledSuffix, ex.Message);
        Assert.False(File.Exists(packages.File(asset.FileName)), "a package the check could not finish is refused like any other");
        Assert.Equal("the one that was there before", File.ReadAllText(updateExe));
    }

    [Fact]
    public void APackageWithANulNamedProgramIsRefusedAndCleanedUp()
    {
        // A program whose entry name Windows cannot create used to throw out of the check before the
        // package was deleted or the updater put back; Velopack then applied it at the next launch.
        RequireSignedFramework();
        using var packages = new TempDir("vpk");
        var updateExe = packages.File("Update.exe");
        File.Copy(SignedFrameworkFile, updateExe);   // what the package left: signed as the running app is, so the check reaches the entries
        var locator = new TestVelopackLocator("ConnectorControl", "1.3.1", packages.Path, appDir: null, rootDir: null, updateExe: updateExe, channel: "win-x64");
        var updater = new VelopackUpdater(_ => new RecordingSource(), locator, verifyInstalled: (_, _) => null, log: _ => { });
        var asset = Asset();
        var packagePath = packages.File(asset.FileName);
        using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("lib/x\0y.exe").Open();
            entry.Write(File.ReadAllBytes(SignedFrameworkFile));
        }

        var ex = Assert.Throws<UpdateVerificationException>(() => updater.VerifyDownloaded(new UpdateInfo(asset, isDowngrade: false), "the one that was there before"u8.ToArray(), FrameworkIdentity));
        Assert.EndsWith(UpdateVerifier.NotInstalledSuffix, ex.Message);
        Assert.False(File.Exists(packagePath), "the refused package is gone");
        Assert.Equal("the one that was there before", File.ReadAllText(updateExe));
    }

    [Fact]
    public void AnUpdaterThatCannotBePutBackIsLoggedAndTheUpdateIsStillRefused()
    {
        using var packages = new TempDir("vpk");
        var updateExe = packages.File("Update.exe");
        Directory.CreateDirectory(updateExe);   // nothing can be written where the updater belongs
        var logged = new List<string>();
        var locator = new TestVelopackLocator("ConnectorControl", "1.3.1", packages.Path, appDir: null, rootDir: null, updateExe: updateExe, channel: "win-x64");
        var updater = new VelopackUpdater(_ => new RecordingSource(), locator, verify: (_, _, _, _, _) => "refused", verifyInstalled: (_, _) => null, log: logged.Add);
        var asset = Asset();
        File.WriteAllText(packages.File(asset.FileName), "package");

        var ex = Assert.Throws<UpdateVerificationException>(() => updater.VerifyDownloaded(new UpdateInfo(asset, isDowngrade: false), "the one that was there before"u8.ToArray(), TestIdentity));
        Assert.Equal("refused", ex.Message);
        Assert.Contains(logged, line => line.Contains(updateExe, StringComparison.Ordinal));
    }

    [Fact]
    public void ThePackageIsLookedForWhereVelopackSavesIt()
    {
        // Velopack replaces every character a file name cannot hold with "_" before it saves the
        // download; the check must read, and a refusal delete, that same file.
        using var packages = new TempDir("vpk");
        string? seen = null;
        // Velopack's test locator throws for an Update.exe path it was not given; this one names a file that is not there.
        var locator = new TestVelopackLocator("ConnectorControl", "1.3.1", packages.Path, appDir: null, rootDir: null, updateExe: packages.File("Update.exe"), channel: "win-x64");
        var updater = new VelopackUpdater(_ => new RecordingSource(), locator, verify: (p, _, _, _, _) => { seen = p; return "refused"; }, verifyInstalled: (_, _) => null, log: _ => { });
        var asset = Asset();
        asset.FileName = "ConnectorControl-1.3.2\0-win-x64-full.nupkg";
        var saved = packages.File("ConnectorControl-1.3.2_-win-x64-full.nupkg");
        File.WriteAllText(saved, "package");

        Assert.Throws<UpdateVerificationException>(() => updater.VerifyDownloaded(new UpdateInfo(asset, isDowngrade: false), knownGoodUpdater: null, TestIdentity));
        Assert.Equal(saved, seen);
        Assert.False(File.Exists(saved));
    }

    [Fact]
    public void TheCheckIsGivenTheFullVersionsPrereleaseLabelsIncluded()
    {
        // Every 1.5.0 preview and the 1.5.0 release share one numeric version; only the label tells them apart.
        using var packages = new TempDir("vpk");
        (string Running, string Feed)? seen = null;
        var locator = new TestVelopackLocator("ConnectorControl", "1.5.0-preview.2", packages.Path, appDir: null, rootDir: null, updateExe: packages.File("Update.exe"), channel: "win-x64");
        var updater = new VelopackUpdater(_ => new RecordingSource(), locator, verify: (_, _, _, running, feed) => { seen = (running, feed); return null; }, verifyInstalled: (_, _) => null, log: _ => { });
        var asset = Asset();
        asset.Version = SemanticVersion.Parse("1.5.0-preview.3");
        updater.VerifyDownloaded(new UpdateInfo(asset, isDowngrade: false), knownGoodUpdater: null, TestIdentity);
        Assert.Equal(("1.5.0-preview.2", "1.5.0-preview.3"), seen);
    }

    [Fact]
    public void ALocatorThatCannotNameTheUpdaterIsARefusal()
    {
        // Velopack's test locator, given no Update.exe, throws NotSupportedException for its path, as a broken real
        // one might. Even with a check that would pass, the package is refused and deleted, and the updater that
        // could not be put back is logged.
        using var packages = new TempDir("vpk");
        var logged = new List<string>();
        var locator = new TestVelopackLocator("ConnectorControl", "1.3.1", packages.Path, appDir: null, rootDir: null, updateExe: null, channel: "win-x64");
        var updater = new VelopackUpdater(_ => new RecordingSource(), locator, verify: (_, _, _, _, _) => null, verifyInstalled: (_, _) => null, log: logged.Add);
        var asset = Asset();
        File.WriteAllText(packages.File(asset.FileName), "package");

        var ex = Assert.Throws<UpdateVerificationException>(() => updater.VerifyDownloaded(new UpdateInfo(asset, isDowngrade: false), knownGoodUpdater: null, TestIdentity));
        Assert.Contains("UpdateExePath", ex.Message);
        Assert.False(File.Exists(packages.File(asset.FileName)));
        Assert.Contains(logged, line => line.Contains("location is unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void ADownloadedPackageIsNeverAppliedAtStartupWithoutTheCheck()
    {
        // Velopack's startup hook would otherwise apply the newest package in packages\ on the next
        // launch, whether or not this app's check ever passed it.
        var hook = VelopackUpdater.StartupHook();
        var autoApply = typeof(VelopackApp).GetField("_autoApply", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(autoApply);
        Assert.False((bool)autoApply.GetValue(hook)!);
    }

    [Fact]
    public async Task AForeignInstalledUpdaterStopsTheDownloadBeforeItStarts()
    {
        using var packages = new TempDir("vpk");
        var updateExe = packages.File("Update.exe");
        File.WriteAllText(updateExe, "not ours");
        var locator = new TestVelopackLocator("ConnectorControl", "1.3.1", packages.Path, appDir: null, rootDir: null, updateExe: updateExe, channel: "win-x64");
        UpdateVerifier.RunningIdentity? seen = null;
        var updater = new VelopackUpdater(
            _ => new RecordingSource(), locator,
            verify: (_, _, _, _, _) => null,
            verifyInstalled: (_, identity) => { seen = identity; return "The installed Update.exe is not validly signed"; },
            identify: _ => TestIdentity);
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => updater.DownloadAsync(new UpdateCheck("1.3.2", null, new UpdateInfo(Asset(), isDowngrade: false)), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("installed Update.exe", ex.Message);
        Assert.Equal(TestIdentity, seen);   // the identity read once per download is what the installed-updater check sees
    }
}
