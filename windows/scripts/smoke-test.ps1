#Requires -Version 7
<#
.SYNOPSIS
    Installs a packed Setup.exe silently and proves the installed app starts and stays up.

.DESCRIPTION
    Runs on a Windows machine — the preview workflow's smoke job, or a tester's PC:
      1. INSTALL assertions, always run. Setup.exe --silent →
         %LOCALAPPDATA%\ConnectorControl\{Update.exe, current\ConnectorControl.exe, current\sq.version},
         with the version and channel read back out of sq.version. Spec §6.6 relies on
         current\ConnectorControl.exe being the stable path the Run key records: asserted here.
      2. LAUNCH assertions, skipped by -SkipLaunch. A silent Velopack install does not launch the
         app, so this starts current\ConnectorControl.exe against a throwaway Claude config and
         master-list folder (CONNECTOR_CONTROL_CLAUDE_CONFIG / CONNECTOR_CONTROL_STORE_DIR — the
         same env overrides AppPathsResolver honours), waits -Seconds, and asserts exactly four
         things:
           a. the process is still alive;
           b. no crash.log under %LOCALAPPDATA%\Connector Control;
           c. the first-run import wrote <store>\mcps.json;
           d. the process exits within 15 s of taskkill.
         Deliberately NOT asserted, because a passive first run does not produce them:
           * backups\claude_desktop_config.original.json — ConfigService.Apply is the only caller
             of EnsureOriginalSnapshot, and AppState.Reload calls PerformApply only when the file
             and the store disagree, which they do not immediately after an import. (It would also
             be in the wrong place: with CONNECTOR_CONTROL_STORE_DIR set, AppPaths puts backups
             under the sandbox store, not under %LOCALAPPDATA%\Connector Control.)
           * settings.json — written last of all, by FirstRunTip, so asserting it turns any
             tray-layer problem into a confusing store-layer failure.
         Both are on the manual PC checklist instead, after a real toggle.
      3. stops the process (a tray app has no main window to close) and copies logs to -LogDir.
    Prints "SMOKE PASS" and exits 0; any failed assertion throws.

.PARAMETER SetupExe      The ConnectorControl-<rid>-Setup.exe to install.
.PARAMETER Version       The version the package was built with (asserted against current\sq.version).
.PARAMETER Channel       The channel the package was built with (asserted against current\sq.version).
.PARAMETER Seconds       How long the app must stay alive.
.PARAMETER LogDir        Where setup.log, crash.log and the imported mcps.json are copied.
.PARAMETER ExpectSigned  Fail unless Setup.exe and the installed exe carry a valid Authenticode signature.
.PARAMETER SkipLaunch    Run the install assertions only. For a hosted CI runner with no
                         interactive desktop session, where App.OnStartup cannot complete.
                         Never pass it on a real PC: it removes the whole runtime check.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SetupExe,
    [Parameter(Mandatory)] [string] $Version,
    [ValidateSet('win-x64', 'win-arm64')] [string] $Channel = 'win-x64',
    [int] $Seconds = 20,
    [string] $LogDir = (Join-Path (Get-Location).Path 'smoke-logs'),
    [switch] $ExpectSigned,
    [switch] $SkipLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# `taskkill` without /F legitimately exits non-zero against a process with no window to close,
# and PowerShell 7.4+ (the runner has 7.6) would turn that into a terminating error while
# $ErrorActionPreference is 'Stop'. Native exit codes are handled explicitly below instead.
$PSNativeCommandUseErrorActionPreference = $false

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw "SMOKE FAIL: $Message" }
    Write-Host "ok   $Message"
}

$installRoot = Join-Path $env:LOCALAPPDATA 'ConnectorControl'          # Velopack app id (spec §4.2)
$dataDir = Join-Path $env:LOCALAPPDATA 'Connector Control'             # app data (spec §4.2)
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) "cc-smoke-$([guid]::NewGuid().ToString('N'))"
$store = Join-Path $sandbox 'store'
New-Item -ItemType Directory -Force -Path $sandbox, $store, $LogDir | Out-Null
# App.OnUnhandledException writes crash.log to ServiceFactory.DefaultDataDir, which is
# LocalAppData\Connector Control and does NOT follow CONNECTOR_CONTROL_STORE_DIR.
$crashLog = Join-Path $dataDir 'crash.log'
Remove-Item -Force -ErrorAction SilentlyContinue $crashLog

Write-Host "== Signature"
$setupSignature = Get-AuthenticodeSignature -FilePath $SetupExe
$signer = if ($null -ne $setupSignature.SignerCertificate) { $setupSignature.SignerCertificate.Subject } else { '(no certificate)' }
Write-Host "     $([System.IO.Path]::GetFileName($SetupExe)): $($setupSignature.Status) $signer"
if ($ExpectSigned) { Assert-True ($setupSignature.Status -eq 'Valid') "Setup.exe carries a valid Authenticode signature" }

Write-Host "== Silent install"
$setupLog = Join-Path $LogDir 'setup.log'
$setup = Start-Process -FilePath $SetupExe -ArgumentList @('--silent', '--log', $setupLog) -PassThru -Wait
Assert-True ($setup.ExitCode -eq 0) "Setup.exe --silent exited 0 (got $($setup.ExitCode))"
$exe = Join-Path $installRoot 'current/ConnectorControl.exe'
Assert-True (Test-Path $exe) "installed executable is $exe (the path RegistryAutostart records)"
Assert-True (Test-Path (Join-Path $installRoot 'Update.exe')) "Update.exe sits beside current\"
$manifest = Get-Content (Join-Path $installRoot 'current/sq.version') -Raw
Assert-True ($manifest -match "<version>$([regex]::Escape($Version))</version>") "sq.version carries version $Version"
Assert-True ($manifest -match "<channel>$([regex]::Escape($Channel))</channel>") "sq.version carries channel $Channel"
if ($ExpectSigned) {
    $exeSignature = Get-AuthenticodeSignature -FilePath $exe
    Assert-True ($exeSignature.Status -eq 'Valid') "installed ConnectorControl.exe is signed"
}

if ($SkipLaunch) {
    Copy-Item -Path (Join-Path $installRoot '*.log') -Destination $LogDir -ErrorAction SilentlyContinue
    Write-Host "== Launch check skipped (-SkipLaunch)"
    Write-Host "     The install layout above is verified; runtime behaviour is deferred to a real PC."
    Write-Host "SMOKE PASS (launch check skipped)"
    exit 0
}

Write-Host "== Launch against a sandbox config"
$claudeConfig = Join-Path $sandbox 'claude_desktop_config.json'
Set-Content -Path $claudeConfig -Encoding utf8 -Value '{"mcpServers":{"smoke":{"command":"npx","args":["-y","@example/smoke"]}}}'
$env:CONNECTOR_CONTROL_CLAUDE_CONFIG = $claudeConfig    # inherited by the child process
$env:CONNECTOR_CONTROL_STORE_DIR = $store               # the app's own override: the test owns where the store lands
$app = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds $Seconds
$alive = -not $app.HasExited
if (Test-Path $crashLog) {
    Write-Host "--- crash.log ---"
    Get-Content $crashLog | Write-Host
    Copy-Item $crashLog (Join-Path $LogDir 'crash.log')
}
Assert-True $alive "ConnectorControl.exe is still running after $Seconds s"
Assert-True (-not (Test-Path $crashLog)) "no crash.log under $dataDir"
$masterList = Join-Path $store 'mcps.json'
Assert-True (Test-Path $masterList) "the first-run import wrote $masterList"
# Logged, not asserted: what the store CONTAINS is covered by the Core tests and by the manual
# PC checklist. All this script needs is that the app got far enough to write it.
Write-Host "     mcps.json: $((Get-Content $masterList -Raw) -replace '\s+', ' ')"

Write-Host "== Stop"
$stopTimeoutMs = 15000
& taskkill.exe /PID $app.Id | Out-Null      # graceful first: posts WM_CLOSE to the process's windows
if (-not $app.WaitForExit($stopTimeoutMs)) {
    # Expected for a tray app: with no top-level window, taskkill reports "can only be
    # terminated forcefully" and exits non-zero. That is the normal path here, not a failure.
    Write-Host "     graceful taskkill did not stop it (a tray app has no window to close); forcing"
    & taskkill.exe /PID $app.Id /F | Out-Null
}
Assert-True ($app.WaitForExit($stopTimeoutMs)) "the process exited within $($stopTimeoutMs / 1000) s of taskkill"

Copy-Item -Path (Join-Path $installRoot '*.log') -Destination $LogDir -ErrorAction SilentlyContinue
Copy-Item -Path $masterList -Destination (Join-Path $LogDir 'mcps.json') -ErrorAction SilentlyContinue
Write-Host "SMOKE PASS"
