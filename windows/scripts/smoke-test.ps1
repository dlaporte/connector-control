#Requires -Version 7
<#
.SYNOPSIS
    Installs a packed Setup.exe silently and proves the installed app starts and stays up; or,
    with -SignatureOnly, just checks a Setup.exe's Authenticode signature.

.DESCRIPTION
    windows-build.yml's smoke job, for both a release and a preview, runs this two ways:
      * The win-x64 package it just built: the full flow below.
      * The win-arm64 package, which cannot be installed on the x64 runner that job uses:
        -SignatureOnly -SetupExe <path> only - no install, no launch, just the signature check.

    The full flow:
      1. INSTALL assertions, always run. Setup.exe --silent →
         %LOCALAPPDATA%\ConnectorControl\{Update.exe, current\ConnectorControl.exe, current\sq.version},
         with the version and channel read back out of sq.version. The Run key relies on
         current\ConnectorControl.exe being the stable path it records: asserted here. So is the
         Start-menu shortcut's target: current\ConnectorControl.exe itself, not a launcher that runs Update.exe.
      2. LAUNCH assertions. This starts current\ConnectorControl.exe against a throwaway Claude
         config and master-list folder (CONNECTOR_CONTROL_CLAUDE_CONFIG / CONNECTOR_CONTROL_STORE_DIR -
         the same env overrides AppPaths honours), polls up to -Seconds for the first-run
         import to write <store>\mcps.json, and asserts:
           a. the process is still alive;
           b. no crash.log under %LOCALAPPDATA%\Connector Control;
           c. the first-run import wrote <store>\mcps.json;
           d. current\connector-control.exe exists and the app has copied it to
              %LOCALAPPDATA%\Connector Control\bin, byte for byte, without a word in crash.log;
           e. the copy says its version;
           f. connector-control.exe status --json answers through the pipe with this version;
           g. settings set claude-integration on writes Claude's entry naming the copy;
           h. the relay, fed a handshake and a call on standard input, answers the call.
         The relay is bounded: one that does not answer in time is killed and fails the run.
         Deliberately NOT asserted, because a passive first run does not produce them:
           * backups\claude_desktop_config.original.json - ConfigService.Apply is the only caller
             of EnsureOriginalSnapshot, and AppState.Reload calls PerformApply only when the file
             and the store disagree, which they do not immediately after an import. (It would also
             be in the wrong place: with CONNECTOR_CONTROL_STORE_DIR set, AppPaths puts backups
             under the sandbox store, not under %LOCALAPPDATA%\Connector Control.)
           * settings.json - written last of all, by FirstRunTip, so asserting it turns any
             tray-layer problem into a confusing store-layer failure.
         Both are on the manual PC checklist instead, after a real toggle.
      3. With -ExpectSigned: the installed exe's own signature, then a package verification -
         the installed exe checks this build's own full package with --verify-package, the same
         rule VelopackUpdater applies to a downloaded update. Running the app's own check here,
         rather than a second copy of it in PowerShell, means the two cannot drift apart.
      4. the real uninstall (Update.exe --uninstall --silent) with the app STILL RUNNING. Velopack
         ends the app's processes first and only then runs the uninstall hook, so the app is gone when the
         hook takes the entry out of Claude's config, the copy's folder out of the user PATH and deletes the
         copy. Asserted: the app exited, the entry is gone and the rest of the config kept, bin\ is gone,
         the PATH is clean, nothing wrote to Claude's config afterwards, the Start-menu shortcut is gone, and crash.log (in the data folder,
         which the uninstall keeps) is still absent, since the hook logs only a failure. (The logs are
         copied to -LogDir before the uninstall, which deletes the install root.)
    Prints "SMOKE PASS" and exits 0; any failed assertion throws.

.PARAMETER SetupExe      The ConnectorControl-<rid>-Setup.exe to check or install.
.PARAMETER SignatureOnly Check SetupExe's Authenticode signature and exit; no install, no launch.
                         For a runtime the current runner cannot install (arm64 on an x64 runner).
.PARAMETER Version       The version the package was built with (asserted against current\sq.version).
.PARAMETER Channel       The channel the package was built with (asserted against current\sq.version).
.PARAMETER Seconds       How long to wait for the first-run import before giving up.
.PARAMETER LogDir        Where setup.log, uninstall.log, crash.log and the imported mcps.json are copied.
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
# PowerShell 7.4+ (the runner has 7.6) turns a non-zero native exit code into a terminating error
# while $ErrorActionPreference is 'Stop'. Native exit codes are asserted explicitly below instead,
# so a failure names the command.
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
$setup = Start-Process -FilePath $SetupExe -ArgumentList @('--silent', '--log', "`"$setupLog`"") -PassThru -Wait
Assert-True ($setup.ExitCode -eq 0) "Setup.exe --silent exited 0 (got $($setup.ExitCode))"
$exe = Join-Path $installRoot 'current/ConnectorControl.exe'
Assert-True (Test-Path $exe) "installed executable is $exe (the path RegistryAutostart records)"
Assert-True (Test-Path (Join-Path $installRoot 'Update.exe')) "Update.exe sits beside current\"
$manifest = Get-Content (Join-Path $installRoot 'current/sq.version') -Raw
Assert-True ($manifest -match "<version>$([regex]::Escape($Version))</version>") "sq.version carries version $Version"
Assert-True ($manifest -match "<channel>$([regex]::Escape($Channel))</channel>") "sq.version carries channel $Channel"
# Velopack 1.2.x points the Start-menu shortcut at current\ConnectorControl.exe itself (shortcuts.rs, get_main_exe_path).
# The install root also gets a stub launcher named after the pack title, which starts Update.exe, but nothing Velopack
# makes points at it. Asserted because the updater's recovery (VelopackUpdater) counts on it: an Update.exe a refused
# update left in place is run only by an uninstall or an apply, never by an ordinary launch.
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Connector Control.lnk'
Assert-True (Test-Path $startMenuShortcut) "the Start-menu shortcut is $startMenuShortcut"
$link = (New-Object -ComObject WScript.Shell).CreateShortcut($startMenuShortcut)
Write-Host "     shortcut: $($link.TargetPath) in $($link.WorkingDirectory)"
Assert-True ([System.IO.Path]::GetFullPath($link.TargetPath) -eq [System.IO.Path]::GetFullPath($exe)) "the shortcut starts $exe, not a launcher that runs Update.exe (got '$($link.TargetPath)')"
$stubLauncher = Join-Path $installRoot 'Connector Control.exe'
Write-Host "     stub launcher at the install root: $(if (Test-Path $stubLauncher) { $stubLauncher } else { '(none)' })"
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
    $verify = Start-Process -FilePath $exe -ArgumentList @('--verify-package', "`"$nupkg`"", '--report', "`"$report`"") -PassThru -Wait
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

Write-Host "== The command-line tool"
$tool = Join-Path $installRoot 'current/connector-control.exe'
$bin = Join-Path $dataDir 'bin'
$binTool = Join-Path $bin 'connector-control.exe'
Assert-True (Test-Path $tool) "connector-control.exe sits beside the app ($tool)"
# The app copies it off the UI thread at launch.
$deadline = (Get-Date).AddSeconds($Seconds)
while (-not (Test-Path $binTool) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
Assert-True (Test-Path $binTool) "the app keeps its copy at $binTool"
Assert-True ((Get-FileHash $tool).Hash -eq (Get-FileHash $binTool).Hash) "the copy is the packaged tool, byte for byte"
# Only a failed copy is logged.
Assert-True (-not (Test-Path $crashLog)) "the copy left nothing in crash.log"
if ($ExpectSigned) {
    Assert-True ((Get-AuthenticodeSignature -FilePath $tool).Status -eq 'Valid') "connector-control.exe is signed"
}
$versionLine = (& $binTool --version) -join ''
Assert-True ($versionLine -eq "connector-control $Version") "the copy says its version (got '$versionLine')"
# CONNECTOR_CONTROL_STORE_DIR, set above and inherited, names the pipe the sandboxed app serves, and keeps
# the tool from starting any other app.
$statusJson = (& $tool status --json) -join "`n"
Assert-True ($LASTEXITCODE -eq 0) "connector-control.exe status --json exited 0 (got ${LASTEXITCODE}: $statusJson)"
$toolStatus = $statusJson | ConvertFrom-Json
# Read safely: under strict mode a missing property (an error-shaped answer) would throw without the output.
$toolVersion = if ($toolStatus.PSObject.Properties.Name -contains 'version') { $toolStatus.version } else { $null }
Assert-True ($toolVersion -eq $Version) "status --json says version $Version (got '$statusJson')"
$integration = if ($toolStatus.PSObject.Properties.Name -contains 'integration') { $toolStatus.integration } else { $null }
$integrationOn = if ($null -ne $integration -and $integration.PSObject.Properties.Name -contains 'enabled') { $integration.enabled } else { $null }
Assert-True ($integrationOn -eq $false) "the integration starts off (got '$statusJson')"

Write-Host "== Turning the integration on from the command line"
& $binTool settings set claude-integration on | Out-Null
Assert-True ($LASTEXITCODE -eq 0) "settings set claude-integration on exited 0 (got $LASTEXITCODE)"
function Get-Servers { (Get-Content $claudeConfig -Raw | ConvertFrom-Json).mcpServers }
function Get-ServerNames { @((Get-Servers).PSObject.Properties.Name) }
# Under strict mode a missing property would throw before the assertion could say anything.
$entry = if ((Get-ServerNames) -contains 'connector-control') { (Get-Servers).'connector-control' } else { $null }
Assert-True ($null -ne $entry -and $entry.command -eq $binTool -and (@($entry.args) -join ',') -eq 'mcp') `
    "Claude's config names the copy (got $($entry | ConvertTo-Json -Compress))"

Write-Host "== The relay, as Claude runs it"
$relayInput = @(
    '{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"claude-ai","version":"0.1.0"}}}',
    '{"jsonrpc":"2.0","method":"notifications/initialized"}',
    '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_status","arguments":{}}}'
) -join "`n"
# Closing standard input ends the relay once the call is answered; a relay that never answers is killed
# after $relayTimeoutMs instead of holding the job until its own timeout.
$relayTimeoutMs = 30000
$relayStart = [System.Diagnostics.ProcessStartInfo]::new($binTool, 'mcp')
$relayStart.RedirectStandardInput = $true
$relayStart.RedirectStandardOutput = $true
$relayStart.RedirectStandardError = $true
$relayStart.UseShellExecute = $false
$relayStart.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
$relayProcess = [System.Diagnostics.Process]::Start($relayStart)
$relayStdout = $relayProcess.StandardOutput.ReadToEndAsync()
$relayStderr = $relayProcess.StandardError.ReadToEndAsync()
$relayProcess.StandardInput.Write($relayInput + "`n")
$relayProcess.StandardInput.Close()
if (-not $relayProcess.WaitForExit($relayTimeoutMs)) {
    $relayProcess.Kill($true)
    throw "SMOKE FAIL: the relay did not end within $($relayTimeoutMs / 1000) s of its input closing"
}
$relayProcess.WaitForExit()
Assert-True ($relayProcess.ExitCode -eq 0) "the relay exited 0 once its input closed (got $($relayProcess.ExitCode): $($relayStderr.GetAwaiter().GetResult()))"
$relayOutput = @(($relayStdout.GetAwaiter().GetResult()) -split "`r?`n" | Where-Object { $_ })
$answer = @($relayOutput | Where-Object { $_ -like '{"id":1,*' })
$answerVersion = $null
if ($answer.Count -eq 1) {
    $parsed = $answer[0] | ConvertFrom-Json
    # Walked one property at a time: an error answer has no result, and strict mode would throw without the output.
    $node = $parsed
    foreach ($name in 'result', 'structuredContent', 'version') {
        if ($null -eq $node -or $node -isnot [pscustomobject] -or $node.PSObject.Properties.Name -notcontains $name) { $node = $null; break }
        $node = $node.$name
    }
    $answerVersion = $node
}
Assert-True ($answerVersion -eq $Version) `
    "the relay answered get_status (got $($relayOutput -join ' | ') $($relayStderr.GetAwaiter().GetResult()))"

Write-Host "== The uninstall, with the app still running"
# The install root goes with the uninstall: its logs are copied first. crash.log lives in the data folder,
# which the uninstall keeps, so it is read afterwards.
Copy-Item -Path (Join-Path $installRoot '*.log') -Destination $LogDir -ErrorAction SilentlyContinue
Copy-Item -Path $masterList -Destination (Join-Path $LogDir 'mcps.json') -ErrorAction SilentlyContinue
# The copy's folder goes on the user PATH first, as Install Command-Line Tool puts it there.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($null -eq $userPath) { $userPath = '' }
[Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ';' + $bin).TrimStart(';'), 'User')
Assert-True (-not $app.HasExited) "the app is still running as the uninstall starts"
# What Windows runs for Quiet Uninstall. Velopack ends the app's processes first and only then runs the app's
# uninstall hook, so no running app is left to write the entry back.
$uninstallLog = Join-Path $LogDir 'uninstall.log'
$uninstall = Start-Process -FilePath (Join-Path $installRoot 'Update.exe') -ArgumentList @('--uninstall', '--silent', '--log', "`"$uninstallLog`"") -PassThru -Wait
# Carries little by itself: the assertions below are what prove the hook ran.
Write-Host "     Update.exe --uninstall exited $($uninstall.ExitCode)"
$uninstallText = if (Test-Path $uninstallLog) { Get-Content $uninstallLog -Raw } else { '' }
Assert-True ($uninstall.ExitCode -eq 0) "Update.exe --uninstall exited 0 (got $($uninstall.ExitCode)); log: $uninstallText"
# Velopack's own line for a hook that ran and succeeded; the other two are its words for one that did not.
if ($uninstallText -match 'Hook exited with non-zero exit code|Failed to start hook') { Write-Host $uninstallText }
Assert-True (($uninstallText -notmatch 'Hook exited with non-zero exit code|Failed to start hook') -and ($uninstallText -match 'Hook executed successfully')) "Velopack ran the uninstall hook and it succeeded (see $uninstallLog)"
Assert-True ($app.WaitForExit(15000)) "the uninstall ended the running app"
Assert-True (-not ((Get-ServerNames) -contains 'connector-control')) "the uninstall hook took the entry out of Claude's config"
Assert-True ((Get-ServerNames) -contains 'smoke') "and left the rest of it"
Assert-True (-not (Test-Path $bin)) "the hook deleted $bin"
$pathAfter = [Environment]::GetEnvironmentVariable('Path', 'User')
Assert-True (-not (($pathAfter -split ';') -contains $bin)) "the hook took $bin out of the user PATH"
# Nothing may write to Claude's config after the hook: the window passes only if the file is byte for byte
# what the hook left, so "nothing put the entry back" is a measured fact rather than an absence of looking.
$configAfterHook = (Get-FileHash $claudeConfig).Hash
$writtenAfterHook = (Get-Item $claudeConfig).LastWriteTimeUtc
$settleUntil = (Get-Date).AddSeconds(8)
while ((Get-Date) -lt $settleUntil) { Start-Sleep -Milliseconds 500 }
Assert-True ((Get-FileHash $claudeConfig).Hash -eq $configAfterHook -and (Get-Item $claudeConfig).LastWriteTimeUtc -eq $writtenAfterHook) "nothing touched Claude's config after the hook"
Assert-True (-not ((Get-ServerNames) -contains 'connector-control')) "and the entry has not come back"
Assert-True (-not (Test-Path $bin)) "nor the copy"
Assert-True (-not (Test-Path $startMenuShortcut)) "the uninstall took the Start-menu shortcut"

# A clean run says nothing to crash.log: not the copy, not the hook (which logs only a failure).
if (Test-Path $crashLog) {
    Write-Host "--- crash.log ---"
    Get-Content $crashLog | Write-Host
    Copy-Item $crashLog (Join-Path $LogDir 'crash.log')
}
Assert-True (-not (Test-Path $crashLog)) "still no crash.log after the uninstall"

Write-Host "SMOKE PASS"
# Explicit: the CI shell ends with $LASTEXITCODE, which a native command above may have left non-zero.
exit 0
