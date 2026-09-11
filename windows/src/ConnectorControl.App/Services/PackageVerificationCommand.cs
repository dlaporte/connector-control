using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ConnectorControl.App.Services;

/// <summary>
/// The `--verify-package &lt;nupkg&gt; --report &lt;file&gt;` switch the app itself understands, so a
/// build's own freshly packed nupkg can be checked against <see cref="UpdateVerifier"/> — the exact
/// rule <see cref="VelopackUpdater"/> applies to a downloaded update — without a real update feed, a
/// scheduled check, or a download. smoke-test.ps1 runs the installed exe with these two flags and
/// reads the report back; nothing else in the app produces this shape of invocation, so any other
/// argument list (no args, a toast-activation relaunch) is simply not this command and a normal
/// launch continues.
/// </summary>
internal static partial class PackageVerificationCommand
{
    /// <summary>
    /// <c>ConnectorControl-&lt;version&gt;-&lt;rid&gt;-full.nupkg</c>, the name `vpk pack` gives the
    /// package it produces. The version may carry a prerelease suffix (`1.3.3-preview.2`) and the rid
    /// itself contains a hyphen (`win-x64`); only the numeric major.minor.patch is wanted, so
    /// everything between it and the trailing `-full.nupkg` is swallowed rather than parsed.
    /// </summary>
    [GeneratedRegex(@"^ConnectorControl-(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-.+)?-full\.nupkg$")]
    private static partial Regex NupkgNamePattern();

    public static bool TryRun(string[] args, out int exitCode) =>
        TryRun(args, ProductionVerify, Environment.ProcessPath, out exitCode);

    /// <summary>
    /// Test seam: <paramref name="verify"/> replaces <see cref="UpdateVerifier.Verify"/> (already
    /// given the identity it needs to check against) and <paramref name="processPath"/> replaces
    /// <see cref="Environment.ProcessPath"/> as the running app's own location.
    /// </summary>
    internal static bool TryRun(string[] args, Func<string, string?, Version, Version, string?> verify, string? processPath, out int exitCode)
    {
        exitCode = 0;
        if (!Contains(args, "--verify-package"))
        {
            return false;   // not this command: fall through to the app's normal startup
        }
        // Past this point the caller is clearly attempting this command, so every remaining
        // problem is reported through this command's own exit code rather than by pretending
        // the launch never happened.
        _ = TryValue(args, "--report", out var reportPath);
        if (!TryValue(args, "--verify-package", out var nupkgPath))
        {
            exitCode = 2;   // "--verify-package" was given with nothing after it: a usage error
            if (reportPath is not null)
            {
                WriteReport(reportPath, "\"--verify-package\" requires a value.");
            }
            return true;
        }
        if (reportPath is null)
        {
            exitCode = 2;   // nowhere to write the reason: there is no report path to write it to
            return true;
        }
        if (!File.Exists(nupkgPath))
        {
            WriteReport(reportPath, $"The package \"{nupkgPath}\" does not exist.");
            exitCode = 2;
            return true;
        }
        if (!TryParseFeedVersion(Path.GetFileName(nupkgPath), out var feedVersion))
        {
            WriteReport(reportPath, $"\"{Path.GetFileName(nupkgPath)}\" is not a package name this app understands (expected ConnectorControl-<version>-<rid>-full.nupkg).");
            exitCode = 2;
            return true;
        }
        if (processPath is null)
        {
            WriteReport(reportPath, "This app's own location is unknown, so the running version could not be read.");
            exitCode = 2;
            return true;
        }
        var runningVersion = UpdateVerifier.EmbeddedVersion(processPath);
        var updateExePath = FindUpdateExe(processPath);
        string? problem;
        try
        {
            problem = verify(nupkgPath, updateExePath, runningVersion, feedVersion);
        }
        catch (Exception ex)
        {
            // The app's unhandled-exception handler (App.OnUnhandledException) is not armed this
            // early in startup, so a throw here would otherwise crash with no report written at all.
            WriteReport(reportPath, ex.ToString());
            exitCode = 1;
            return true;
        }
        WriteReport(reportPath, problem ?? OkReport(updateExePath));
        exitCode = problem is null ? 0 : 1;
        return true;
    }

    /// <summary>
    /// The success report names which checks actually ran, so a layout the checks silently skip
    /// (no installed Update.exe found) is visible in the report rather than looking identical to
    /// a full pass.
    /// </summary>
    private static string OkReport(string? updateExePath) =>
        updateExePath is null
            ? "OK\nchecked: package entries, main executable version (no installed updater found)"
            : "OK\nchecked: package entries, main executable version, installed updater";

    /// <summary>Builds the running app's identity and defers to the real check.</summary>
    private static string? ProductionVerify(string packagePath, string? updateExePath, Version runningVersion, Version feedVersion)
    {
        var identity = UpdateVerifier.ExpectedIdentity(Environment.ProcessPath!);
        return UpdateVerifier.Verify(packagePath, updateExePath, identity, runningVersion, feedVersion);
    }

    /// <summary>
    /// Velopack's install layout is `&lt;root&gt;\current\ConnectorControl.exe` with
    /// `&lt;root&gt;\Update.exe` beside `current\`: two directories up from the running exe. Null when
    /// that file is not there (a dev run, or a layout this is not).
    /// </summary>
    private static string? FindUpdateExe(string processPath)
    {
        var currentDir = Path.GetDirectoryName(processPath);
        var root = currentDir is null ? null : Path.GetDirectoryName(currentDir);
        if (root is null)
        {
            return null;
        }
        var candidate = Path.Combine(root, "Update.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    internal static bool TryParseFeedVersion(string nupkgFileName, out Version version)
    {
        var match = NupkgNamePattern().Match(nupkgFileName);
        if (!match.Success)
        {
            version = new Version(0, 0, 0);
            return false;
        }
        version = new Version(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value));
        return true;
    }

    private static void WriteReport(string reportPath, string text)
    {
        var dir = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(reportPath, text);
    }

    /// <summary>The token after the first occurrence of <paramref name="flag"/>, wherever it sits in <paramref name="args"/>. False both when <paramref name="flag"/> is absent and when it is the last token (present but valueless) — callers that must tell those apart use <see cref="Contains"/> first.</summary>
    private static bool TryValue(string[] args, string flag, [NotNullWhen(true)] out string? value)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                value = args[i + 1];
                return true;
            }
        }
        value = null;
        return false;
    }

    /// <summary>Whether <paramref name="flag"/> appears anywhere in <paramref name="args"/>, value or not.</summary>
    private static bool Contains(string[] args, string flag) => Array.IndexOf(args, flag) >= 0;
}
