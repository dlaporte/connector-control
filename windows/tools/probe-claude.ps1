# Day-one probe for the Connector Control Windows port.
# Run in a normal (non-admin) PowerShell:   powershell -ExecutionPolicy Bypass -File .\probe-claude.ps1
# Reports how Claude Desktop is installed, where its config really lives, and the AUMID it can
# be relaunched by. SessionEnd.cs answers how Claude Desktop is asked to quit; this probe does not.
$ErrorActionPreference = 'Continue'

function Section($title) { Write-Host ""; Write-Host "== $title ==" -ForegroundColor Cyan }

Section "Windows"
[System.Environment]::OSVersion.VersionString
"PowerShell $($PSVersionTable.PSVersion)"

Section "Claude packages (Get-AppxPackage)"
$pkgs = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Claude*' -or $_.Name -like 'Anthropic*' }
if ($pkgs) { $pkgs | Select-Object Name, PackageFamilyName, Version, Architecture, InstallLocation | Format-List } else { "none" }

Section "Start menu entries (Get-StartApps) - the AppID is the AUMID used to relaunch"
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

Section "command shapes in existing configs (name: command first-two-args …)"
# Only the launcher shape is of interest here (npx / uvx / node / a path). The rest of args
# can carry tokens and OAuth client secrets, and env is not read at all: this output is meant
# to be pasted into a chat.
foreach ($p in $candidates) {
  if (-not (Test-Path $p)) { continue }
  "--- $p"
  try {
    $j = Get-Content $p -Raw | ConvertFrom-Json
    if ($j.mcpServers) {
      $j.mcpServers.PSObject.Properties | ForEach-Object {
        $all = @($_.Value.args)
        $shown = @($all | Select-Object -First 2) -join ' '
        $rest = if ($all.Count -gt 2) { " … (+{0} more args, not shown)" -f ($all.Count - 2) } else { "" }
        "{0}: {1} {2}{3}" -f $_.Name, $_.Value.command, $shown, $rest
      }
    } else { "(no mcpServers key)" }
  } catch { "(unparseable)" }
}

Section "Legacy (Squirrel) install"
$legacy = "$env:LOCALAPPDATA\AnthropicClaude\claude.exe"
if (Test-Path $legacy) { "EXISTS  $legacy" } else { "missing $legacy" }

Section "Signer of claude.exe (what Connector Control's Restart Claude check compares: the O= attribute)"
$exes = @($legacy)
if ($pkgs) { $pkgs | ForEach-Object { $exes += Join-Path $_.InstallLocation 'claude.exe' } }
foreach ($exe in $exes | Where-Object { Test-Path $_ }) {
  $sig = Get-AuthenticodeSignature $exe
  "{0}" -f $exe
  "  status:     {0}" -f $sig.Status
  "  subject:    {0}" -f $sig.SignerCertificate.Subject
  "  issuer:     {0}" -f $sig.SignerCertificate.Issuer
  "  thumbprint: {0}" -f $sig.SignerCertificate.Thumbprint
}
if ($pkgs) { $pkgs | ForEach-Object { "package {0}: Publisher={1}  PublisherId={2}" -f $_.Name, $_.Publisher, $_.PublisherId } }
"Paste the subject line into ClaudePublisher.ExpectedOrganizations (windows/src/ConnectorControl.Core/ClaudePublisher.cs) if its O= is spelt differently."

Section "Claude processes"
$procs = Get-Process | Where-Object { $_.ProcessName -like 'claude*' }
if ($procs) { $procs | Select-Object Id, ProcessName, StartTime, MainWindowTitle, Path | Format-Table -AutoSize } else { "none running" }

Section "node / npx on PATH"
foreach ($c in 'node', 'npx', 'npx.cmd') {
  $w = Get-Command $c -ErrorAction SilentlyContinue
  "{0}: {1}" -f $c, $(if ($w) { $w.Source } else { 'not found' })
}

Section "Done"
"Paste everything above back into the chat."
