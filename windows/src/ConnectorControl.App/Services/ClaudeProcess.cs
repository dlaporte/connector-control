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

    private readonly Func<ClaudeInstallInfo> install;
    private readonly Func<string?> launchTargetOverride;
    private readonly TimeSpan quitTimeout;
    private readonly TimeSpan pollInterval;

    public ClaudeProcess(Func<ClaudeInstallInfo> install, Func<string?> launchTargetOverride, TimeSpan? quitTimeout = null, TimeSpan? pollInterval = null)
    {
        this.install = install;
        this.launchTargetOverride = launchTargetOverride;
        this.quitTimeout = quitTimeout ?? DefaultQuitTimeout;
        this.pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public bool IsRunning => WithProcesses(processes => processes.Length > 0);

    /// <summary>Earliest StartTime across Claude's processes (Electron spawns several within a couple of seconds).</summary>
    public DateTime? LaunchTime => WithProcesses(processes =>
    {
        DateTime? earliest = null;
        foreach (var process in processes)
        {
            try
            {
                var started = process.StartTime;
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

    public async Task<string?> RestartAsync(CancellationToken cancellationToken = default)
    {
        var info = install();
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
        if (IsRunning)
        {
            var quit = await Task.Run(() => QuitAndWait(cancellationToken), cancellationToken).ConfigureAwait(false);
            if (!quit)
            {
                return DidNotQuitMessage;
            }
        }
        return Launch(target, aumid);
    }

    /// <summary>An app user model id looks like <c>Family_hash!App</c>; an exe path is rooted.</summary>
    internal static bool IsAumid(string target) => target.Contains('!') && !Path.IsPathRooted(target);

    private bool QuitAndWait(CancellationToken cancellationToken)
    {
        var pids = WithProcesses(processes => processes.Select(p => p.Id).ToHashSet());
        var windows = SessionEnd.FindCandidateWindows(pids, MainWindowTitle);
        if (windows.Count == 0 || !SessionEnd.RequestQuit(windows))
        {
            return false;
        }
        var deadline = DateTime.UtcNow + quitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunning)
            {
                return true;
            }
            Thread.Sleep(pollInterval);
        }
        return !IsRunning;
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
            if (info.InstallDirectory is not { Length: > 0 } directory)
            {
                return true;   // location unknown: the name is all we have
            }
            return ProcessImage.IsUnder(ProcessImage.ImagePath(process.Id), directory);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;   // exited between enumeration and query, or not ours to inspect
        }
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

    private T WithProcesses<T>(Func<Process[], T> use)
    {
        var info = install();
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
