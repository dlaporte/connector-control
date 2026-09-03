using System.Windows;

namespace ConnectorControl.App;

/// <summary>
/// Phase 0 stub: proves the WPF project builds. Phase 3 replaces this with the
/// tray application. It exits immediately so an accidental launch does nothing.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Shutdown();
    }
}
