#Requires -Version 7
<#
.SYNOPSIS
    Drives connector-control.exe mcp with the official MCP Inspector against the headless test host.

.DESCRIPTION
    The Windows twin of scripts/mac/inspector-check.sh: the test host runs the real control layer over a
    sandbox folder and serves its pipe; the pinned Inspector starts the relay with
    CONNECTOR_CONTROL_STORE_DIR pointing into the sandbox — which names the test host's pipe and keeps the
    relay from starting any real app — and checks tools/list (strictly), get_status, list_connectors, and
    that restart_claude, which the test host declines, exits 5 (isError).

.PARAMETER Tool
    connector-control.exe.
.PARAMETER TestHost
    ConnectorControl.TestHost.exe.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Tool,
    [Parameter(Mandatory)] [string] $TestHost
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# Pinned, as scripts/mac/inspector-check.sh pins it: the CLI's flags changed between major versions.
$inspector = '@modelcontextprotocol/inspector@2.12.0'
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) "cc-inspect-$([guid]::NewGuid().ToString('N'))"
$store = Join-Path $sandbox 'store'
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
$env:MCP_INSPECTOR_SECRET_STORE = 'memory'
$env:MCP_STORAGE_DIR = Join-Path $sandbox 'inspector'
$env:MCP_CATALOG_PATH = Join-Path $sandbox 'inspector/mcp.json'
$env:MCP_CLIENT_CONFIG_PATH = Join-Path $sandbox 'inspector/client.json'
New-Item -ItemType Directory -Force -Path $env:MCP_STORAGE_DIR | Out-Null

function Invoke-Inspector {
    param([string[]] $Arguments)
    $all = @('--yes', $inspector, '--cli', '--format', 'json', '-q', '-e', "CONNECTOR_CONTROL_STORE_DIR=$store")
    # The Inspector hands its server only a few variables; a framework-dependent build needs to find .NET.
    if ($env:DOTNET_ROOT) { $all += @('-e', "DOTNET_ROOT=$env:DOTNET_ROOT") }
    $all += $Arguments + @('--', $Tool, 'mcp')
    $output = & npx @all
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($output -join "`n") }
}

function Get-Result {
    param([string] $Json)
    $value = $Json | ConvertFrom-Json
    if ($value.PSObject.Properties.Name -contains 'result') { $value.result } else { $value }
}

$hostOut = Join-Path $sandbox 'host.out'
$hostErr = Join-Path $sandbox 'host.err'
$testHostProcess = Start-Process -FilePath $TestHost -ArgumentList @($sandbox) -RedirectStandardOutput $hostOut `
    -RedirectStandardError $hostErr -PassThru -NoNewWindow
try {
    $deadline = (Get-Date).AddSeconds(20)
    while (-not ((Test-Path $hostOut) -and (Select-String -Path $hostOut -Pattern '^ready ' -Quiet))) {
        if ($testHostProcess.HasExited) { Get-Content $hostErr | Write-Host; throw 'the test host exited before it was ready' }
        if ((Get-Date) -gt $deadline) { throw 'the test host never said it was ready' }
        Start-Sleep -Milliseconds 100
    }

    Write-Host '== tools/list, with the Inspector''s strict schema check'
    $list = Invoke-Inspector @('--method', 'tools/list', '--strict')
    if ($list.Code -ne 0) { throw "tools/list exited $($list.Code): $($list.Text)" }
    $tools = @((Get-Result $list.Text).tools)
    if ($tools.Count -ne 24) { throw "expected 24 tools, got $($tools.Count)" }
    if (@($tools | Where-Object { $_.annotations.readOnlyHint -eq $true }).Count -ne 8) { throw 'expected 8 read-only tools' }

    Write-Host '== tools/call get_status'
    $status = Invoke-Inspector @('--method', 'tools/call', '--tool-name', 'get_status')
    $statusResult = Get-Result $status.Text
    if ($status.Code -ne 0 -or $statusResult.isError -or -not ($statusResult.structuredContent.version -is [string])) {
        throw "get_status did not answer: $($status.Text)"
    }

    Write-Host '== tools/call list_connectors'
    $connectors = Invoke-Inspector @('--method', 'tools/call', '--tool-name', 'list_connectors')
    $names = @((Get-Result $connectors.Text).structuredContent.connectors | ForEach-Object { $_.name } | Sort-Object)
    if (($names -join ',') -ne 'fetch,time') { throw "list_connectors did not list the seed: $($connectors.Text)" }

    Write-Host '== restart_claude, which always asks and which the test host declines'
    $declined = Invoke-Inspector @('--method', 'tools/call', '--tool-name', 'restart_claude')
    if ($declined.Code -ne 5) { throw "a declined change should exit 5 (isError), got $($declined.Code)" }

    Write-Host 'Inspector check passed'
}
finally {
    if (-not $testHostProcess.HasExited) { Stop-Process -Id $testHostProcess.Id -Force }
    Remove-Item -Recurse -Force $sandbox -ErrorAction SilentlyContinue
}

# Reached only when every check passed (a failed one throws, which fails the step). The last native
# command ran restart_claude, which exits 5 on purpose, and a CI step ends with that code unless told.
exit 0
