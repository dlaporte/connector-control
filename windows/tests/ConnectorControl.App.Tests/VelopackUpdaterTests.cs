using ConnectorControl.App.Services;
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
}
