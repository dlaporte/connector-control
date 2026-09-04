using ConnectorControl.App.Services;

namespace ConnectorControl.App.Tests;

public class VelopackUpdaterTests
{
    [Fact]
    public async Task IsInertWhenNotInstalled()
    {
        // The test host is not a Velopack install: everything must be a safe no-op.
        var updater = new VelopackUpdater();
        Assert.False(updater.IsAvailable);
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
}
