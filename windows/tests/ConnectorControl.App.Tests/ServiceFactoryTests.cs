using ConnectorControl.App.Services;

namespace ConnectorControl.App.Tests;

public class ServiceFactoryTests
{
    [Fact]
    public void DefaultPathsLiveUnderLocalAppData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(local, "Connector Control"), ServiceFactory.DefaultDataDir);
        Assert.Equal(Path.Combine(local, "Connector Control", "settings.json"), ServiceFactory.DefaultSettingsPath);
    }

    [Fact]
    public void CreateDefaultWiresEveryService()
    {
        var services = ServiceFactory.CreateDefault(a => a());
        Assert.NotNull(services.Settings);
        Assert.NotNull(services.ClaudeInstall);
        Assert.NotNull(services.ClaudeProcess);
        Assert.NotNull(services.Notifier);
        Assert.NotNull(services.Autostart);
        Assert.NotNull(services.Updater);
        (services.Notifier as IDisposable)?.Dispose();
    }
}
