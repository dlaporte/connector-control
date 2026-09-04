using ConnectorControl.App.Services;
using ConnectorControl.Core.Services;

namespace ConnectorControl.App.Tests;

public class ToastNotifierTests
{
    [Theory]
    [InlineData("action=restartClaude", true)]
    [InlineData("action=restartClaude;extra=1", true)]
    [InlineData("action=somethingElse", false)]
    [InlineData("", false)]
    public void RecognizesTheRestartActivationArgument(string argument, bool expected)
    {
        Assert.Equal(expected, ToastNotifier.IsRestartActivation(argument));
    }

    [Fact]
    public void ConstructionAndDisposalDoNotThrowOnThisMachine()
    {
        // Hooks toast activation for an unpackaged app; must degrade gracefully on a bare runner.
        using var notifier = new ToastNotifier(a => a());
        Assert.NotNull(notifier);
    }

    [Fact]
    public void RestartEventIsRaisedThroughMarshal()
    {
        var marshalled = 0;
        var raised = 0;
        using var notifier = new ToastNotifier(a => { marshalled++; a(); });
        notifier.RestartActionActivated += () => raised++;
        notifier.HandleActivation("action=" + Notifications.RestartAction);
        notifier.HandleActivation("action=other");
        Assert.Equal(1, marshalled);
        Assert.Equal(1, raised);
    }
}
