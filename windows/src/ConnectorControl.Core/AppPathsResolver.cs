namespace ConnectorControl.Core;

/// <summary>
/// Windows edition of Swift <c>AppPaths.live</c> plus the Mac app's custom-store
/// rule: env overrides first, then settings, then defaults. Claude's config can
/// exist both inside an MSIX package's virtualized AppData and at the real
/// AppData path — a machine that upgraded from an older MSIX build to a current
/// one can have both, with the MSIX copy stale. Since Claude rewrites its config
/// on every startup, the file with the newest last-write time is the live one;
/// when neither exists, or both are exactly as old as each other, we fall back
/// to the real AppData (Roaming) path.
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
            claude = ResolveClaudeConfig(folders, probe);
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
    /// Picks the Claude config to use: among every MSIX LocalCache candidate
    /// plus the real AppData (Roaming) path, the one written most recently —
    /// ties, and the case where none exists, go to Roaming, since Claude
    /// always writes there once it is past the MSIX-virtualized builds.
    /// </summary>
    internal static string ResolveClaudeConfig(KnownFolders folders, IPathProbe probe)
    {
        var roaming = Path.Combine(folders.RoamingAppData, "Claude", "claude_desktop_config.json");

        // Roaming is both a candidate and the tie/fallback winner, so seed the
        // search with it and let only a strictly newer MSIX candidate beat it.
        var newestPath = roaming;
        var newestTime = probe.LastWriteTimeUtc(roaming);
        foreach (var candidate in MsixClaudeConfigCandidates(folders, probe))
        {
            var time = probe.LastWriteTimeUtc(candidate);
            if (time is { } t && (newestTime is not { } current || t > current))
            {
                newestTime = t;
                newestPath = candidate;
            }
        }
        return newestPath;
    }

    /// <summary>
    /// The config file a virtualized MSIX Claude actually reads, when that file
    /// exists (it shadows the real AppData file); null otherwise — current
    /// Claude builds write the real AppData path.
    /// </summary>
    public static string? ResolveMsixClaudeConfig(KnownFolders folders, IPathProbe probe) =>
        MsixClaudeConfigCandidates(folders, probe).FirstOrDefault(probe.FileExists);

    /// <summary>
    /// Every LocalCache config path a <c>Claude_*</c> or <c>Anthropic.Claude*</c>
    /// MSIX package folder could produce, in ordinal folder-name order. Existence
    /// of the file or the package folder is not implied — callers probe for that.
    /// </summary>
    internal static IReadOnlyList<string> MsixClaudeConfigCandidates(KnownFolders folders, IPathProbe probe)
    {
        var packages = Path.Combine(folders.LocalAppData, "Packages");
        if (!probe.DirectoryExists(packages))
        {
            return [];
        }
        return probe.EnumerateDirectories(packages)
            .Where(dir =>
            {
                var name = Path.GetFileName(dir);
                return name.StartsWith("Claude_", StringComparison.Ordinal)
                    || name.StartsWith("Anthropic.Claude", StringComparison.Ordinal);
            })
            .OrderBy(dir => dir, StringComparer.Ordinal)
            .Select(dir => Path.Combine(dir, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"))
            .ToList();
    }
}
