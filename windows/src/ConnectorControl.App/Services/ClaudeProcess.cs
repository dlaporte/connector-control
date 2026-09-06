using System.ComponentModel;
using System.Diagnostics;
using ConnectorControl.Core.Services;

namespace ConnectorControl.App.Services;

/// <summary>
/// Replaces NSRunningApplication + ClaudeRestarter (spec §6.2): running check
/// and launch time by process name; restart = graceful quit (session-end
/// messages, never force-kill), wait up to 15 s, relaunch by AUMID or exe.
/// </summary>
public sealed class ClaudeProcess : IClaudeProcess
{
    public const string MainWindowTitle = "Claude";
    public const string DidNotQuitMessage = "Claude didn’t quit (it may be showing a dialog). Quit it manually, then click Restart Claude again.";
    public const string NotInstalledMessage = "Claude Desktop was not found on this PC.";

    private static readonly int? CurrentSessionId = CurrentSession();
    private static readonly TimeSpan DefaultQuitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan InstallCacheTtl = TimeSpan.FromMinutes(1);

    private readonly Func<ClaudeInstallInfo> install;
    private readonly Func<string?> launchTargetOverride;
    private readonly TimeSpan quitTimeout;
    private readonly TimeSpan pollInterval;
    private readonly object gate = new();

    private ClaudeInstallInfo? cachedInstall;
    private DateTime cachedAt;

    public ClaudeProcess(Func<ClaudeInstallInfo> install, Func<string?> launchTargetOverride, TimeSpan? quitTimeout = null, TimeSpan? pollInterval = null)
    {
        this.install = install;
        this.launchTargetOverride = launchTargetOverride;
        this.quitTimeout = quitTimeout ?? DefaultQuitTimeout;
        this.pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public bool IsRunning => IsRunningFor(CurrentInstall());

    /// <summary>
    /// Earliest start time across Claude's processes (Electron spawns several
    /// within a couple of seconds), in UTC, or null when Claude is not running.
    /// </summary>
    public DateTime? LaunchTime => WithProcesses(CurrentInstall(), processes =>
    {
        DateTime? earliest = null;
        foreach (var process in processes)
        {
            try
            {
                // Process.StartTime is local; the contract (and ISettings.LastApplyDate,
                // which it is compared with) is UTC.
                var started = process.StartTime.ToUniversalTime();
                if (earliest is null || started < earliest)
                {
                    earliest = started;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // exited between enumeration and query, or access denied: skip it
            }
        }
        return earliest;
    });

    /// <summary>
    /// The install, resolved on first use and then cached. Detect() walks every
    /// package registered for the user, and RefreshRestartState plus the 250 ms
    /// quit poll read IsRunning and LaunchTime far too often to pay for that on
    /// each read (review I3). A NotFound result is never cached, and RestartAsync
    /// re-resolves, so a Claude installed while the app runs is still found. The
    /// cache also expires on a TTL: BelongsToInstall already tolerates Claude's own
    /// MSIX update relocating its versioned folder (review R1), but the TTL is a
    /// cheap second line of defence against any other way the cached info could
    /// go stale (e.g. a package reinstalled under a new publisher id).
    /// </summary>
    private ClaudeInstallInfo CurrentInstall()
    {
        lock (gate)
        {
            if (cachedInstall is null || cachedInstall.Kind == ClaudeInstallKind.NotFound || DateTime.UtcNow - cachedAt > InstallCacheTtl)
            {
                cachedInstall = install();
                cachedAt = DateTime.UtcNow;
            }
            return cachedInstall;
        }
    }

    private ClaudeInstallInfo RefreshInstall()
    {
        var info = install();
        lock (gate)
        {
            cachedInstall = info;
            cachedAt = DateTime.UtcNow;
        }
        return info;
    }

    public async Task<string?> RestartAsync(CancellationToken cancellationToken = default)
    {
        var info = RefreshInstall();   // the user may have installed or updated Claude since we started
        var target = launchTargetOverride() is { Length: > 0 } overridden ? overridden : info.LaunchTarget;
        if (target is null)
        {
            return NotInstalledMessage;
        }
        var aumid = IsAumid(target);
        if (!aumid && !File.Exists(target))
        {
            return $"Claude was not found at {target}.";
        }
        if (IsRunningFor(info))
        {
            var quit = await Task.Run(() => QuitAndWait(info, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (!quit)
            {
                return DidNotQuitMessage;
            }
        }
        return Launch(target, aumid);
    }

    /// <summary>An app user model id looks like <c>Family_hash!App</c>; an exe path is rooted.</summary>
    internal static bool IsAumid(string target) => target.Contains('!') && !Path.IsPathRooted(target);

    private bool QuitAndWait(ClaudeInstallInfo info, CancellationToken cancellationToken)
    {
        var pids = WithProcesses(info, processes => processes.Select(p => p.Id).ToHashSet());
        var windows = SessionEnd.FindCandidateWindows(pids, MainWindowTitle);
        if (windows.Count == 0 || !SessionEnd.RequestQuit(windows))
        {
            return false;
        }
        var deadline = DateTime.UtcNow + quitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunningFor(info))
            {
                return true;
            }
            Thread.Sleep(pollInterval);
        }
        return !IsRunningFor(info);
    }

    private static string? Launch(string target, bool aumid)
    {
        try
        {
            var startInfo = aumid
                ? new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + target) { UseShellExecute = true }
                : new ProcessStartInfo(target) { UseShellExecute = true };
            using var started = Process.Start(startInfo);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Claude's processes: the right image name, in this logon session, and —
    /// when the install location is known — running from it. Without the
    /// location filter the Claude Code CLI (also <c>claude.exe</c>) and another
    /// logged-on user's Claude Desktop would both count as ours (spec §6.2).
    /// </summary>
    private bool IsClaude(Process process, ClaudeInstallInfo info)
    {
        try
        {
            if (CurrentSessionId is int session && process.SessionId != session)
            {
                return false;
            }
            if (info.InstallDirectory is not { Length: > 0 })
            {
                return true;   // location unknown: the name is all we have; skip the image-path read
            }
            return BelongsToInstall(ProcessImage.ImagePath(process.Id), info);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;   // exited between enumeration and query, or not ours to inspect
        }
    }

    /// <summary>
    /// True when a process image path belongs to <paramref name="info"/>. MSIX
    /// installs match by package family (name + publisher id from
    /// <c>PackageFamilyName</c>), not the exact installed folder: Claude updates
    /// itself roughly weekly and each update relocates every process to a new
    /// versioned WindowsApps folder, so an exact-folder match goes stale until
    /// the next Restart click (review R1). Legacy installs match the exact
    /// folder, which the Squirrel updater never moves. A null
    /// <c>InstallDirectory</c> means the location is unknown, so the caller's
    /// name-only match is all there is.
    /// </summary>
    internal static bool BelongsToInstall(string? imagePath, ClaudeInstallInfo info)
    {
        if (info.InstallDirectory is not { Length: > 0 } directory)
        {
            return true;
        }
        if (info.Kind != ClaudeInstallKind.Msix || info.PackageFamilyName is not { Length: > 0 } family)
        {
            return ProcessImage.IsUnder(imagePath, directory);   // legacy: exact folder never moves
        }
        return BelongsToPackageFamily(imagePath, directory, family);
    }

    /// <summary>
    /// True when <paramref name="imagePath"/> sits directly under the same
    /// WindowsApps root as <paramref name="installDirectory"/>, in a package
    /// folder for the same family: <c>&lt;Name&gt;_&lt;version&gt;_&lt;arch&gt;_&lt;resourceId&gt;__&lt;PublisherId&gt;</c>
    /// for a family name <c>&lt;Name&gt;_&lt;PublisherId&gt;</c>. Version-independent by
    /// design, so it survives Claude's own update relocating the folder.
    /// </summary>
    internal static bool BelongsToPackageFamily(string? imagePath, string installDirectory, string familyName)
    {
        var separator = familyName.LastIndexOf('_');
        if (string.IsNullOrEmpty(imagePath) || separator < 0)
        {
            return false;
        }
        var root = Path.GetDirectoryName(installDirectory.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(root) || !ProcessImage.IsUnder(imagePath, root))
        {
            return false;
        }
        var packageFolder = imagePath[(root.Length + 1)..].Split(['\\', '/'], 2)[0];
        var name = familyName[..separator];
        var publisherId = familyName[(separator + 1)..];
        return packageFolder.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)
            && packageFolder.EndsWith("__" + publisherId, StringComparison.OrdinalIgnoreCase);
    }

    private static int? CurrentSession()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.SessionId;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or PlatformNotSupportedException)
        {
            return null;   // unknown: do not filter by session
        }
    }

    private bool IsRunningFor(ClaudeInstallInfo info) => WithProcesses(info, processes => processes.Length > 0);

    private T WithProcesses<T>(ClaudeInstallInfo info, Func<Process[], T> use)
    {
        var processes = Process.GetProcessesByName(info.ProcessName);
        try
        {
            return use(processes.Where(process => IsClaude(process, info)).ToArray());
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
