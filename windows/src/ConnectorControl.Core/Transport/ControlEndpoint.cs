using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

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

    /// <summary>Where the app records its own executable, beside the copy of the tool it keeps.</summary>
    public const string AppPathFileName = "app-path.txt";

    /// <summary>The app the tool starts: the one beside it — the packaged tool sits beside ConnectorControl.exe —
    /// else the one the app recorded beside the copy it keeps in bin\; null when neither is there, or the
    /// record names anything but ConnectorControl.exe.</summary>
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
            && string.Equals(Path.GetFileName(recorded), AppFileName, StringComparison.OrdinalIgnoreCase)
            && exists(recorded) ? recorded : null;
    }
}
