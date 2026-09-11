using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.State;

/// <summary>
/// The Mac sweepPermissionsOnce (catalog §1.15) with a DACL instead of chmod:
/// one-time repair of files written before owner-only permissions were
/// enforced, gated by the SweepVersion setting so launches stay cheap.
/// Every error is ignored, like the Swift try?.
/// </summary>
/// <remarks>
/// The sweep touches only what this app writes. A protected owner-only DACL
/// is not additive — it strips inheritance, SYSTEM, Administrators and any
/// sharing the owner set up — and the store directory can be a folder the
/// user chose (OneDrive, Documents, a repo checkout). So in the store
/// directory only mcps.json and the corrupt-file asides beside it are
/// repaired, never anything else in the folder and never anything below it,
/// and the folder's own DACL is rewritten only while it is the app's default
/// location. The backups directory is always the app's own (machine-local,
/// never the synced folder), so everything under it is the app's to repair.
/// A drive root or a well-known shell folder is refused outright, whatever
/// role it was given.
///
/// Windows has always swept with a DACL, so there is only the one pass
/// (unlike the Mac's mode-then-ACL history): CurrentVersion is 1, and there
/// is no migration off the old boolean AclSweepDone flag — the sweep is
/// idempotent and cheap, so an upgraded install simply re-runs it once more
/// under the new key.
/// </remarks>
public static class PermissionsSweep
{
    /// <summary>Bump this, and add the new pass's check below, to add a pass.</summary>
    public const int CurrentVersion = 1;

    /// <summary>True when the sweep ran (first time only).</summary>
    public static bool RunOnce(ISettings settings, AppPaths paths) =>
        RunOnce(settings, paths, ProtectedRoots());

    internal static bool RunOnce(ISettings settings, AppPaths paths, IReadOnlyCollection<string> protectedRoots)
    {
        if (settings.SweepVersion >= CurrentVersion)
        {
            return false;
        }
        var attempted = 0;
        var applied = 0;
        void Apply(string path)
        {
            attempted++;
            if (OwnerOnlyAcl.TryApply(path))
            {
                applied++;
            }
        }

        var storeDir = paths.StoreDir;
        if (Directory.Exists(storeDir) && !IsProtected(storeDir, protectedRoots))
        {
            if (string.IsNullOrEmpty(settings.MasterStoreDir))
            {
                Apply(storeDir);
            }
            foreach (var file in EnumerateSafely(storeDir, SearchOption.TopDirectoryOnly))
            {
                if (IsStoreFile(Path.GetFileName(file)) && File.Exists(file))
                {
                    Apply(file);
                }
            }
        }

        var backupsDir = paths.BackupsDir;
        if (Directory.Exists(backupsDir) && !IsProtected(backupsDir, protectedRoots))
        {
            Apply(backupsDir);
            foreach (var entry in EnumerateSafely(backupsDir, SearchOption.AllDirectories))
            {
                Apply(entry);
            }
        }

        // A sweep that tried and achieved nothing is not done: leave the version behind so the
        // next launch tries again, instead of recording success.
        if (attempted == 0 || applied > 0)
        {
            settings.SweepVersion = CurrentVersion;
        }
        return true;
    }

    /// <summary>mcps.json and the <c>mcps.corrupt.&lt;timestamp&gt;.json</c> asides MasterStoreIO leaves beside it.</summary>
    internal static bool IsStoreFile(string name) =>
        string.Equals(name, "mcps.json", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith("mcps.corrupt.", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    /// <summary>A drive root, or one of the user's shell folders, is never rewritten.</summary>
    internal static bool IsProtected(string path, IReadOnlyCollection<string> protectedRoots)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full);
        if (root is not null && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        foreach (var candidate in protectedRoots)
        {
            if (candidate.Length > 0
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)), full, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The user's profile and the shell folders most likely to be chosen by mistake, plus OneDrive.</summary>
    private static IReadOnlyCollection<string> ProtectedRoots()
    {
        var roots = new List<string>();
        foreach (var folder in new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
        })
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length > 0)
            {
                roots.Add(path);
            }
        }
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } oneDrive)
            {
                roots.Add(oneDrive);
            }
        }
        return roots;
    }

    private static IEnumerable<string> EnumerateSafely(string root, SearchOption option)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(root, "*", option).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];   // best effort
        }
    }
}
