using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Transport;

/// <summary>
/// Windows only: the Mac counterpart is Sources/ConnectorControlTransport/ControlEndpoint.swift.
///
/// Where the app's endpoint and its tool are, worked out the same way by the app and by the tool.
/// </summary>
public static class ControlEndpoint
{
    public const string PipePrefix = "ConnectorControl-";
    public const string ToolFileName = "connector-control.exe";
    public const string AppFileName = "ConnectorControl.exe";
    /// <summary>What the tool starts the app with: no flyout, and a second instance leaves without a word.</summary>
    public const string BackgroundArgument = "--background";

    /// <summary>The pipe: <c>ConnectorControl-&lt;user SID&gt;</c>, one per account on the PC; with
    /// CONNECTOR_CONTROL_STORE_DIR set, a suffix from that folder, so a development run or a test host never
    /// meets the app the person is running — as the Mac's socket moves with the store folder.</summary>
    public static string PipeName(IReadOnlyDictionary<string, string> environment, string userSid)
    {
        var name = PipePrefix + userSid;
        if (environment.TryGetValue(AppPaths.StoreDirEnv, out var store) && store.Length > 0)
        {
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(store)).ToUpperInvariant();
            name += "-" + ContentHash.Sha256(Encoding.UTF8.GetBytes(folder))["sha256:".Length..][..16];
        }
        return name;
    }

    /// <summary>The account this process runs as, as Windows names it.</summary>
    [SupportedOSPlatform("windows")]
    public static string CurrentUserSid() =>
        WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("This process runs as no user.");

    /// <summary>Whether the tool may start the app when nothing answers: never while
    /// CONNECTOR_CONTROL_STORE_DIR is set, since the app it would start reads the live store, not the one
    /// named.</summary>
    public static bool MayStartApp(IReadOnlyDictionary<string, string> environment) =>
        !environment.TryGetValue(AppPaths.StoreDirEnv, out var store) || store.Length == 0;

    /// <summary>Whether a token runs elevated: its owner — who owns what it creates, the pipe among them — is not
    /// its user, as for an administrator's elevated token, owned by BUILTIN\Administrators. Its pipe check admits
    /// only a pipe of the same owner, so it cannot reach the person's app, and an app it started would serve a
    /// pipe out of reach of everything the person runs as themselves. An owner or user Windows cannot name
    /// counts as elevated: the tool then starts nothing.</summary>
    public static bool IsElevated(string? ownerSid, string? userSid) =>
        ownerSid is null || userSid is null || !string.Equals(ownerSid, userSid, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this process runs elevated, as <see cref="IsElevated(string?, string?)"/> reads it.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return IsElevated(identity.Owner?.Value, identity.User?.Value);
    }

    /// <summary>Where the app records its own executable, beside the copy of the tool it keeps.</summary>
    public const string AppPathFileName = "app-path.txt";

    /// <summary>The app the tool starts: the one beside it — the packaged tool sits beside ConnectorControl.exe —
    /// else the one the app recorded beside the copy it keeps in bin\; null when neither is there, or the
    /// record names anything but a full path to ConnectorControl.exe: a relative one would resolve against
    /// whatever folder the tool was run from.</summary>
    public static string? AppExecutable(string toolPath, Func<string, bool> exists, Func<string, string?> readText)
    {
        var folder = Path.GetDirectoryName(toolPath) ?? "";
        var beside = Path.Combine(folder, AppFileName);
        if (exists(beside))
        {
            return beside;
        }
        var recorded = readText(Path.Combine(folder, AppPathFileName))?.Trim();
        return recorded is { Length: > 0 }
            && Path.IsPathFullyQualified(recorded)
            && string.Equals(Path.GetFileName(recorded), AppFileName, StringComparison.OrdinalIgnoreCase)
            && exists(recorded) ? recorded : null;
    }

    /// <summary>What the tool says for the version a build stamped — .NET's informational version, which
    /// carries the source revision after a "+": its SemVer, or "development build" for a stamp that is none
    /// — an empty one — or for <c>0.0.0</c>, what a build nobody stamped carries (Directory.Build.props), so
    /// both platforms call such a build the same thing.</summary>
    public static string ToolVersion(string stamped) =>
        ControlVersion.Semantic(stamped) is { } version && version != "0.0.0" ? version : AppVersion.DevelopmentBuild;
}
