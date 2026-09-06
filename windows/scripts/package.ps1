#Requires -Version 7
<#
.SYNOPSIS
    Publishes the Windows app for one runtime and packs it with Velopack (vpk 1.2.0).

.DESCRIPTION
    1. dotnet publish (self-contained, -p:Version=<Version>) into windows/artifacts/publish/<Runtime>
    2. optionally `vpk download github` — the previous full package of the same channel, so vpk
       can also produce a delta package (exits 0 with "No full / applicable release" on the first
       release of a channel). That downloaded package is deleted again after the pack, so the
       output folder only ever holds this build's own files.
    3. `vpk pack` into windows/artifacts/<Runtime>:
         ConnectorControl-<Runtime>-Setup.exe
         ConnectorControl-<Version>-<Runtime>-full.nupkg  (+ -delta.nupkg when step 2 found one)
         releases.<Runtime>.json  RELEASES-<Runtime>  assets.<Runtime>.json
    The channel name IS the runtime identifier (spec §8.2): an installed app asks GitHub for
    releases.win-x64.json or releases.win-arm64.json and nothing else.

.PARAMETER Version
    SemVer 2 version: 1.3.0 for a release, 1.3.0-preview.3 for a preview. Stamped into the assemblies
    (-p:Version) and the Velopack package (--packVersion) from this one value. A prerelease label
    makes the installed app follow prereleases (VelopackUpdater.FollowsPrereleases).
.PARAMETER Runtime
    win-x64 or win-arm64.
.PARAMETER ReleaseNotes
    Markdown file embedded in the package and shown by the in-app update dialog.
.PARAMETER DownloadPrevious
    Fetch the previous release of this channel first (delta generation). Reads env VPK_TOKEN.
.PARAMETER Prerelease
    With -DownloadPrevious: prereleases count as "previous release".
.PARAMETER AzureTrustedSignFile
    Azure Artifact Signing metadata.json ({Endpoint, CodeSigningAccountName, CertificateProfileName}).
    Windows only. Needs AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_SECRET in the environment.
    Omit for an unsigned build (vpk then warns "No signing parameters provided").
.PARAMETER Vpk
    The vpk executable (default: vpk on PATH — `dotnet tool install -g vpk --version 1.2.0`).
.PARAMETER CrossCompile
    Prefix vpk with the [win] directive: required when packing on macOS/Linux; signing is
    unavailable there.

.EXAMPLE
    ./windows/scripts/package.ps1 -Version 1.3.0-preview.1 -Runtime win-x64 -ReleaseNotes notes.md -DownloadPrevious -Prerelease
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime,

    [string] $ReleaseNotes,
    [switch] $DownloadPrevious,
    [switch] $Prerelease,
    [string] $AzureTrustedSignFile,
    [string] $Vpk = 'vpk',
    [switch] $CrossCompile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# PowerShell 7.4+ turns a non-zero native exit code into a terminating error whenever
# $ErrorActionPreference is 'Stop' (the runner has 7.6). Turn that off so Invoke-Native's
# own $LASTEXITCODE check reports which command failed instead of a generic message.
$PSNativeCommandUseErrorActionPreference = $false

$windowsDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $windowsDir 'src/ConnectorControl.App/ConnectorControl.App.csproj'
$icon = Join-Path $windowsDir 'assets/ConnectorControl.ico'
$publishDir = Join-Path $windowsDir "artifacts/publish/$Runtime"
$outputDir = Join-Path $windowsDir "artifacts/$Runtime"
$repoUrl = 'https://github.com/dlaporte/connector-control'

function Invoke-Native {
    param([string] $Label, [string] $Exe, [string[]] $Arguments)
    Write-Host ">> $Exe $($Arguments -join ' ')"
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path $icon)) { throw "Missing $icon — run: swift scripts/generate-ico.swift windows/assets/ConnectorControl.ico (on the Mac)" }

Write-Host "== Publish $Runtime, version $Version"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
Invoke-Native 'dotnet publish' 'dotnet' @(
    'publish', $project, '-c', 'Release', '-r', $Runtime, '--self-contained', 'true', "-p:Version=$Version", '-o', $publishDir)
$mainExe = Join-Path $publishDir 'ConnectorControl.exe'
if (-not (Test-Path $mainExe)) { throw "publish produced no $mainExe" }

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$vpkGlobal = @('--skip-updates', '--yes')        # no vpk self-update check (it stalled a Mac run for 5 min); no prompts
if ($CrossCompile) { $vpkGlobal = @('[win]') + $vpkGlobal }

if ($DownloadPrevious) {
    Write-Host "== Previous $Runtime release (for a delta package)"
    $downloadArgs = $vpkGlobal + @('download', 'github', '--repoUrl', $repoUrl, '--channel', $Runtime, '--outputDir', $outputDir)
    if ($Prerelease) { $downloadArgs += '--pre' }
    Invoke-Native 'vpk download github' $Vpk $downloadArgs
}

Write-Host "== vpk pack $Runtime"
$packArgs = $vpkGlobal + @(
    'pack',
    '--packId', 'ConnectorControl',            # install root %LOCALAPPDATA%\ConnectorControl (spec §4.2)
    '--packVersion', $Version,
    '--packDir', $publishDir,
    '--mainExe', 'ConnectorControl.exe',
    '--packTitle', 'Connector Control',
    '--packAuthors', 'David LaPorte',
    '--icon', $icon,
    '--runtime', $Runtime,
    '--channel', $Runtime,                     # channel = RID: releases.<rid>.json on the GitHub release
    '--outputDir', $outputDir,
    '--shortcuts', 'StartMenuRoot',            # a tray app: Start menu entry, no desktop icon
    '--noPortable'                             # spec §8.2 lists Setup.exe + nupkgs + index only
)
if ($ReleaseNotes) { $packArgs += @('--releaseNotes', (Resolve-Path $ReleaseNotes).Path) }
if ($AzureTrustedSignFile) { $packArgs += @('--azureTrustedSignFile', (Resolve-Path $AzureTrustedSignFile).Path) }
Invoke-Native 'vpk pack' $Vpk $packArgs

$expected = @(
    "ConnectorControl-$Runtime-Setup.exe",
    "ConnectorControl-$Version-$Runtime-full.nupkg",
    "releases.$Runtime.json",
    "assets.$Runtime.json")
foreach ($name in $expected) {
    if (-not (Test-Path (Join-Path $outputDir $name))) { throw "vpk pack did not produce $name in $outputDir" }
}

# -DownloadPrevious left the PREVIOUS release's full package in $outputDir so that vpk
# could diff against it. The delta exists now, and `vpk upload` uploads only what
# assets.<Runtime>.json lists (this build's files), so the old package is dead weight —
# about 70 MB of it in every uploaded CI artifact. Drop it.
Get-ChildItem $outputDir -Filter '*.nupkg' |
    Where-Object { $_.Name -notlike "ConnectorControl-$Version-$Runtime-*.nupkg" } |
    ForEach-Object {
        Write-Host "   pruning $($_.Name) (previous release; already consumed by the delta)"
        Remove-Item -Force $_.FullName
    }

Write-Host "== Packed into $outputDir"
Get-ChildItem $outputDir | Sort-Object Name | ForEach-Object { Write-Host ("   {0,12:N0}  {1}" -f $_.Length, $_.Name) }
