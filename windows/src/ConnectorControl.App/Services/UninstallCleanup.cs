using ConnectorControl.Core;
using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Services;

/// <summary>
/// What Velopack runs before it uninstalls the app (<see cref="ClaudeIntegrationCleanup.Uninstall"/> over the live
/// paths, the settings' overrides and the registry). Velopack allows it 30 seconds and then ends the process, so
/// it waits on nothing slow and never throws: an uninstall must not fail for what it tidies, and the worst left
/// behind is an entry Claude shows as failed.
/// </summary>
internal static class UninstallCleanup
{
    public static void Run()
    {
        try
        {
            var settings = new SettingsStore(ServiceFactory.DefaultSettingsPath);
            ClaudeIntegrationCleanup.Uninstall(PathContext.Live(), new PathOverrides(settings.ClaudeConfigPath, settings.MasterStoreDir),
                ServiceFactory.DefaultDataDir, new RegistryUserEnvironment(), settings.BackupKeepCount, App.Log);
        }
        catch (Exception ex)
        {
            // said to the app's log, which never throws; the uninstall carries on
            App.Log($"The uninstall cleanup did not finish: {ex}");
        }
    }
}
