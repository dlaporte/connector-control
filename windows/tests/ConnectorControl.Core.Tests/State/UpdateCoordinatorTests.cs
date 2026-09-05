using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class UpdateCoordinatorTests
{
    private readonly FakeUpdater updater = new();
    private readonly FakeSettings settings = new();
    private readonly FakeNotifier notifier = new();
    private readonly FakeDialogs dialogs = new();
    private readonly DelayQueue delays = new();

    private UpdateCoordinator Coordinator() =>
        new(updater, settings, notifier, dialogs, new AppHost(a => a(), delays.Add, () => DateTime.UtcNow));

    private static UpdateCheck Update(string version = "1.3.0") => new(version, "## Fixes\n- one", new object());

    [Fact]
    public async Task InertWhenNotRunningFromAnInstall()
    {
        updater.IsAvailable = false;
        using var coordinator = Coordinator();
        coordinator.Start();
        Assert.Empty(delays.Pending);
        Assert.Equal(UpdateOutcome.Unavailable, await coordinator.CheckAsync(interactive: true));
        Assert.Equal(0, updater.Checks);
        Assert.Empty(dialogs.Informs);
    }

    [Fact]
    public void ChecksTenSecondsAfterLaunchAndThenDaily()
    {
        using var coordinator = Coordinator();
        coordinator.Start();
        coordinator.Start();   // idempotent
        Assert.Single(delays.Pending);
        Assert.Equal(TimeSpan.FromSeconds(10), delays.Pending[0].Delay);
        delays.RunNext();
        Assert.Equal(1, updater.Checks);
        Assert.Single(delays.Pending);
        Assert.Equal(TimeSpan.FromHours(24), delays.Pending[0].Delay);
        delays.RunNext();
        Assert.Equal(2, updater.Checks);
    }

    [Fact]
    public async Task AutoUpdateDownloadsStagesForQuitAndToastsOncePerVersion()
    {
        updater.Next = Update();
        using var coordinator = Coordinator();
        Assert.Equal(UpdateOutcome.StagedForQuit, await coordinator.CheckAsync(interactive: false));
        Assert.Equal(1, updater.Downloads);
        Assert.Equal(1, updater.AppliedOnQuit);
        Assert.Equal(0, updater.AppliedAndRestarted);
        Assert.Equal((Notifications.Title, "An update to Connector Control is ready and will install when you quit.", (string?)null), notifier.Sent[0]);
        Assert.Equal("1.3.0", coordinator.NotifiedVersion);
        Assert.Empty(dialogs.Offers);

        Assert.Equal(UpdateOutcome.StagedForQuit, await coordinator.CheckAsync(interactive: false));
        Assert.Single(notifier.Sent);   // same version: no second toast

        updater.Next = Update("1.4.0");
        await coordinator.CheckAsync(interactive: false);
        Assert.Equal(2, notifier.Sent.Count);
    }

    [Fact]
    public async Task TheSamePendingUpdateIsStagedOnlyOnce()
    {
        updater.Next = Update();
        using var coordinator = Coordinator();
        await coordinator.CheckAsync(interactive: false);
        await coordinator.CheckAsync(interactive: false);   // tomorrow's check finds the same update
        Assert.Equal(1, updater.Downloads);
        Assert.Equal(1, updater.AppliedOnQuit);
        Assert.Equal("1.3.0", coordinator.StagedVersion);

        updater.Next = Update("1.4.0");
        await coordinator.CheckAsync(interactive: false);
        Assert.Equal(2, updater.Downloads);
        Assert.Equal(2, updater.AppliedOnQuit);
        Assert.Equal("1.4.0", coordinator.StagedVersion);
    }

    [Fact]
    public async Task ManualCheckWithNothingAvailableSaysUpToDate()
    {
        updater.VersionDisplay = "1.2.2";
        using var coordinator = Coordinator();
        Assert.Equal(UpdateOutcome.UpToDate, await coordinator.CheckAsync(interactive: true));
        Assert.Equal(new FakeDialogs.InformCall("You're up to date.", "Connector Control 1.2.2 is currently the newest version available."), dialogs.Informs[0]);
    }

    [Fact]
    public async Task BackgroundCheckWithNothingAvailableIsSilent()
    {
        using var coordinator = Coordinator();
        Assert.Equal(UpdateOutcome.UpToDate, await coordinator.CheckAsync(interactive: false));
        Assert.Empty(dialogs.Informs);
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task ManualCheckOffersTheUpdateAndInstallsOnAccept()
    {
        updater.Next = Update();
        updater.VersionDisplay = "1.2.2";
        dialogs.NextOffer = true;
        using var coordinator = Coordinator();
        Assert.Equal(UpdateOutcome.Installing, await coordinator.CheckAsync(interactive: true));
        Assert.Equal(new FakeDialogs.OfferCall("1.3.0", "1.2.2", "## Fixes\n- one"), dialogs.Offers[0]);
        Assert.Equal(1, updater.Downloads);
        Assert.Equal(1, updater.AppliedAndRestarted);
        Assert.Equal(0, updater.AppliedOnQuit);
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task LaterDefersWithoutDownloading()
    {
        updater.Next = Update();
        dialogs.NextOffer = false;
        using var coordinator = Coordinator();
        Assert.Equal(UpdateOutcome.Deferred, await coordinator.CheckAsync(interactive: true));
        Assert.Equal(0, updater.Downloads);
    }

    [Fact]
    public async Task AutoUpdateOffShowsTheDialogEvenForABackgroundCheck()
    {
        settings.AutoUpdate = false;
        updater.Next = Update();
        dialogs.NextOffer = false;
        using var coordinator = Coordinator();
        Assert.Equal(UpdateOutcome.Deferred, await coordinator.CheckAsync(interactive: false));
        Assert.Single(dialogs.Offers);
        Assert.Equal(0, updater.AppliedOnQuit);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task CheckFailureInformsOnlyWhenInteractive(bool interactive, int informs)
    {
        updater.CheckFailure = new HttpRequestException("offline");
        using var coordinator = Coordinator();
        Assert.Equal(UpdateOutcome.Failed, await coordinator.CheckAsync(interactive));
        Assert.Equal(informs, dialogs.Informs.Count);
        if (interactive)
        {
            Assert.Equal(new FakeDialogs.InformCall("Couldn’t check for updates.", "offline"), dialogs.Informs[0]);
        }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task ADownloadFailureIsHandledAndNothingIsStaged(bool interactive, int informs)
    {
        updater.Next = Update();
        updater.DownloadFailure = new HttpRequestException("connection reset");
        dialogs.NextOffer = true;
        using var coordinator = Coordinator();
        Assert.Equal(UpdateOutcome.Failed, await coordinator.CheckAsync(interactive));
        Assert.Equal(0, updater.AppliedOnQuit);
        Assert.Equal(0, updater.AppliedAndRestarted);
        Assert.Null(coordinator.StagedVersion);
        Assert.Equal(informs, dialogs.Informs.Count);
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public void DialogStringsMatchTheSpec()
    {
        Assert.Equal("A new version of Connector Control is available!", UpdateCoordinator.AvailableHeadline);
        Assert.Equal("Connector Control 1.3.0 is now available — you have 1.2.2. Would you like to install it now?", UpdateCoordinator.AvailableDetail("1.3.0", "1.2.2"));
        Assert.Equal("Install and Relaunch", UpdateCoordinator.InstallButton);
        Assert.Equal("Later", UpdateCoordinator.LaterButton);
    }
}
