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
        Assert.True(shown.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(SingleInstance.IsToastActivation([]));   // a plain double-click carries no arguments
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
}
