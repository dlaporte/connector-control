using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using ConnectorControl.Core.State;
using Microsoft.Win32.SafeHandles;

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
    /// meets the app the person is running - as the Mac's socket moves with the store folder.</summary>
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
    /// CONNECTOR_CONTROL_STORE_DIR or CONNECTOR_CONTROL_CLAUDE_CONFIG is set, since the app it would start reads
    /// the live store and edits the live Claude config, not the ones named.</summary>
    public static bool MayStartApp(IReadOnlyDictionary<string, string> environment) =>
        IsUnset(environment, AppPaths.StoreDirEnv) && IsUnset(environment, AppPaths.ClaudeConfigEnv);

    private static bool IsUnset(IReadOnlyDictionary<string, string> environment, string name) =>
        !environment.TryGetValue(name, out var value) || value.Length == 0;

    /// <summary>The prefix of every variable that points the app or the tool somewhere other than the live files.</summary>
    private const string OverridePrefix = "CONNECTOR_CONTROL_";

    /// <summary>
    /// The environment the tool starts the app with, sorted as Windows wants an environment block. Platform idiom:
    /// the Mac's <c>open</c> hands the app launchd's environment, never the caller's; CreateProcess would hand it the
    /// tool's, which is Claude's or a terminal's. So the app gets the caller's environment without any
    /// CONNECTOR_CONTROL_* variable, and with its PATH rebuilt as Explorer builds it at sign-in: the machine's
    /// setting, then the user's (<paramref name="machinePath"/>, <paramref name="userPath"/>, already expanded). A
    /// terminal's PATH (an nvm or venv folder first) would otherwise decide which npx or uvx the app finds for as
    /// long as it runs. The rest is kept rather than rebuilt from the registry: it is the same sign-in's, and a
    /// variable only a terminal set changes nothing the app reads. When neither setting can be read, the caller's
    /// PATH stays: an app with no PATH at all finds no tool.
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> AppEnvironment(IReadOnlyDictionary<string, string> current, string? machinePath, string? userPath)
    {
        var kept = current
            .Where(pair => pair.Key.Length > 0 && !pair.Key.Contains('=') && !pair.Key.Contains('\0') && !pair.Value.Contains('\0'))
            .Where(pair => !pair.Key.StartsWith(OverridePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(pair => (Name: pair.Key, Value: pair.Value))
            .ToList();
        var parts = new[] { machinePath, userPath }.Where(part => !string.IsNullOrEmpty(part)).ToList();
        if (parts.Count > 0)
        {
            kept.RemoveAll(pair => string.Equals(pair.Name, "PATH", StringComparison.OrdinalIgnoreCase));
            kept.Add(("Path", string.Join(';', parts)));
        }
        return kept.OrderBy(pair => pair.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The Unicode environment block CreateProcess takes: <c>NAME=value</c> each ended by a NUL, and one more NUL to close.</summary>
    public static string EnvironmentBlock(IEnumerable<(string Name, string Value)> environment)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in environment)
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }
        return block.Append('\0').ToString();
    }

    /// <summary>The types <c>GetTokenInformation</c> gives for TokenElevationType: a token that was never split
    /// (UAC off, the built-in Administrator account, a standard user), the elevated half of a split one, and its
    /// filtered half, which an administrator runs with by default.</summary>
    public const int TokenElevationTypeDefault = 1;
    public const int TokenElevationTypeFull = 2;
    public const int TokenElevationTypeLimited = 3;

    /// <summary>Whether a token runs elevated: it is the elevated half of a split token, what "Run as administrator"
    /// gives with UAC on. Such a token's pipe check admits only a pipe of the same owner, so it cannot reach the
    /// person's app, and an app it started would serve a pipe out of reach of everything the person runs as
    /// themselves. A token that was never split is how everything the person runs starts - with UAC off, or as the
    /// built-in Administrator account - so an app it starts serves them all. A type Windows does not give, or none
    /// at all, counts as elevated: the tool then starts nothing.</summary>
    public static bool IsElevated(int? elevationType) =>
        elevationType is not (TokenElevationTypeDefault or TokenElevationTypeLimited);

    /// <summary>Whether this process runs elevated, as <see cref="IsElevated(int?)"/> reads its token's elevation type.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return IsElevated(GetTokenInformation(identity.AccessToken, TokenElevationTypeClass, out var type, sizeof(int), out _) ? type : null);
    }

    /// <summary>TOKEN_INFORMATION_CLASS's TokenElevationType.</summary>
    private const int TokenElevationTypeClass = 18;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out int information, int length, out int returnedLength);

    /// <summary>Where the app records its own executable, beside the copy of the tool it keeps.</summary>
    public const string AppPathFileName = "app-path.txt";

    /// <summary>The app the tool starts: the one beside it - the packaged tool sits beside ConnectorControl.exe -
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

    /// <summary>What the tool says for the version a build stamped - .NET's informational version, which
    /// carries the source revision after a "+": its SemVer, or "development build" for a stamp that is none -
    /// an empty one - or for <c>0.0.0</c>, what a build nobody stamped carries (Directory.Build.props), so
    /// both platforms call such a build the same thing.</summary>
    public static string ToolVersion(string stamped) =>
        ControlVersion.Semantic(stamped) is { } version && version != "0.0.0" ? version : AppVersion.DevelopmentBuild;
}
