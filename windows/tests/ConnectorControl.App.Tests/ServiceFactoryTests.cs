using ConnectorControl.App.Services;
using ConnectorControl.Core.Services;

namespace ConnectorControl.App.Tests;

public class ServiceFactoryTests
{
    private sealed class DisposableNotifier : INotifier, IDisposable
    {
        public int Disposals { get; private set; }

        public event Action? RestartActionActivated { add { } remove { } }

        public void Notify(string title, string body, string? category = null)
        {
        }

        public void Dispose() => Disposals++;
    }

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
        using var services = ServiceFactory.CreateDefault(a => a());
        Assert.NotNull(services.Settings);
        Assert.NotNull(services.ClaudeInstall);
        Assert.NotNull(services.ClaudeProcess);
        Assert.NotNull(services.Notifier);
        Assert.NotNull(services.Autostart);
        Assert.NotNull(services.Updater);
    }

    [Fact]
    public void DisposingTheServicesDisposesTheNotifierItOwns()
    {
        var notifier = new DisposableNotifier();
        using var real = ServiceFactory.CreateDefault(a => a());
        var services = real with { Notifier = notifier };
        Assert.Equal(0, notifier.Disposals);
        services.Dispose();
        Assert.Equal(1, notifier.Disposals);
    }
}
