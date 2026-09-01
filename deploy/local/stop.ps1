$ErrorActionPreference = 'Stop'
$pidRoot = 'C:\DEPLOY\SmartMetrix\runtime\pids'
if (-not (Test-Path $pidRoot)) { Write-Host 'Local deployment is not running.'; exit 0 }

Get-ChildItem -LiteralPath $pidRoot -Filter '*.pid' | ForEach-Object {
    $processId = [int](Get-Content $_.FullName)
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($process) {
        Stop-Process -Id $processId
        Write-Host "Stopped $($_.BaseName) (PID $processId)"
    }
    Remove-Item -LiteralPath $_.FullName -Force
}
