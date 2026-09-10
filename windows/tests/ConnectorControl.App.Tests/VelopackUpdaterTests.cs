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
        // Spec §6.7: prerelease flag = current version is a prerelease. A preview is packed as
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
        (string Package, string? UpdateExe, string Running)? seen = null;
        var locator = new TestVelopackLocator("ConnectorControl", "1.3.1", packages.Path, appDir: null, rootDir: null, updateExe: updateExe, channel: "win-x64");
        var updater = new VelopackUpdater(_ => new RecordingSource(), locator, verify: (p, u, r, running, feed) => { seen = (p, u, r); return "refused by the test verifier"; }, verifyInstalled: (_, _) => null);
        var asset = new VelopackAsset { PackageId = "ConnectorControl", Version = new SemanticVersion(1, 3, 2), Type = VelopackAssetType.Full, FileName = "ConnectorControl-1.3.2-win-x64-full.nupkg", SHA1 = "", SHA256 = "", Size = 0 };
        var packagePath = packages.File(asset.FileName);
        File.WriteAllText(packagePath, "not really a package");

        var ex = Assert.Throws<UpdateVerificationException>(() => updater.VerifyDownloaded(new UpdateInfo(asset, isDowngrade: false), knownGoodUpdater: null));
        Assert.Equal("refused by the test verifier", ex.Message);
        Assert.Equal((packagePath, updateExe, Environment.ProcessPath!), seen);
        Assert.False(File.Exists(packagePath), "a refused package is not left for a later apply");
    }

    [Fact]
    public void AnAcceptedPackageIsLeftInPlace()
    {
        using var packages = new TempDir("vpk");
        var updateExe = packages.File("Update.exe");
        File.WriteAllText(updateExe, "stub");
        var locator = new TestVelopackLocator("ConnectorControl", "1.3.1", packages.Path, appDir: null, rootDir: null, updateExe: updateExe, channel: "win-x64");
        var updater = new VelopackUpdater(_ => new RecordingSource(), locator, verify: (_, _, _, _, _) => null, verifyInstalled: (_, _) => null);
        var asset = new VelopackAsset { PackageId = "ConnectorControl", Version = new SemanticVersion(1, 3, 2), Type = VelopackAssetType.Full, FileName = "ConnectorControl-1.3.2-win-x64-full.nupkg", SHA1 = "", SHA256 = "", Size = 0 };
        File.WriteAllText(packages.File(asset.FileName), "package");
        updater.VerifyDownloaded(new UpdateInfo(asset, isDowngrade: false), knownGoodUpdater: null);
        Assert.True(File.Exists(packages.File(asset.FileName)));
    }

    [Fact]
    public void ARefusedPackagePutsThePreviousUpdaterBack()
    {
        using var packages = new TempDir("vpk");
        var updateExe = packages.File("Update.exe");
        File.WriteAllText(updateExe, "from the refused package");
        var locator = new TestVelopackLocator("ConnectorControl", "1.3.1", packages.Path, appDir: null, rootDir: null, updateExe: updateExe, channel: "win-x64");
        var updater = new VelopackUpdater(_ => new RecordingSource(), locator, verify: (_, _, _, _, _) => "refused", verifyInstalled: (_, _) => null);
        var asset = new VelopackAsset { PackageId = "ConnectorControl", Version = new SemanticVersion(1, 3, 2), Type = VelopackAssetType.Full, FileName = "ConnectorControl-1.3.2-win-x64-full.nupkg", SHA1 = "", SHA256 = "", Size = 0 };
        File.WriteAllText(packages.File(asset.FileName), "package");
        Assert.Throws<UpdateVerificationException>(() => updater.VerifyDownloaded(new UpdateInfo(asset, isDowngrade: false), "the one that was there before"u8.ToArray()));
        Assert.Equal("the one that was there before", File.ReadAllText(updateExe));
    }

    [Fact]
    public async Task AForeignInstalledUpdaterStopsTheDownloadBeforeItStarts()
    {
        using var packages = new TempDir("vpk");
        var updateExe = packages.File("Update.exe");
        File.WriteAllText(updateExe, "not ours");
        var locator = new TestVelopackLocator("ConnectorControl", "1.3.1", packages.Path, appDir: null, rootDir: null, updateExe: updateExe, channel: "win-x64");
        var updater = new VelopackUpdater(_ => new RecordingSource(), locator, verify: (_, _, _, _, _) => null, verifyInstalled: (_, _) => "The installed Update.exe is not validly signed");
        var asset = new VelopackAsset { PackageId = "ConnectorControl", Version = new SemanticVersion(1, 3, 2), Type = VelopackAssetType.Full, FileName = "ConnectorControl-1.3.2-win-x64-full.nupkg", SHA1 = "", SHA256 = "", Size = 0 };
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => updater.DownloadAsync(new UpdateCheck("1.3.2", null, new UpdateInfo(asset, isDowngrade: false)), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("installed Update.exe", ex.Message);
    }
}
