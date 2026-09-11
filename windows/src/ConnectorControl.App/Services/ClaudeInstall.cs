using System.Runtime.InteropServices;
using ConnectorControl.Core;
using ConnectorControl.Core.Services;
using Windows.Management.Deployment;

namespace ConnectorControl.App.Services;

/// <summary>
/// Detects how Claude Desktop is installed. The WinRT package
/// manager is authoritative (it gives the AUMID and the install location); a
/// scan of %LOCALAPPDATA%\Packages (the folder name IS the package family
/// name) stands in only when that API throws, never when it simply reports no
/// Claude package. Otherwise the legacy Squirrel exe decides.
/// </summary>
public sealed class ClaudeInstall : IClaudeInstall
{
    private const string AppIdSuffix = "!Claude";
    private readonly KnownFolders folders;
    private readonly IPathProbe probe;

    public ClaudeInstall(KnownFolders folders, IPathProbe probe)
    {
        this.folders = folders;
        this.probe = probe;
    }

    public ClaudeInstallInfo Detect() => Detect(DetectMsixViaPackageManager);

    /// <param name="lookUpMsix">The WinRT package query; the tests substitute its three outcomes.</param>
    internal ClaudeInstallInfo Detect(Func<MsixLookup> lookUpMsix)
    {
        var msix = lookUpMsix();
        if (msix.Info is not null)
        {
            return msix.Info;
        }
        if (!msix.Available && DetectMsixByFolderScan() is { } scanned)
        {
            return scanned;   // scan only when the API itself failed
        }
        var legacyExe = Path.Combine(folders.LocalAppData, "AnthropicClaude", "claude.exe");
        if (probe.FileExists(legacyExe))
        {
            return new ClaudeInstallInfo(ClaudeInstallKind.Legacy, null, legacyExe, ClaudeInstallInfo.DefaultProcessName, Path.GetDirectoryName(legacyExe));
        }
        return ClaudeInstallInfo.NotFound;
    }

    /// <summary>
    /// The outcome of the WinRT package query. <c>Available</c> is false only when
    /// the API itself failed: only then does <see cref="Detect"/> fall back to the
    /// folder scan, because a %LOCALAPPDATA%\Packages folder left behind by an uninstalled MSIX
    /// Claude would otherwise shadow a working legacy install with a launch target
    /// nothing can open.
    /// </summary>
    internal readonly record struct MsixLookup(bool Available, ClaudeInstallInfo? Info)
    {
        internal static MsixLookup Unavailable => new(false, null);

        internal static MsixLookup NotInstalled => new(true, null);

        internal static MsixLookup Found(ClaudeInstallInfo info) => new(true, info);
    }

    private static MsixLookup DetectMsixViaPackageManager()
    {
        try
        {
            return FindClaudePackage() is { } found
                ? MsixLookup.Found(new ClaudeInstallInfo(ClaudeInstallKind.Msix, found.Family, found.Aumid, ClaudeInstallInfo.DefaultProcessName, found.InstalledPath))
                : MsixLookup.NotInstalled;   // the query worked: there is no MSIX Claude
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException
            or TypeLoadException or FileNotFoundException or PlatformNotSupportedException)
        {
            return MsixLookup.Unavailable;   // WinRT unusable here: the folder scan decides
        }
    }

    /// <summary>
    /// One WinRT loop over every package for the current user, returning the first whose family
    /// name is Claude's: the family name, its best-effort AUMID (falling back to
    /// <c>family!Claude</c> when the app-list entry can't be read), and its install location when
    /// available. Null means the query worked and found no Claude package; an exception means
    /// WinRT itself is unusable — <see cref="DetectMsixViaPackageManager"/> tells those apart.
    /// </summary>
    internal static (string Family, string Aumid, string? InstalledPath)? FindClaudePackage()
    {
        var manager = new PackageManager();
        foreach (var package in manager.FindPackagesForUser(string.Empty))
        {
            var family = package.Id.FamilyName;
            if (!ClaudePackage.IsClaudeFamily(family))
            {
                continue;
            }
            string? aumid = null;
            string? installedPath = null;
            // Package.GetAppListEntries() and Package.InstalledPath require Windows
            // 10.0.19041.0+; the app's SupportedOSPlatformVersion (10.0.17763.0) is
            // lower, so both must be guarded.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                try
                {
                    var entries = package.GetAppListEntries();
                    aumid = entries.Count > 0 ? entries[0].AppUserModelId : null;
                    installedPath = package.InstalledPath;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    // app-list entry or install location unavailable (a staged or partly
                    // installed package): fall back to the conventional id below, and process
                    // matching falls back to the name alone
                }
            }
            return (family, aumid ?? family + AppIdSuffix, installedPath);
        }
        return null;
    }

    /// <summary>
    /// Fallback when WinRT is unavailable: package folders are named by family name.
    /// The install directory cannot be derived from the family name, so it is left
    /// unknown and Claude's processes are matched by name alone.
    /// </summary>
    internal ClaudeInstallInfo? DetectMsixByFolderScan()
    {
        var family = ClaudePackage.PackageFolders(probe, folders).Select(Path.GetFileName).FirstOrDefault();
        return family is null
            ? null
            : new ClaudeInstallInfo(ClaudeInstallKind.Msix, family, family + AppIdSuffix, ClaudeInstallInfo.DefaultProcessName);
    }
}
