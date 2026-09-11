using ConnectorControl.Core;
using ConnectorControl.Core.Services;

namespace ConnectorControl.App.Services;

/// <summary>Composition root for the platform services.</summary>
public static class ServiceFactory
{
    public static string DefaultDataDir =>
        Path.Combine(KnownFolders.Current().LocalAppData, AppPaths.DataDirName);

    public static string DefaultSettingsPath => Path.Combine(DefaultDataDir, "settings.json");

    /// <param name="marshal">Runs an action on the UI thread (the WPF Dispatcher in the app; inline in tests).</param>
    public static PlatformServices CreateDefault(Action<Action> marshal)
    {
        var settings = new SettingsStore(DefaultSettingsPath);
        var install = new ClaudeInstall(KnownFolders.Current(), new RealPathProbe());
        var process = new ClaudeProcess(install.Detect, () => settings.ClaudeLaunchTarget);
        return new PlatformServices(
            settings,
            install,
            process,
            new ToastNotifier(marshal),
            new RegistryAutostart(),
            new VelopackUpdater());
    }
}
