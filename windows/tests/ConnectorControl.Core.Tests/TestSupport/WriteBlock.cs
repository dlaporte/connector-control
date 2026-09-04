using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// Makes writes to one file fail while reads keep working, so
/// <c>ClaudeConfigIO.Write</c> throws inside <c>ConfigService.Apply</c>.
/// <para>
/// Two mechanisms, in order. First the parent directory is closed to new files —
/// a Deny ACE for the current user's SID on Windows, mode 0500 on Unix — which
/// stops <c>AtomicFile</c> from creating its temp file. If a probe shows that did
/// not bind (an elevated Windows account can be granted access that outranks a
/// Deny ACE), the file itself is held open with <c>FileShare.Read</c>: Windows
/// enforces share modes against every caller regardless of privilege, so
/// <c>File.Replace</c> fails with a sharing violation while <c>File.ReadAllBytes</c>
/// and <c>File.Copy</c> (both readers) still succeed.
/// </para>
/// <para>
/// <see cref="IsEffective"/> reports whether writes really are blocked. Tests
/// ASSERT it rather than skipping on it: CI runs with failSkips, so a block that
/// stops working must fail loudly, not quietly weaken a test. On Unix it can only
/// be false when the suite runs as root, which mode bits do not bind.
/// </para>
/// </summary>
public sealed class WriteBlock : IDisposable
{
    private readonly string path;
    private readonly string directory;
    private readonly UnixFileMode? previousMode;
    private readonly FileStream? handle;

    public WriteBlock(string path)
    {
        this.path = Path.GetFullPath(path);
        directory = Path.GetDirectoryName(this.path) ?? throw new ArgumentException("Path has no parent directory.", nameof(path));
        if (OperatingSystem.IsWindows())
        {
            AddDeny(directory);
        }
        else
        {
            previousMode = File.GetUnixFileMode(directory);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        if (!CanCreateFiles())
        {
            IsEffective = true;
            return;
        }
        if (OperatingSystem.IsWindows() && File.Exists(this.path))
        {
            handle = new FileStream(this.path, FileMode.Open, FileAccess.Read, FileShare.Read);
            IsEffective = true;
        }
    }

    /// <summary>True when writes to the file are genuinely blocked.</summary>
    public bool IsEffective { get; }

    public void Dispose()
    {
        handle?.Dispose();
        if (OperatingSystem.IsWindows())
        {
            RemoveDeny(directory);
        }
        else if (previousMode is { } mode)
        {
            File.SetUnixFileMode(directory, mode);
        }
    }

    /// <summary>Can the current user still add a file to the directory? (AtomicFile's first step.)</summary>
    private bool CanCreateFiles()
    {
        var probe = Path.Combine(directory, $".writeblock-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        try
        {
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // leaving the probe behind is harmless in a temp directory
        }
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static void AddDeny(string path)
    {
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(DenyRule());
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveDeny(string path)
    {
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        security.RemoveAccessRule(DenyRule());
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule DenyRule() =>
        new(WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("no SID"),
            FileSystemRights.CreateFiles | FileSystemRights.WriteData,
            AccessControlType.Deny);
}
