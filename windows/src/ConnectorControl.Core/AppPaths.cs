namespace ConnectorControl.Core;

/// <summary>
/// Where the app reads and writes, plus resolution of the defaults: Windows edition of Swift
/// <c>AppPaths.live</c> plus the Mac app's custom-store rule: env overrides first, then
/// settings, then defaults. Claude's config can exist both inside an MSIX package's
/// virtualized AppData and at the real AppData path — a machine that upgraded from an older
/// MSIX build to a current one can have both, with the MSIX copy stale. Since Claude rewrites
/// its config on every startup, the file with the newest last-write time is the live one; when
/// neither exists, or both are exactly as old as each other, we fall back to the real AppData
/// (Roaming) path.
/// </summary>
public sealed record AppPaths(string ClaudeConfigPath, string StoreDir, string BackupsDir)
{
    /// <summary>
    /// A folder name, not a display string: it happens to match <see cref="Product.Name"/> today,
    /// but must not be rewired to it — every existing install's data already lives under this
    /// exact folder, and a rename here would silently orphan it.
    /// </summary>
    public const string DataDirName = "Connector Control";
    public const string ClaudeConfigEnv = "CONNECTOR_CONTROL_CLAUDE_CONFIG";
    public const string StoreDirEnv = "CONNECTOR_CONTROL_STORE_DIR";

    public AppPaths(string claudeConfigPath, string storeDir)
        : this(claudeConfigPath, storeDir, Path.Combine(storeDir, "backups"))
    {
    }

    public string MasterStorePath => Path.Combine(StoreDir, "mcps.json");

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
    /// Every LocalCache config path a Claude MSIX package folder could produce, in ordinal
    /// folder-name order. Existence of the file or the package folder is not implied — callers
    /// probe for that.
    /// </summary>
    internal static IReadOnlyList<string> MsixClaudeConfigCandidates(KnownFolders folders, IPathProbe probe) =>
        ClaudePackage.PackageFolders(probe, folders)
            .Select(dir => Path.Combine(dir, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"))
            .ToList();
}
