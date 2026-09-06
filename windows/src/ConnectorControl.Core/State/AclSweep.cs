using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.State;

/// <summary>
/// The Mac sweepPermissionsOnce (catalog §1.15) with a DACL instead of chmod:
/// one-time repair of files written before owner-only permissions were
/// enforced, gated by the aclSweepDone setting so launches stay cheap.
/// Every error is ignored, like the Swift try?.
/// </summary>
public static class AclSweep
{
    /// <summary>True when the sweep ran (first time only).</summary>
    public static bool RunOnce(ISettings settings, AppPaths paths)
    {
        if (settings.AclSweepDone)
        {
            return false;
        }
        foreach (var root in new[] { paths.StoreDir, paths.BackupsDir })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            OwnerOnlyAcl.TryApply(root);
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
                {
                    OwnerOnlyAcl.TryApply(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }
        settings.AclSweepDone = true;
        return true;
    }
}
