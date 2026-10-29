using ConnectorControl.Core;
using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Services;

/// <summary>
/// Windows only: the Mac has no uninstall hook.
///
/// What Velopack runs before it uninstalls the app (<see cref="ClaudeIntegrationCleanup.Uninstall"/> over the live
/// paths, the settings' overrides and the registry). Velopack allows it 60 seconds and then ends the process, so
/// it waits on nothing slow and never throws: an uninstall must not fail for what it tidies, and the worst left
/// behind is an entry Claude shows as failed.
/// </summary>
internal static class UninstallCleanup
{
    /// <summary>How long the hook waits for the single-instance lock: a few seconds of the 60 Velopack allows.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    public static void Run() => Run(() => SingleInstance.Hold("", LockWait), () =>
    {
        var settings = new SettingsStore(ServiceFactory.DefaultSettingsPath);
        ClaudeIntegrationCleanup.Uninstall(PathContext.Live(), new PathOverrides(settings.ClaudeConfigPath, settings.MasterStoreDir),
            ServiceFactory.DefaultDataDir, new RegistryUserEnvironment(), settings.BackupKeepCount, App.Log);
    }, App.Log);

    /// <summary>
    /// Runs <paramref name="cleanup"/> holding the app's single-instance lock, taken before any step. Velopack stops
    /// the app before the hook, but Claude's relay, which lives outside the install folder and survives, may start
    /// it again for a call in between; holding the lock makes that app a second instance, which a background launch
    /// ends without a word, before it could put back the entry this hook takes away. A lock that cannot be had —
    /// such an app got there first — is said to <paramref name="log"/>, and the cleanup still runs: its last step
    /// sweeps the configs again. So does a lock that throws: the lock only narrows a race, and the cleanup matters
    /// more. <paramref name="hold"/> takes the lock (<see cref="SingleInstance.Hold"/>; a test's keeps its name apart).
    /// </summary>
    internal static void Run(Func<Mutex?> hold, Action cleanup, Action<string> log)
    {
        Mutex? held = null;
        try
        {
            held = hold();
            if (held is null)
            {
                log("The uninstall cleanup ran without the single-instance lock: a copy of the app is running.");
            }
        }
        catch (Exception ex)
        {
            log($"The uninstall cleanup ran without the single-instance lock, which could not be taken: {ex.GetType().Name}: {ex.Message}");
        }
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            // said to the app's log, which never throws; the uninstall carries on
            log($"The uninstall cleanup did not finish: {ex}");
        }
        finally
        {
            if (held is not null)
            {
                held.ReleaseMutex();
                held.Dispose();
            }
        }
    }
}
