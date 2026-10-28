using ConnectorControl.Core.State;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core;

/// <summary>
/// Windows only: the Mac has no uninstall hook; its README says to turn the integration off before
/// deleting the app.
///
/// What the uninstaller takes away: Connector Control's own entry from every Claude config the app may have
/// written — only when it is exactly as the app writes it, so a connector of that name a person made stays —
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
    /// once <paramref name="backUp"/> has copied the file; true when it removed it. No file, a malformed one, an
    /// entry of that name that runs anything else, or a backup that fails: the file is left as it is.</summary>
    public static bool RemoveEntry(string configPath, string toolPath, Action<string>? backUp = null)
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
            ClaudeConfigIO.Write(kept, configPath);
            return true;
        }
        catch (Exception ex) when (ex is ClaudeConfigException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Everything the uninstaller takes away, as <see cref="ClaudeIntegrationCleanup"/> says. Each config is
    /// backed up into the app's backups folder first, keeping as many backups as the person chose. The copy goes
    /// before the PATH entry: Velopack ends the hook after 30 seconds, and the PATH change waits on every window's
    /// answer to its broadcast.</summary>
    public static void Uninstall(PathContext paths, PathOverrides overrides, string dataDir, IUserEnvironment environment, int keepCount)
    {
        var tool = ControlToolCopy.ToolPath(dataDir);
        var backups = new BackupManager(AppPaths.Resolve(paths.Environment, overrides, paths.Folders, paths.Probe).BackupsDir, keepCount);
        foreach (var config in CandidateConfigs(paths, overrides))
        {
            RemoveEntry(config, tool, path => backups.BackUp(path, RestoreModel.Series));
        }
        ControlToolCopy.Remove(dataDir);
        var bin = ControlToolCopy.BinDir(dataDir);
        var userPath = environment.ReadPath();
        if (UserPath.Contains(userPath, bin))
        {
            environment.WritePath(UserPath.Removing(userPath, bin));
        }
    }

    internal static bool IsTheAppsEntry(JsonValue entry, string toolPath) =>
        entry.Kind == JsonKind.Object
        && entry["command"] is { Kind: JsonKind.String } command
        && string.Equals(command.StringValue, toolPath, StringComparison.OrdinalIgnoreCase)
        && entry["args"] is { Kind: JsonKind.Array } args
        && args.ArrayItems.Length == 1 && args.ArrayItems[0] == JsonValue.String(ClaudeIntegration.RelayArgument);
}
