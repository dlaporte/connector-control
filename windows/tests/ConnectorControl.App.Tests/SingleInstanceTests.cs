namespace ConnectorControl.App.Tests;

public class SingleInstanceTests
{
    [Fact]
    public void ASecondLaunchIsNotTheOwnerAndAsksTheFirstToShowItsFlyout()
    {
        using var first = new SingleInstance();
        Assert.True(first.IsFirstInstance);
        using var shown = new ManualResetEventSlim();
        first.OnShowRequested(shown.Set);

        using (var second = new SingleInstance())
        {
            Assert.False(second.IsFirstInstance);
            second.SignalShow();
        }
        Assert.True(shown.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(SingleInstance.IsToastActivation([]));   // a plain double-click carries no arguments
    }

    [Theory]
    [InlineData(new[] { "-ToastActivated" }, true)]
    [InlineData(new[] { "-toastactivated" }, true)]
    [InlineData(new[] { "-Toast" }, false)]
    [InlineData(new[] { "--something-else" }, false)]
    public void ToastActivationIsRecognisedSoItStaysSilent(string[] arguments, bool expected) =>
        Assert.Equal(expected, SingleInstance.IsToastActivation(arguments));
}
