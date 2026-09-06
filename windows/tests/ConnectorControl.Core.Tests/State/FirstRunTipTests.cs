using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class FirstRunTipTests
{
    [Fact]
    public void ShowsOnceAndRemembers()
    {
        var settings = new FakeSettings();
        var notifier = new FakeNotifier();
        Assert.True(FirstRunTip.ShowIfNeeded(settings, notifier));
        Assert.Equal((Notifications.Title, "Connector Control lives in the system tray. Drag its icon out of the overflow (^) to keep it visible.", (string?)null), notifier.Sent[0]);
        Assert.True(settings.TrayTipShown);
        Assert.False(FirstRunTip.ShowIfNeeded(settings, notifier));
        Assert.Single(notifier.Sent);
    }

    [Fact]
    public void SkipsWhenAlreadyShown()
    {
        var settings = new FakeSettings { TrayTipShown = true };
        var notifier = new FakeNotifier();
        Assert.False(FirstRunTip.ShowIfNeeded(settings, notifier));
        Assert.Empty(notifier.Sent);
    }
}
