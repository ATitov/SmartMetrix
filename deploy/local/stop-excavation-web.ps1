[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$workspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'process-state.ps1')
$pidPath = Join-Path $workspaceRoot 'artifacts/excavation-web/gateway.pid.json'
$state = Read-SmartMetrixProcessState -Path $pidPath
if ($state -and (Test-SmartMetrixProcessState $state)) {
    Stop-Process -Id $state.Pid
    Write-Host 'Excavation web demo stopped.'
}
else { Write-Host 'No matching excavation web process is running.' }
