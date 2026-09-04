namespace ConnectorControl.Core;

/// <summary>
/// Windows edition of Swift <c>AppPaths.live</c> plus the Mac app's custom-store
/// rule: env overrides first, then settings, then defaults. Claude's config is
/// found inside the MSIX package's virtualized AppData when Claude is installed
/// that way (its own "Edit Config" button opens the wrong file — see spec §4.1).
/// </summary>
public static class AppPathsResolver
{
    public const string DataDirName = "Connector Control";
    public const string ClaudeConfigEnv = "CONNECTOR_CONTROL_CLAUDE_CONFIG";
    public const string StoreDirEnv = "CONNECTOR_CONTROL_STORE_DIR";

    public static AppPaths Resolve(
        IReadOnlyDictionary<string, string> environment,
        PathOverrides overrides,
        KnownFolders folders,
        IPathProbe probe)
    {
        var defaultStore = Path.Combine(folders.LocalAppData, DataDirName);

        string claude;
        if (environment.TryGetValue(ClaudeConfigEnv, out var envClaude) && envClaude.Length > 0)
        {
            claude = envClaude;
        }
        else if (overrides.ClaudeConfigPath is { Length: > 0 } settingClaude)
        {
            claude = settingClaude;
        }
        else
        {
            claude = ResolveMsixClaudeConfig(folders, probe)
                ?? Path.Combine(folders.RoamingAppData, "Claude", "claude_desktop_config.json");
        }

        if (environment.TryGetValue(StoreDirEnv, out var envStore) && envStore.Length > 0)
        {
            return new AppPaths(claude, envStore);                     // backups under it (dev sandbox)
        }
        if (overrides.MasterStoreDir is { Length: > 0 } customStore)
        {
            // Backups never follow a custom (possibly synced) store dir.
            return new AppPaths(claude, customStore, Path.Combine(defaultStore, "backups"));
        }
        return new AppPaths(claude, defaultStore);
    }

    /// <summary>
    /// The config file a virtualized MSIX Claude actually reads, when that file
    /// exists (it shadows the real AppData file); null otherwise — current
    /// Claude builds write the real AppData path.
    /// </summary>
    public static string? ResolveMsixClaudeConfig(KnownFolders folders, IPathProbe probe)
    {
        var packages = Path.Combine(folders.LocalAppData, "Packages");
        if (!probe.DirectoryExists(packages))
        {
            return null;
        }
        var candidates = probe.EnumerateDirectories(packages)
            .Where(dir =>
            {
                var name = Path.GetFileName(dir);
                return name.StartsWith("Claude_", StringComparison.Ordinal)
                    || name.StartsWith("Anthropic.Claude", StringComparison.Ordinal);
            })
            .OrderBy(dir => dir, StringComparer.Ordinal)
            .Select(dir => Path.Combine(dir, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"))
            .ToList();
        return candidates.FirstOrDefault(probe.FileExists);
    }
}
