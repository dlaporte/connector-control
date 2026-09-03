# Day-one probe for the Connector Control Windows port.
# Run in a normal (non-admin) PowerShell:   powershell -ExecutionPolicy Bypass -File .\probe-claude.ps1
# Reports how Claude Desktop is installed, where its config really lives, how
# it can be relaunched, and (optionally) whether it quits on WM_CLOSE.
$ErrorActionPreference = 'Continue'

function Section($title) { Write-Host ""; Write-Host "== $title ==" -ForegroundColor Cyan }

Section "Windows"
[System.Environment]::OSVersion.VersionString
"PowerShell $($PSVersionTable.PSVersion)"

Section "Claude packages (Get-AppxPackage)"
$pkgs = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Claude*' -or $_.Name -like 'Anthropic*' }
if ($pkgs) { $pkgs | Select-Object Name, PackageFamilyName, Version, Architecture, InstallLocation | Format-List } else { "none" }

Section "Start menu entries (Get-StartApps) — the AppID is the AUMID used to relaunch"
$apps = Get-StartApps -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*Claude*' }
if ($apps) { $apps | Format-Table Name, AppID -AutoSize } else { "none" }

Section "Config file candidates"
$candidates = @("$env:APPDATA\Claude\claude_desktop_config.json")
Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -like 'Claude_*' -or $_.Name -like 'Anthropic.Claude*' } |
  ForEach-Object { $candidates += "$($_.FullName)\LocalCache\Roaming\Claude\claude_desktop_config.json" }
foreach ($p in $candidates) {
  if (Test-Path $p) { $i = Get-Item $p; "EXISTS  {0}  ({1} bytes, modified {2:u})" -f $p, $i.Length, $i.LastWriteTimeUtc }
  else { "missing $p" }
}

Section "command shapes in existing configs (name: command args)"
foreach ($p in $candidates) {
  if (-not (Test-Path $p)) { continue }
  "--- $p"
  try {
    $j = Get-Content $p -Raw | ConvertFrom-Json
    if ($j.mcpServers) {
      $j.mcpServers.PSObject.Properties | ForEach-Object {
        $a = @($_.Value.args) -join ' '
        "{0}: {1} {2}" -f $_.Name, $_.Value.command, $a
      }
    } else { "(no mcpServers key)" }
  } catch { "(unparseable)" }
}

Section "Legacy (Squirrel) install"
$legacy = "$env:LOCALAPPDATA\AnthropicClaude\claude.exe"
if (Test-Path $legacy) { "EXISTS  $legacy" } else { "missing $legacy" }

Section "Claude processes"
$procs = Get-Process | Where-Object { $_.ProcessName -like 'claude*' }
if ($procs) { $procs | Select-Object Id, ProcessName, StartTime, MainWindowTitle, Path | Format-Table -AutoSize } else { "none running" }

Section "node / npx on PATH"
foreach ($c in 'node', 'npx', 'npx.cmd') {
  $w = Get-Command $c -ErrorAction SilentlyContinue
  "{0}: {1}" -f $c, $(if ($w) { $w.Source } else { 'not found' })
}

if ($procs) {
  Section "Graceful-quit test"
  $answer = Read-Host "Claude is running. Post WM_CLOSE to its top-level windows to see whether it quits? This closes Claude. (y/N)"
  if ($answer -eq 'y') {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class Win {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  public static List<IntPtr> WindowsOf(HashSet<uint> pids) {
    var r = new List<IntPtr>();
    EnumWindows((h, l) => { uint pid; GetWindowThreadProcessId(h, out pid); if (pids.Contains(pid)) r.Add(h); return true; }, IntPtr.Zero);
    return r;
  }
}
"@
    $pids = New-Object 'System.Collections.Generic.HashSet[uint32]'
    $procs | ForEach-Object { [void]$pids.Add([uint32]$_.Id) }
    $wins = [Win]::WindowsOf($pids)
    "top-level windows: $($wins.Count)"
    foreach ($h in $wins) {
      $sb = New-Object System.Text.StringBuilder 256
      [void][Win]::GetWindowText($h, $sb, 256)
      "  hwnd {0} visible={1} title='{2}'" -f $h, [Win]::IsWindowVisible($h), $sb.ToString()
    }
    foreach ($h in $wins) { [void][Win]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }   # WM_CLOSE
    $deadline = (Get-Date).AddSeconds(15)
    do {
      Start-Sleep -Milliseconds 250
      $still = Get-Process | Where-Object { $_.ProcessName -like 'claude*' }
    } while ($still -and (Get-Date) -lt $deadline)
    if ($still) {
      "RESULT: Claude still running after 15 s (it hides to the tray or ignores WM_CLOSE)"
      $still | Select-Object Id, ProcessName, MainWindowTitle | Format-Table -AutoSize
    } else {
      "RESULT: Claude quit gracefully on WM_CLOSE"
    }
  }
}

Section "Done"
"Paste everything above back into the chat."
