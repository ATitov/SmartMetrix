$ErrorActionPreference = 'Stop'
$deploymentRoot = 'C:\DEPLOY\SmartMetrix'
$statusScript = Join-Path $deploymentRoot 'status.ps1'
$startScript = Join-Path $deploymentRoot 'start.ps1'
if (-not (Test-Path $statusScript) -or -not (Test-Path $startScript)) { exit 2 }

$configuration = Import-PowerShellDataFile (Join-Path $deploymentRoot 'services.psd1')
$pidRoot = Join-Path $deploymentRoot 'runtime\pids'
$missing = $false
foreach ($item in @($configuration.Infrastructure) + @($configuration.Services)) {
    $pidFile = Join-Path $pidRoot "$($item.Name).pid"
    if (-not (Test-Path $pidFile)) { $missing = $true; break }
    $processId = [int](Get-Content $pidFile)
    if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) { $missing = $true; break }
}
if ($missing) { & $startScript }
& (Join-Path $deploymentRoot 'rotate-logs.ps1')
