$ErrorActionPreference = 'Stop'
$deploymentRoot = 'C:\DEPLOY\SmartMetrix'
. (Join-Path $PSScriptRoot 'process-state.ps1')
$statusScript = Join-Path $deploymentRoot 'status.ps1'
$startScript = Join-Path $deploymentRoot 'start.ps1'
if (-not (Test-Path $statusScript) -or -not (Test-Path $startScript)) { exit 2 }
if (Test-Path -LiteralPath (Join-Path $deploymentRoot 'runtime\intentionally-stopped')) {
    & (Join-Path $deploymentRoot 'rotate-logs.ps1')
    exit 0
}

$configuration = Import-PowerShellDataFile (Join-Path $deploymentRoot 'services.psd1')
$pidRoot = Join-Path $deploymentRoot 'runtime\pids'
$missing = $false
foreach ($item in @($configuration.Infrastructure) + @($configuration.Services)) {
    $pidFile = Join-Path $pidRoot "$($item.Name).pid"
    $state = Read-SmartMetrixProcessState -Path $pidFile
    if (-not $state -or -not (Test-SmartMetrixProcessState -State $state)) { $missing = $true; break }
}
if ($missing) { & $startScript }
& (Join-Path $deploymentRoot 'rotate-logs.ps1')
