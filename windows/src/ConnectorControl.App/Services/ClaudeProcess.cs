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

    private T WithProcesses<T>(Func<Process[], T> use)
    {
        var processes = Process.GetProcessesByName(install().ProcessName);
        try
        {
            return use(processes);
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
