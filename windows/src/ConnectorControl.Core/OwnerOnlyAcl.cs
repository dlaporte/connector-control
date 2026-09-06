using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ConnectorControl.Core;

/// <summary>
/// Windows counterpart of the Mac app's mode 0600 / 0700: a protected DACL
/// granting the current user full control and nobody else. Connector configs
/// can hold env-var secrets, so every file this app writes gets this.
/// </summary>
public static class OwnerOnlyAcl
{
    /// <summary>Best effort, like Swift's <c>try?</c>: no-op off Windows, errors swallowed.</summary>
    public static void TryApply(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            Apply(path);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException            // includes PrivilegeNotHeldException
            or InvalidOperationException
            or PlatformNotSupportedException
            or System.Security.SecurityException
            or IdentityNotMappedException)
        {
            // Best effort, like Swift's `try?`: the write itself must never fail
            // because the ACL could not be tightened. Programming errors still surface.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Apply(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows identity has no SID.");
        if (Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else if (File.Exists(path))
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }

    /// <summary>True when the DACL is protected and every rule names the current user.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsOwnerOnly(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows identity has no SID.");
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
