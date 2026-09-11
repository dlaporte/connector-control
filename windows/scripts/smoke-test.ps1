#Requires -Version 7
<#
.SYNOPSIS
    Installs a packed Setup.exe silently and proves the installed app starts and stays up; or,
    with -SignatureOnly, just checks a Setup.exe's Authenticode signature.

.DESCRIPTION
    windows-build.yml's smoke job, for both a release and a preview, runs this two ways:
      * The win-x64 package it just built: the full flow below.
      * The win-arm64 package, which cannot be installed on the x64 runner that job uses:
        -SignatureOnly -SetupExe <path> only — no install, no launch, just the signature check.

    The full flow:
      1. INSTALL assertions, always run. Setup.exe --silent →
         %LOCALAPPDATA%\ConnectorControl\{Update.exe, current\ConnectorControl.exe, current\sq.version},
         with the version and channel read back out of sq.version. The Run key relies on
         current\ConnectorControl.exe being the stable path it records: asserted here.
      2. LAUNCH assertions. This starts current\ConnectorControl.exe against a throwaway Claude
         config and master-list folder (CONNECTOR_CONTROL_CLAUDE_CONFIG / CONNECTOR_CONTROL_STORE_DIR
         — the same env overrides AppPaths honours), polls up to -Seconds for the first-run
         import to write <store>\mcps.json, and asserts exactly four things:
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
      3. With -ExpectSigned: the installed exe's own signature, then a package verification —
         the installed exe checks this build's own full package with --verify-package, the same
         rule VelopackUpdater applies to a downloaded update. Running the app's own check here,
         rather than a second copy of it in PowerShell, means the two cannot drift apart.
      4. stops the process (a tray app has no main window to close) and copies logs to -LogDir.
    Prints "SMOKE PASS" and exits 0; any failed assertion throws.

.PARAMETER SetupExe      The ConnectorControl-<rid>-Setup.exe to check or install.
.PARAMETER SignatureOnly Check SetupExe's Authenticode signature and exit; no install, no launch.
                         For a runtime the current runner cannot install (arm64 on an x64 runner).
.PARAMETER Version       The version the package was built with (asserted against current\sq.version).
.PARAMETER Channel       The channel the package was built with (asserted against current\sq.version).
.PARAMETER Seconds       How long to wait for the first-run import before giving up.
.PARAMETER LogDir        Where setup.log, crash.log and the imported mcps.json are copied.
.PARAMETER ExpectSigned  Fail unless Setup.exe, the installed exe, and this build's own full
                         package all pass the app's own update-signature check.
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory)] [string] $SetupExe,

    [Parameter(Mandatory, ParameterSetName = 'SignatureOnly')] [switch] $SignatureOnly,

    [Parameter(Mandatory, ParameterSetName = 'Install')] [string] $Version,
    [Parameter(ParameterSetName = 'Install')] [ValidateSet('win-x64', 'win-arm64')] [string] $Channel = 'win-x64',
    [Parameter(ParameterSetName = 'Install')] [int] $Seconds = 20,
    [Parameter(ParameterSetName = 'Install')] [string] $LogDir = (Join-Path (Get-Location).Path 'smoke-logs'),
    [Parameter(ParameterSetName = 'Install')] [switch] $ExpectSigned
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

if ($SignatureOnly) {
    Write-Host "== Signature ($([System.IO.Path]::GetFileName($SetupExe)))"
    $sig = Get-AuthenticodeSignature -FilePath $SetupExe
    $signer = if ($null -ne $sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '(no certificate)' }
    Write-Host "     $([System.IO.Path]::GetFileName($SetupExe)): $($sig.Status) $signer"
    Assert-True ($sig.Status -eq 'Valid') "$([System.IO.Path]::GetFileName($SetupExe)) carries a valid Authenticode signature"
    Write-Host "SMOKE PASS (signature only)"
    exit 0
}

$installRoot = Join-Path $env:LOCALAPPDATA 'ConnectorControl'          # Velopack app id: the install root
$dataDir = Join-Path $env:LOCALAPPDATA 'Connector Control'             # app data
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

    Write-Host "== Package verification"
    # The exact rule VelopackUpdater applies to a downloaded update, run by the installed app
    # itself against this build's own full package: proves a real update from this release would
    # be accepted, without a second copy of UpdateVerifier's rule transcribed here to drift from it.
    $nupkg = Join-Path (Split-Path -Parent $SetupExe) "ConnectorControl-$Version-$Channel-full.nupkg"
    Assert-True (Test-Path $nupkg) "full package sits beside Setup.exe ($nupkg)"
    $report = Join-Path $sandbox 'verify-report.txt'
    $verify = Start-Process -FilePath $exe -ArgumentList @('--verify-package', $nupkg, '--report', $report) -PassThru -Wait
    $reportText = if (Test-Path $report) { (Get-Content $report -Raw).Trim() } else { '(no report written)' }
    Write-Host "     $reportText"
    Assert-True ($verify.ExitCode -eq 0) "--verify-package exited 0 (got $($verify.ExitCode))"
    # A passing report is "OK" followed by a line naming which checks ran; only the first line matters here.
    Assert-True ($reportText -match '^OK') "the package verification report says OK (got '$reportText')"
}

Write-Host "== Launch against a sandbox config"
$claudeConfig = Join-Path $sandbox 'claude_desktop_config.json'
Set-Content -Path $claudeConfig -Encoding utf8 -Value '{"mcpServers":{"smoke":{"command":"npx","args":["-y","@example/smoke"]}}}'
$env:CONNECTOR_CONTROL_CLAUDE_CONFIG = $claudeConfig    # inherited by the child process
$env:CONNECTOR_CONTROL_STORE_DIR = $store               # the app's own override: the test owns where the store lands
$app = Start-Process -FilePath $exe -PassThru
$masterList = Join-Path $store 'mcps.json'
$pollIntervalMs = 250
$deadline = (Get-Date).AddSeconds($Seconds)
# Polling instead of a flat sleep: the common case (the import finishes in well under $Seconds)
# returns as soon as it is done instead of always paying for the worst case.
do {
    Start-Sleep -Milliseconds $pollIntervalMs
} while (-not (Test-Path $masterList) -and -not $app.HasExited -and (Get-Date) -lt $deadline)
$alive = -not $app.HasExited
if (Test-Path $crashLog) {
    Write-Host "--- crash.log ---"
    Get-Content $crashLog | Write-Host
    Copy-Item $crashLog (Join-Path $LogDir 'crash.log')
}
Assert-True $alive "ConnectorControl.exe is still running after up to $Seconds s"
Assert-True (-not (Test-Path $crashLog)) "no crash.log under $dataDir"
Assert-True (Test-Path $masterList) "the first-run import wrote $masterList within $Seconds s"
# Logged, not asserted: what the store CONTAINS is covered by the Core tests and by the manual
# PC checklist. All this script needs is that the app got far enough to write it.
Write-Host "     mcps.json: $((Get-Content $masterList -Raw) -replace '\s+', ' ')"

Write-Host "== Stop"
$stopTimeoutMs = 15000   # generous margin over how long a graceful WM_CLOSE-style shutdown should ever take
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
