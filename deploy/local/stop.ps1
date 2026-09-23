$ErrorActionPreference = 'Stop'
$pidRoot = 'C:\DEPLOY\SmartMetrix\runtime\pids'
$stoppedMarker = 'C:\DEPLOY\SmartMetrix\runtime\intentionally-stopped'
. (Join-Path $PSScriptRoot 'process-state.ps1')
if (-not (Test-Path $pidRoot)) { Write-Host 'Local deployment is not running.'; exit 0 }

Get-ChildItem -LiteralPath $pidRoot -Filter '*.pid' | ForEach-Object {
    $state = Read-SmartMetrixProcessState -Path $_.FullName
    if ($state -and (Test-SmartMetrixProcessState -State $state)) {
        try {
            Stop-Process -Id $state.Pid -ErrorAction Stop
            Write-Host "Stopped $($_.BaseName) (PID $($state.Pid))"
        } catch [System.ComponentModel.Win32Exception] {
            throw "Cannot stop $($_.BaseName) (PID $($state.Pid)). Run stop.ps1 in the same user context that started SmartMetrix."
        }
    } elseif ($state) {
        Write-Warning "Ignored stale PID state for $($_.BaseName); no process was stopped."
    } else {
        Write-Warning "Ignored untrusted PID file $($_.Name); no process was stopped."
    }
    Remove-Item -LiteralPath $_.FullName -Force
}
Set-Content -LiteralPath $stoppedMarker -Value (Get-Date -Format o) -Encoding UTF8
