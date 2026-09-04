using System.Runtime.InteropServices;
using ConnectorControl.Core;
using ConnectorControl.Core.Services;
using Windows.Management.Deployment;

namespace ConnectorControl.App.Services;

/// <summary>
/// Detects how Claude Desktop is installed (spec §6.1). Order: the WinRT
/// package manager (authoritative, gives the AUMID), then a scan of
/// %LOCALAPPDATA%\Packages (the folder name IS the package family name), then
/// the legacy Squirrel exe.
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

    public ClaudeInstallInfo Detect()
    {
        var msix = DetectMsixViaPackageManager() ?? DetectMsixByFolderScan();
        if (msix is not null)
        {
            return msix;
        }
        var legacyExe = Path.Combine(folders.LocalAppData, "AnthropicClaude", "claude.exe");
        if (probe.FileExists(legacyExe))
        {
            return new ClaudeInstallInfo(ClaudeInstallKind.Legacy, null, legacyExe, ClaudeInstallInfo.DefaultProcessName, Path.GetDirectoryName(legacyExe));
        }
        return ClaudeInstallInfo.NotFound;
    }

    internal static bool IsClaudeFamily(string family) =>
        family.StartsWith("Claude_", StringComparison.Ordinal)
        || family.StartsWith("Anthropic.Claude", StringComparison.Ordinal);

    private static ClaudeInstallInfo? DetectMsixViaPackageManager()
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
                return new ClaudeInstallInfo(ClaudeInstallKind.Msix, family, aumid ?? family + AppIdSuffix, ClaudeInstallInfo.DefaultProcessName, installDirectory);
            }
            return null;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException
            or TypeLoadException or FileNotFoundException or PlatformNotSupportedException)
        {
            return null;   // WinRT unavailable in this environment: the folder scan decides
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
