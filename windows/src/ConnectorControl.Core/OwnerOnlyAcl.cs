using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ConnectorControl.Core;

/// <summary>
/// Windows counterpart of the Mac app's mode 0600 / 0700: a protected DACL
/// granting the current user full control and nobody else. Connector configs
/// can hold env-var secrets, so every file this app writes gets this — in the
/// create call itself where possible (<see cref="WriteNewProtectedFile"/>,
/// <see cref="CreateDirectoryProtected"/>, both reached through <see cref="AtomicFile"/>),
/// after the fact only as a repair (<see cref="TryApply"/>).
/// </summary>
public static class OwnerOnlyAcl
{
    /// <summary>The process identity never changes, and every DACL this class builds names it.</summary>
    private static SecurityIdentifier? currentUser;

    /// <summary>
    /// Best effort, like Swift's <c>try?</c>: errors swallowed. Returns true when the ACL was
    /// applied (or there was nothing to do: off Windows), false when the attempt failed, so a
    /// caller that sweeps many paths can tell a working sweep from one that achieved nothing.
    /// </summary>
    public static bool TryApply(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }
        try
        {
            Apply(path);
            return true;
        }
        catch (Exception ex) when (IsAclRepairFailure(ex))
        {
            // Best effort, like Swift's `try?`: the write itself must never fail
            // because the ACL could not be tightened. Programming errors still surface.
            return false;
        }
    }

    /// <summary>
    /// Creates <paramref name="path"/>, which must not exist yet, with the owner-only DACL in the
    /// create call itself — the Windows counterpart of <c>open(O_CREAT|O_EXCL, 0600)</c> — and
    /// writes <paramref name="data"/> into it. Nothing else ever sees the file with the parent
    /// folder's inherited permissions. When the ACL machinery itself fails (no SID, an unsupported
    /// object) the file is created plainly and <see cref="TryApply"/> repairs it. Returns whether
    /// the file ended up owner-only; file errors throw as they would from File.WriteAllBytes.
    /// </summary>
    internal static bool WriteNewProtectedFile(string path, byte[] data)
    {
        if (OperatingSystem.IsWindows())
        {
            FileStream? protectedStream = null;
            try
            {
                protectedStream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, FileSecurityForCurrentUser());
            }
            catch (Exception ex) when (IsAclUnavailable(ex))
            {
                // fall through to the plain create below
            }
            if (protectedStream is not null)
            {
                using (protectedStream)
                {
                    protectedStream.Write(data);
                }
                return true;
            }
        }
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(data);
        }
        return TryApply(path);
    }

    /// <summary>
    /// Creates <paramref name="dir"/> owner-only when it does not exist yet, missing parents
    /// included — the Mac's <c>createDirectory(attributes: 0o700)</c>. A directory that already
    /// exists — a folder the user chose — is never rewritten (the sweep's rule too). Throws
    /// IOException when a file sits where the directory should be.
    /// </summary>
    internal static void CreateDirectoryProtected(string dir)
    {
        if (Directory.Exists(dir))
        {
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                new DirectoryInfo(dir).Create(DirectorySecurityForCurrentUser());
                return;
            }
            catch (Exception ex) when (IsAclUnavailable(ex))
            {
                // fall through to the plain create below
            }
        }
        Directory.CreateDirectory(dir);
        TryApply(dir);
    }

    /// <summary>A protected DACL (no inheritance) granting the current user full control and nobody else.</summary>
    [SupportedOSPlatform("windows")]
    private static FileSecurity FileSecurityForCurrentUser()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>The directory form of <see cref="FileSecurityForCurrentUser"/>: the rule inherits to what is created inside.</summary>
    [SupportedOSPlatform("windows")]
    private static DirectorySecurity DirectorySecurityForCurrentUser()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUser(), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser()
    {
        if (currentUser is null)
        {
            using var identity = WindowsIdentity.GetCurrent();
            currentUser = identity.User ?? throw new InvalidOperationException("The current Windows identity has no SID.");
        }
        return currentUser;
    }

    [SupportedOSPlatform("windows")]
    private static void Apply(string path)
    {
        if (Directory.Exists(path))
        {
            new DirectoryInfo(path).SetAccessControl(DirectorySecurityForCurrentUser());
        }
        else if (File.Exists(path))
        {
            new FileInfo(path).SetAccessControl(FileSecurityForCurrentUser());
        }
    }

    /// <summary>
    /// The ACL machinery is unavailable on its own terms — no SID, an object or volume without security
    /// (NotSupportedException is what .NET raises for ERROR_NO_SECURITY_ON_OBJECT on FAT/exFAT and
    /// some shares) — as opposed to the file operation it was attached to failing.
    /// </summary>
    private static bool IsAclUnavailable(Exception ex) => ex is InvalidOperationException
        or PlatformNotSupportedException
        or NotSupportedException
        or System.Security.SecurityException
        or IdentityNotMappedException;

    /// <summary>Everything a repair (<see cref="TryApply"/>) swallows: the machinery being unavailable, plus the file being gone or locked.</summary>
    private static bool IsAclRepairFailure(Exception ex) =>
        IsAclUnavailable(ex)
        || ex is IOException
        || ex is UnauthorizedAccessException;   // includes PrivilegeNotHeldException

    /// <summary>True when the DACL is protected and every rule names the current user.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsOwnerOnly(string path)
    {
        var user = CurrentUser();
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        if (!security.AreAccessRulesProtected)
        {
            return false;
        }
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        if (rules.Count == 0)
        {
            return false;
        }
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || !rule.IdentityReference.Equals(user))
            {
                return false;
            }
        }
        return true;
    }
}
