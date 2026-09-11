#Requires -Version 7
<#
.SYNOPSIS
    Publishes the Windows app for one runtime and packs it with Velopack.

.DESCRIPTION
    1. dotnet publish (self-contained, -p:Version=<Version>) into windows/artifacts/publish/<Runtime>
    2. `vpk pack` into windows/artifacts/<Runtime>:
         ConnectorControl-<Runtime>-Setup.exe
         ConnectorControl-<Version>-<Runtime>-full.nupkg
         releases.<Runtime>.json  RELEASES-<Runtime>  assets.<Runtime>.json
    The channel name IS the runtime identifier (spec §8.2): an installed app asks GitHub for
    releases.win-x64.json or releases.win-arm64.json and nothing else. Never a delta package:
    VelopackUpdater sets MaximumDeltasBeforeFallback = 0, so an installed client would never apply
    one anyway — producing one here would just be extra upload weight nobody downloads.

    vpk itself runs with its working directory set to windows/, whatever directory this script was
    invoked from (windows-build.yml runs it from the repo root): `dotnet` resolves a local tool's
    manifest by walking up from the CURRENT directory, and windows/.config/dotnet-tools.json — the
    manifest pinning vpk — is not an ancestor of the repo root. Every other path this script uses
    is already absolute by the time vpk runs, so that directory change affects nothing else.

.PARAMETER Version
    SemVer 2 version: 1.3.0 for a release, 1.3.0-preview.3 for a preview. Stamped into the assemblies
    (-p:Version) and the Velopack package (--packVersion) from this one value. A prerelease label
    makes the installed app follow prereleases (VelopackUpdater.FollowsPrereleases).
.PARAMETER Runtime
    win-x64 or win-arm64.
.PARAMETER ReleaseNotes
    Markdown file embedded in the package and shown by the in-app update dialog.
.PARAMETER RepoUrl
    The GitHub repository this build's update feed lives in: $env:GITHUB_REPOSITORY (every GitHub
    Actions runner sets it) mapped to its https URL, or the literal below for a local run off CI.
    Must agree with VelopackUpdater.RepoUrl — the app checks that URL's releases, not this one.
.PARAMETER AzureTrustedSignFile
    Azure Artifact Signing metadata.json ({Endpoint, CodeSigningAccountName, CertificateProfileName}).
    Windows only. Needs AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_SECRET in the environment.
    Omit for an unsigned build (vpk then warns "No signing parameters provided").
.PARAMETER Vpk
    How to invoke vpk, split on the first space: the default 'dotnet vpk' runs the local tool
    windows/.config/dotnet-tools.json pins (restored by `dotnet tool restore` in windows/) as
    "dotnet" plus a leading "vpk" argument; a bare 'vpk' (a global install) still works unsplit.
.PARAMETER CrossCompile
    Prefix vpk with the [win] directive: required when packing on macOS/Linux; signing is
    unavailable there.

.EXAMPLE
    ./windows/scripts/package.ps1 -Version 1.3.0-preview.1 -Runtime win-x64 -ReleaseNotes notes.md
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
    [string] $RepoUrl = $(
        if ($env:GITHUB_REPOSITORY) { "https://github.com/$env:GITHUB_REPOSITORY" }
        else { 'https://github.com/dlaporte/connector-control' }
    ),
    [string] $AzureTrustedSignFile,
    [string] $Vpk = 'dotnet vpk',
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

function Invoke-Native {
    param([string] $Label, [string] $Exe, [string[]] $Arguments)
    Write-Host ">> $Exe $($Arguments -join ' ')"
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path $icon)) { throw "Missing $icon — run: swift scripts/generate-icon.swift windows/assets/ConnectorControl.ico (on the Mac)" }

# 'dotnet vpk' splits into the exe to run and a leading argument in front of every other vpk
# argument below; a bare global install ('vpk') has nothing to prefix.
$vpkParts = $Vpk.Split([char[]]' ', 2)
$vpkExe = $vpkParts[0]
$vpkPrefixArgs = if ($vpkParts.Count -gt 1) { @($vpkParts[1]) } else { @() }

Write-Host "== Publish $Runtime, version $Version (feed: $RepoUrl)"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
Invoke-Native 'dotnet publish' 'dotnet' @(
    'publish', $project, '-c', 'Release', '-r', $Runtime, '--self-contained', 'true', "-p:Version=$Version", '-o', $publishDir)
$mainExe = Join-Path $publishDir 'ConnectorControl.exe'
if (-not (Test-Path $mainExe)) { throw "publish produced no $mainExe" }

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$vpkGlobal = @('--skip-updates', '--yes')        # no vpk self-update check (it stalled a Mac run for 5 min); no prompts
if ($CrossCompile) { $vpkGlobal = @('[win]') + $vpkGlobal }

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
# Every argument above is already resolved (an absolute path, or a value with no path in it), so
# it is safe to change directory now: vpk's own local-tool manifest lookup needs to run from
# windows/, not wherever this script itself was invoked from.
Push-Location $windowsDir
try {
    Invoke-Native 'vpk pack' $vpkExe ($vpkPrefixArgs + $packArgs)
}
finally {
    Pop-Location
}

$expected = @(
    "ConnectorControl-$Runtime-Setup.exe",
    "ConnectorControl-$Version-$Runtime-full.nupkg",
    "releases.$Runtime.json",
    "assets.$Runtime.json")
foreach ($name in $expected) {
    if (-not (Test-Path (Join-Path $outputDir $name))) { throw "vpk pack did not produce $name in $outputDir" }
}

Write-Host "== Packed into $outputDir"
Get-ChildItem $outputDir | Sort-Object Name | ForEach-Object { Write-Host ("   {0,12:N0}  {1}" -f $_.Length, $_.Name) }
