using System.Runtime.InteropServices;
using ConnectorControl.Core;
using ConnectorControl.Core.Services;
using Windows.Management.Deployment;

namespace ConnectorControl.App.Services;

/// <summary>
/// Detects how Claude Desktop is installed (spec §6.1). The WinRT package
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
            return scanned;   // spec §6.1: scan only when the API itself failed
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
    /// the API itself failed: spec §6.1 falls back to the folder scan on that alone,
    /// because a %LOCALAPPDATA%\Packages folder left behind by an uninstalled MSIX
    /// Claude would otherwise shadow a working legacy install with a launch target
    /// nothing can open.
    /// </summary>
    internal readonly record struct MsixLookup(bool Available, ClaudeInstallInfo? Info)
    {
        internal static MsixLookup Unavailable => new(false, null);

        internal static MsixLookup NotInstalled => new(true, null);

        internal static MsixLookup Found(ClaudeInstallInfo info) => new(true, info);
    }

    internal static bool IsClaudeFamily(string family) =>
        family.StartsWith("Claude_", StringComparison.Ordinal)
        || family.StartsWith("Anthropic.Claude", StringComparison.Ordinal);

    private static MsixLookup DetectMsixViaPackageManager()
    {
        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                var family = package.Id.FamilyName;
                if (!IsClaudeFamily(family))
                {
                    continue;
                }
                string? aumid = null;
                string? installDirectory = null;
                // Package.GetAppListEntries() and Package.InstalledPath require Windows
                // 10.0.19041.0+; the app's SupportedOSPlatformVersion (10.0.17763.0) is
                // lower, so both must be guarded.
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                {
                    try
                    {
                        var entries = package.GetAppListEntries();
                        if (entries.Count > 0)
                        {
                            aumid = entries[0].AppUserModelId;
                        }
                    }
                    catch (COMException)
                    {
                        // fall back to the conventional id below
                    }
                    try
                    {
                        installDirectory = package.InstalledPath;
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException)
                    {
                        // location unavailable (a staged or partly installed package):
                        // process matching falls back to the name alone
                    }
                }
                return MsixLookup.Found(new ClaudeInstallInfo(ClaudeInstallKind.Msix, family, aumid ?? family + AppIdSuffix, ClaudeInstallInfo.DefaultProcessName, installDirectory));
            }
            return MsixLookup.NotInstalled;   // the query worked: there is no MSIX Claude
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException
            or TypeLoadException or FileNotFoundException or PlatformNotSupportedException)
        {
            return MsixLookup.Unavailable;   // WinRT unusable here: the folder scan decides
        }
    }

    /// <summary>
    /// Fallback when WinRT is unavailable: package folders are named by family name.
    /// The install directory cannot be derived from the family name, so it is left
    /// unknown and Claude's processes are matched by name alone.
    /// </summary>
    internal ClaudeInstallInfo? DetectMsixByFolderScan()
    {
        var packages = Path.Combine(folders.LocalAppData, "Packages");
        if (!probe.DirectoryExists(packages))
        {
            return null;
        }
        var family = probe.EnumerateDirectories(packages)
            .Select(dir => Path.GetFileName(dir))
            .Where(name => name is not null && IsClaudeFamily(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .FirstOrDefault();
        return family is null
            ? null
            : new ClaudeInstallInfo(ClaudeInstallKind.Msix, family, family + AppIdSuffix, ClaudeInstallInfo.DefaultProcessName);
    }
}
