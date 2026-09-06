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
    public void RestartToastCarriesTheButtonAndTheAction()
    {
        var xml = ToastNotifier.Build(Notifications.Title, "Apply finished.", Notifications.RestartCategory)
            .GetToastContent().GetContent();
        Assert.Contains("Connector Control", xml, StringComparison.Ordinal);
        Assert.Contains("Apply finished.", xml, StringComparison.Ordinal);
        Assert.Contains("Restart Claude", xml, StringComparison.Ordinal);
        Assert.Contains("action=restartClaude", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("somethingElse")]
    public void OtherCategoriesGetNoButton(string? category)
    {
        var xml = ToastNotifier.Build(Notifications.Title, "Backups pruned.", category)
            .GetToastContent().GetContent();
        Assert.Contains("Backups pruned.", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<action", xml, StringComparison.Ordinal);
        Assert.DoesNotContain(Notifications.RestartButton, xml, StringComparison.Ordinal);
        Assert.DoesNotContain(Notifications.RestartAction, xml, StringComparison.Ordinal);
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
