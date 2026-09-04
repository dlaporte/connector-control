# Second-round probe for the Connector Control Windows port: how can Claude
# Desktop (MSIX) be asked to quit WITHOUT force-killing it, and does relaunch
# by app identity work? Run with Claude open, in a normal PowerShell:
#   powershell -ExecutionPolicy Bypass -File .\probe-claude-quit.ps1
# Each attempt asks for confirmation first. Nothing is ever force-killed.
$ErrorActionPreference = 'Continue'

function Section($title) { Write-Host ""; Write-Host "== $title ==" -ForegroundColor Cyan }
function ClaudeProcs { Get-Process | Where-Object { $_.ProcessName -like 'claude*' } }
function WaitGone($seconds) {
  $deadline = (Get-Date).AddSeconds($seconds)
  do { Start-Sleep -Milliseconds 250; $p = ClaudeProcs } while ($p -and (Get-Date) -lt $deadline)
  return (-not $p)
}

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
public static class Rm {
  [StructLayout(LayoutKind.Sequential)]
  public struct RM_UNIQUE_PROCESS { public int dwProcessId; public FILETIME ProcessStartTime; }
  [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
  public static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);
  [DllImport("rstrtmgr.dll")]
  public static extern int RmEndSession(uint pSessionHandle);
  [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
  public static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications, RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);
  [DllImport("rstrtmgr.dll")]
  public static extern int RmShutdown(uint pSessionHandle, uint lActionFlags, IntPtr fnStatus);
  [DllImport("kernel32.dll", SetLastError = true)]
  public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("kernel32.dll", SetLastError = true)]
  public static extern bool GetProcessTimes(IntPtr h, out FILETIME c, out FILETIME e, out FILETIME k, out FILETIME u);
  [DllImport("kernel32.dll")]
  public static extern bool CloseHandle(IntPtr h);
  // Asks the Restart Manager to shut the given processes down gracefully (no force flag).
  public static int Shutdown(int[] pids, out string detail) {
    uint session;
    StringBuilder key = new StringBuilder(64);
    int r = RmStartSession(out session, 0, key);
    if (r != 0) { detail = "RmStartSession=" + r; return r; }
    try {
      List<RM_UNIQUE_PROCESS> apps = new List<RM_UNIQUE_PROCESS>();
      foreach (int pid in pids) {
        IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) continue;
        FILETIME c, e, k, u;
        if (GetProcessTimes(h, out c, out e, out k, out u)) {
          RM_UNIQUE_PROCESS p = new RM_UNIQUE_PROCESS();
          p.dwProcessId = pid;
          p.ProcessStartTime = c;
          apps.Add(p);
        }
        CloseHandle(h);
      }
      r = RmRegisterResources(session, 0, null, (uint)apps.Count, apps.ToArray(), 0, null);
      if (r != 0) { detail = "RmRegisterResources=" + r; return r; }
      r = RmShutdown(session, 0, IntPtr.Zero);
      detail = "RmShutdown=" + r + " (registered " + apps.Count + " processes)";
      return r;
    } finally {
      RmEndSession(session);
    }
  }
}
public static class Win {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
  public static List<IntPtr> WindowsOf(HashSet<uint> pids) {
    List<IntPtr> r = new List<IntPtr>();
    EnumWindows(delegate(IntPtr h, IntPtr l) { uint pid; GetWindowThreadProcessId(h, out pid); if (pids.Contains(pid)) r.Add(h); return true; }, IntPtr.Zero);
    return r;
  }
  public static string Title(IntPtr h) { StringBuilder sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
}
"@

Section "Claude processes before"
$procs = ClaudeProcs
if (-not $procs) { "Claude is not running. Launch it, wait for it to finish starting, then re-run this script."; exit }
$procs | Format-Table Id, ProcessName, StartTime -AutoSize

Section "Attempt 1: Restart Manager graceful shutdown (no force)"
$a1 = Read-Host "Ask Claude to quit via the Restart Manager (the API installers use; sends session-end requests, never force-kills)? (y/N)"
if ($a1 -eq 'y') {
  [int[]]$pids = @($procs | ForEach-Object { [int]$_.Id })
  $detail = ""
  $r = [Rm]::Shutdown($pids, [ref]$detail)
  "RmShutdown returned $r ($detail)  [0 = success; 352 = an app refused to quit]"
  if (WaitGone 15) { "RESULT-RM: Claude quit within 15 s" }
  else { "RESULT-RM: Claude still running after 15 s"; ClaudeProcs | Format-Table Id, ProcessName -AutoSize }
}

if (ClaudeProcs) {
  Section "Attempt 2: WM_QUERYENDSESSION + WM_ENDSESSION to Claude's main window"
  $a2 = Read-Host "Still running. Send the session-end messages (ENDSESSION_CLOSEAPP) to Claude's main window? (y/N)"
  if ($a2 -eq 'y') {
    $set = New-Object 'System.Collections.Generic.HashSet[uint32]'
    ClaudeProcs | ForEach-Object { [void]$set.Add([uint32]$_.Id) }
    $wins = [Win]::WindowsOf($set) | Where-Object { [Win]::IsWindowVisible($_) -or ([Win]::Title($_) -eq 'Claude') }
    "target windows: $(@($wins).Count)"
    foreach ($h in $wins) {
      $res = [IntPtr]::Zero
      [void][Win]::SendMessageTimeout($h, 0x0011, [IntPtr]1, [IntPtr]1, 2, 5000, [ref]$res)   # WM_QUERYENDSESSION, ENDSESSION_CLOSEAPP
      "  hwnd $h title='$([Win]::Title($h))' WM_QUERYENDSESSION -> $res"
      [void][Win]::SendMessageTimeout($h, 0x0016, [IntPtr]1, [IntPtr]1, 2, 5000, [ref]$res)   # WM_ENDSESSION
    }
    if (WaitGone 15) { "RESULT-ENDSESSION: Claude quit within 15 s" }
    else { "RESULT-ENDSESSION: Claude still running after 15 s"; ClaudeProcs | Format-Table Id, ProcessName -AutoSize }
  }
}

Section "Relaunch by app identity"
if (ClaudeProcs) {
  "Claude is still running, so the relaunch test is skipped. Quit Claude from its tray icon (right-click the icon, Quit), then re-run this script and answer N to the quit attempts to reach this step."
} else {
  $a3 = Read-Host "Claude is not running. Relaunch it via shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude? (y/N)"
  if ($a3 -eq 'y') {
    Start-Process explorer.exe 'shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude'
    $deadline = (Get-Date).AddSeconds(20)
    do { Start-Sleep -Milliseconds 500; $p = ClaudeProcs } while ((-not $p) -and (Get-Date) -lt $deadline)
    if ($p) { "RESULT-RELAUNCH: Claude started ($(@($p).Count) processes)"; $p | Format-Table Id, ProcessName, StartTime -AutoSize }
    else { "RESULT-RELAUNCH: no Claude process within 20 s" }
  }
}

Section "Done"
"Paste everything above back into the chat."
