using ConnectorControl.App.Services;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.App.Tests;

public class SingleInstanceTests
{
    [Fact]
    public void ASecondLaunchIsNotTheOwnerAndAsksTheFirstToShowItsFlyout()
    {
        // Private names: the shipped app may be running on this machine and would otherwise
        // own the production mutex, making the first instance here look like a second launch.
        var suffix = "." + Guid.NewGuid().ToString("N");
        using var first = new SingleInstance(suffix);
        Assert.True(first.IsFirstInstance);
        using var shown = new ManualResetEventSlim();
        first.OnShowRequested(shown.Set);

        using (var second = new SingleInstance(suffix))
        {
            Assert.False(second.IsFirstInstance);
            second.SignalShow();
        }
        Assert.True(shown.Wait(Wait.Eventually, TestContext.Current.CancellationToken));
        Assert.False(SingleInstance.IsToastActivation([]));   // a plain double-click carries no arguments
    }

    [Fact]
    public void TheUninstallCleanupHoldsTheLockSoAnAppStartedMeanwhileLeaves()
    {
        // A relay that gets a call while the uninstall hook runs would start the app; holding the lock makes that
        // app a second instance, which a background launch ends without a word, before it touches anything.
        var suffix = "." + Guid.NewGuid().ToString("N");
        bool? startedAppIsFirst = null;
        var logged = new List<string>();
        UninstallCleanup.Run(() => SingleInstance.Hold(suffix, TimeSpan.FromSeconds(5)), () =>
        {
            using var started = new SingleInstance(suffix);
            startedAppIsFirst = started.IsFirstInstance;
        }, logged.Add);
        Assert.False(startedAppIsFirst);
        Assert.Empty(logged);
        using var later = new SingleInstance(suffix);
        Assert.True(later.IsFirstInstance, "the lock is let go once the cleanup is done");
    }

    [Fact]
    public void AnUninstallCleanupThatCannotTakeTheLockSaysSoAndStillRuns()
    {
        var suffix = "." + Guid.NewGuid().ToString("N");
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var running = new SingleInstance(suffix);
            holding.Set();
            release.Wait();
        });
        holder.Start();
        try
        {
            Assert.True(holding.Wait(Wait.Eventually, TestContext.Current.CancellationToken));
            var ran = false;
            var logged = new List<string>();
            UninstallCleanup.Run(() => SingleInstance.Hold(suffix, TimeSpan.FromMilliseconds(50)), () => ran = true, logged.Add);
            Assert.True(ran);
            Assert.Single(logged);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    [Fact]
    public void ALockThatThrowsStillLetsTheUninstallCleanupRun()
    {
        var ran = false;
        var logged = new List<string>();
        UninstallCleanup.Run(() => throw new IOException("no lock"), () => ran = true, logged.Add);
        Assert.True(ran);
        Assert.Contains("no lock", Assert.Single(logged));
    }

    [Fact]
    public void TheShippedNamesAreSessionLocalAndStable()
    {
        // Local\ scopes both objects to the logon session (one tray icon per user, not per
        // machine); the names are what every installed build agrees on, so they must not drift.
        Assert.Equal(@"Local\ConnectorControl.SingleInstance", SingleInstance.MutexName);
        Assert.Equal(@"Local\ConnectorControl.ShowFlyout", SingleInstance.ShowEventName);
    }

    [Theory]
    [InlineData(new[] { "-ToastActivated" }, true)]
    [InlineData(new[] { "-toastactivated" }, true)]
    [InlineData(new[] { "-Toast" }, false)]
    [InlineData(new[] { "--something-else" }, false)]
    public void ToastActivationIsRecognisedSoItStaysSilent(string[] arguments, bool expected) =>
        Assert.Equal(expected, SingleInstance.IsToastActivation(arguments));

    [Theory]
    [InlineData(new[] { "--background" }, true)]
    [InlineData(new[] { "--Background" }, true)]
    [InlineData(new[] { "-background" }, false)]
    [InlineData(new string[0], false)]
    public void ABackgroundLaunchIsRecognisedSoItStaysSilent(string[] arguments, bool expected) =>
        Assert.Equal(expected, SingleInstance.IsBackgroundLaunch(arguments));
}
