$pidRoot = 'C:\DEPLOY\SmartMetrix\runtime\pids'
$configuration = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'services.psd1')
$allItems = @($configuration.Infrastructure | ForEach-Object { @{ Name=$_.Name; Port=$_.HealthPort } }) + @($configuration.Services)
$rows = foreach ($service in $allItems) {
    $pidFile = Join-Path $pidRoot "$($service.Name).pid"
    $processId = if (Test-Path $pidFile) { [int](Get-Content $pidFile) } else { 0 }
    $processRunning = $processId -gt 0 -and $null -ne (Get-Process -Id $processId -ErrorAction SilentlyContinue)
    $health = 'stopped'
    if ($processRunning) {
        try {
            $healthPath = if ($service.Name -eq 'nats') { '/healthz' } elseif ($service.Name -eq 'minio') { '/minio/health/live' } else { '/health' }
            $response = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$($service.Port)$healthPath" -TimeoutSec 2
            $health = if ($response.StatusCode -eq 200) { 'healthy' } else { "http-$($response.StatusCode)" }
        } catch { $health = 'starting/unhealthy' }
    }
    [pscustomobject]@{ Service = $service.Name; Port = $service.Port; PID = if ($processRunning) { $processId } else { '-' }; Status = $health }
}
$rows | Format-Table -AutoSize
