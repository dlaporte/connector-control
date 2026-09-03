namespace ConnectorControl.Core;

/// <summary>Where the app reads and writes. Resolution of the defaults lives in <c>AppPathsResolver</c> (Task 17).</summary>
public sealed record AppPaths(string ClaudeConfigPath, string StoreDir, string BackupsDir)
{
    public AppPaths(string claudeConfigPath, string storeDir)
        : this(claudeConfigPath, storeDir, Path.Combine(storeDir, "backups"))
    {
    }

    public string MasterStorePath => Path.Combine(StoreDir, "mcps.json");
}
