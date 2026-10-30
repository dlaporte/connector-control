using ConnectorControl.Core.State;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core;

/// <summary>
/// Windows only: the Mac has no uninstall hook; its README says to turn the integration off before
/// deleting the app.
///
/// What the uninstaller takes away: Connector Control's own entry from every Claude config the app may have
/// written - only when it is exactly as the app writes it, so a connector of that name a person made stays -
/// the copy's folder from the user PATH, and the copy itself.
/// </summary>
public static class ClaudeIntegrationCleanup
{
    /// <summary>Every Claude config the app may have written: the one a development run names, the one Settings
    /// names, the Roaming one, and each MSIX package's.</summary>
    public static IReadOnlyList<string> CandidateConfigs(PathContext paths, PathOverrides overrides)
    {
        var found = new List<string>();
        void Add(string? path)
        {
            if (path is { Length: > 0 } && !found.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(path);
            }
        }
        Add(paths.Environment.GetValueOrDefault(AppPaths.ClaudeConfigEnv));
        Add(overrides.ClaudeConfigPath);
        Add(Path.Combine(paths.Folders.RoamingAppData, "Claude", "claude_desktop_config.json"));
        foreach (var candidate in AppPaths.MsixClaudeConfigCandidates(paths.Folders, paths.Probe))
        {
            Add(candidate);
        }
        return found;
    }

    /// <summary>Removes the app's entry from <paramref name="configPath"/> when it is there as the app writes it,
    /// once <paramref name="backUp"/> has copied the file; true when it removed it. No file, an entry of that name
    /// that runs anything else: the file is left as it is, and nothing is said. A malformed file, a backup that
    /// fails or a write that fails: left as it is too, and said to <paramref name="failed"/>, since the entry may
    /// still be there. An entry that is the app's but carries other keys, such as <c>env</c>, is still removed: it
    /// runs a copy that is being deleted.</summary>
    public static bool RemoveEntry(string configPath, string toolPath, Action<string>? backUp = null, Action<string>? failed = null) =>
        RemoveEntry(configPath, toolPath, backUp, failed, ClaudeConfigIO.Write);

    /// <summary><paramref name="write"/> saves the config, as <see cref="ClaudeConfigIO.Write"/> does: a test's way to a
    /// write that fails on any platform.</summary>
    internal static bool RemoveEntry(string configPath, string toolPath, Action<string>? backUp, Action<string>? failed,
        Action<IReadOnlyDictionary<string, JsonValue>, string> write)
    {
        try
        {
            var servers = ClaudeConfigIO.ReadMcpServers(configPath);
            if (!servers.TryGetValue(ClaudeIntegration.EntryName, out var entry) || !IsTheAppsEntry(entry, toolPath))
            {
                return false;
            }
            backUp?.Invoke(configPath);
            var kept = servers.Where(server => server.Key != ClaudeIntegration.EntryName).ToDictionary(server => server.Key, server => server.Value, StringComparer.Ordinal);
            write(kept, configPath);
            return true;
        }
        catch (Exception ex) when (ex is ClaudeConfigException or IOException or UnauthorizedAccessException)
        {
            failed?.Invoke($"Connector Control's entry may still be in {configPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Everything the uninstaller takes away, as <see cref="ClaudeIntegrationCleanup"/> says. The app's record
    /// of itself is deleted before anything else, so nothing starts the app again. Each config is backed up into the
    /// app's backups folder before it is changed, keeping as many backups as the person chose. The copy goes before
    /// the PATH entry: Velopack ends the hook after 60 seconds, and the PATH change waits on every window's answer to
    /// its broadcast. The configs are swept once more as the last step: an app a relay started before the hook took
    /// the single-instance lock may have put the entry back meanwhile. Each step stands alone: one that fails is said
    /// to <paramref name="failed"/> - the app's log - and never keeps another from running, since nobody will see the
    /// failure and this runs once.</summary>
    public static void Uninstall(PathContext paths, PathOverrides overrides, string dataDir, IUserEnvironment environment, int keepCount, Action<string>? failed = null) =>
        Uninstall(paths, overrides, dataDir, environment, keepCount, failed, ControlToolCopy.Remove, ClaudeConfigIO.Write);

    /// <summary><paramref name="removeCopy"/> deletes the copy, as <see cref="ControlToolCopy.Remove"/> does: a test's
    /// way to a copy step that throws; <paramref name="write"/> saves a config, as <see cref="ClaudeConfigIO.Write"/> does.</summary>
    internal static void Uninstall(PathContext paths, PathOverrides overrides, string dataDir, IUserEnvironment environment, int keepCount,
        Action<string>? failed, Action<string> removeCopy, Action<IReadOnlyDictionary<string, JsonValue>, string>? write = null)
    {
        write ??= ClaudeConfigIO.Write;
        var tool = ControlToolCopy.ToolPath(dataDir);
        var bin = ControlToolCopy.BinDir(dataDir);
        var record = Path.Combine(bin, ControlEndpoint.AppPathFileName);
        // First, before any config is changed: Velopack stops the app before it runs this hook, and a relay that
        // gets a call in between would start it again from this record - putting back the entry this hook takes
        // away, and holding current\ open. Without the record the copy finds no app to start.
        Step(failed, $"The app's record could not be deleted from {bin}", () =>
        {
            if (File.Exists(record))
            {
                File.Delete(record);
            }
        });
        IReadOnlyList<string> configs = [];
        BackupManager? backups = null;
        Step(failed, "The Claude configs could not be listed", () => configs = CandidateConfigs(paths, overrides));
        Step(failed, "The backups folder could not be resolved", () =>
            backups = new BackupManager(AppPaths.Resolve(paths.Environment, overrides, paths.Folders, paths.Probe).BackupsDir, keepCount));
        void RemoveEntries()
        {
            foreach (var config in configs)
            {
                if (backups is null)
                {
                    // A config is never changed without its backup.
                    failed?.Invoke($"Connector Control's entry may still be in {config}: there was nowhere to back it up.");
                    continue;
                }
                var backUps = backups;
                Step(failed, $"Connector Control's entry may still be in {config}", () =>
                    RemoveEntry(config, tool, path => backUps.BackUp(path, RestoreModel.Series), failed, write));
            }
        }
        RemoveEntries();
        Step(failed, $"The command-line tool's copy could not be removed from {bin}", () =>
        {
            removeCopy(dataDir);
            // A copy Claude's relay is still running cannot be deleted; nothing will start it again.
            if (File.Exists(tool) || Directory.Exists(bin))
            {
                failed?.Invoke($"The command-line tool's copy in {bin} could not be deleted.");
            }
        });
        Step(failed, "Connector Control's folder could not be taken out of the user PATH", () =>
        {
            var userPath = environment.ReadPath();
            if (UserPath.Contains(userPath, bin))
            {
                environment.WritePath(UserPath.Removing(userPath, bin));
            }
        });
        // Last: an entry put back while the steps above ran goes again. A config that is already clean is only read.
        RemoveEntries();
    }

    private static void Step(Action<string>? failed, string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            failed?.Invoke($"{what}: {ex.Message}");
        }
    }

    internal static bool IsTheAppsEntry(JsonValue entry, string toolPath) =>
        entry.Kind == JsonKind.Object
        && entry["command"] is { Kind: JsonKind.String } command
        && string.Equals(command.StringValue, toolPath, StringComparison.OrdinalIgnoreCase)
        && entry["args"] is { Kind: JsonKind.Array } args
        && args.ArrayItems.Length == 1 && args.ArrayItems[0] == JsonValue.String(ClaudeIntegration.RelayArgument);
}
