[CmdletBinding()]
param([switch]$Build, [switch]$TestMode)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'process-state.ps1')
$deploymentRoot = 'C:\DEPLOY\SmartMetrix'
$runtimeRoot = Join-Path $deploymentRoot 'runtime'
$stoppedMarker = Join-Path $runtimeRoot 'intentionally-stopped'
$deploymentLogRoot = 'C:\DEPLOY_LOG'
$logRoot = Join-Path $runtimeRoot 'logs'
$pidRoot = Join-Path $runtimeRoot 'pids'
$securityRoot = Join-Path $runtimeRoot 'security'
$secretRoot = Join-Path $deploymentRoot 'secrets'
$configuration = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'services.psd1')
New-Item -ItemType Directory -Force -Path $logRoot, $pidRoot, $securityRoot, $secretRoot, $deploymentLogRoot | Out-Null
if (Test-Path -LiteralPath $stoppedMarker) { Remove-Item -LiteralPath $stoppedMarker -Force }
$bootstrapFile = Join-Path $secretRoot 'bootstrap-admin.txt'
if (-not (Test-Path -LiteralPath $bootstrapFile)) {
    $bootstrapPassword = 'Sm!' + [Guid]::NewGuid().ToString('N')
    Set-Content -LiteralPath $bootstrapFile -Value "login=admin`npassword=$bootstrapPassword" -Encoding UTF8
} else {
    $passwordLine = Get-Content -LiteralPath $bootstrapFile | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    $bootstrapPassword = $passwordLine.Substring('password='.Length)
}

if ($Build) {
    $deployScript = Join-Path $PSScriptRoot 'deploy.ps1'
    if (-not (Test-Path $deployScript)) { throw 'deploy.ps1 is required for -Build.' }
    & $deployScript
}

$toolRoot = Join-Path $deploymentRoot 'infrastructure'
$natsExecutable = Get-ChildItem -LiteralPath $toolRoot -Recurse -Filter 'nats-server.exe' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
$minioExecutable = Join-Path $toolRoot 'minio.exe'
if (-not $natsExecutable -or -not (Test-Path $minioExecutable)) {
    throw 'NATS/MinIO binaries are missing. Install the local infrastructure binaries first; see src/docs/local-deployment.md.'
}

$infrastructure = @(
    @{ Name='nats'; File=$natsExecutable; Arguments="--jetstream --store_dir `"$(Join-Path $runtimeRoot 'nats')`" --http_port 8222"; Environment=@{} },
    @{ Name='minio'; File=$minioExecutable; Arguments="server `"$(Join-Path $runtimeRoot 'minio')`" --address 127.0.0.1:9000 --console-address 127.0.0.1:9001"; Environment=@{ MINIO_ROOT_USER='smartmetrix'; MINIO_ROOT_PASSWORD='smartmetrix-dev-secret' } }
)
foreach ($item in $infrastructure) {
    $pidFile = Join-Path $pidRoot "$($item.Name).pid"
    $existingState = Read-SmartMetrixProcessState -Path $pidFile
    if ($existingState -and (Test-SmartMetrixProcessState -State $existingState)) {
        Write-Host "$($item.Name) already running (PID $($existingState.Pid))"
        continue
    }
    if (Test-Path -LiteralPath $pidFile) { Remove-Item -LiteralPath $pidFile -Force }
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $item.File
    $startInfo.Arguments = $item.Arguments
    $startInfo.WorkingDirectory = $runtimeRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $oldValues = @{}
    foreach ($entry in $item.Environment.GetEnumerator()) {
        $oldValues[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    try { $process = [System.Diagnostics.Process]::Start($startInfo) }
    finally { foreach ($entry in $oldValues.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') } }
    Write-SmartMetrixProcessState -Path $pidFile -Process $process -ExpectedPath $item.File
    Write-Host "Started $($item.Name) (PID $($process.Id))"
}
Start-Sleep -Seconds 3

$commonEnvironment = @{
    ASPNETCORE_ENVIRONMENT = 'Development'
    Logging__EventLog__LogLevel__Default = 'None'
    SmartMetrix__Messaging__Url = 'nats://127.0.0.1:4222'
    SmartMetrix__FileLogging__Directory = $deploymentLogRoot
}

foreach ($service in $configuration.Services) {
    $pidFile = Join-Path $pidRoot "$($service.Name).pid"
    $existingState = Read-SmartMetrixProcessState -Path $pidFile
    if ($existingState -and (Test-SmartMetrixProcessState -State $existingState)) {
        Write-Host "$($service.Name) already running (PID $($existingState.Pid))"
        continue
    }
    if (Test-Path -LiteralPath $pidFile) { Remove-Item -LiteralPath $pidFile -Force }

    $serviceRoot = Join-Path $deploymentRoot "services\$($service.Name)"
    $stdout = Join-Path $logRoot "$($service.Name).out.log"
    $stderr = Join-Path $logRoot "$($service.Name).err.log"
    $environment = $commonEnvironment.Clone()
    $environment['ASPNETCORE_URLS'] = "http://127.0.0.1:$($service.Port)"

    if ($service.Name -eq 'api-gateway') {
        $environment['OperatorApi__OrchestratorUrl'] = 'http://127.0.0.1:5201'
        $environment['OperatorApi__ApiKeys__operator-secret'] = 'operator'
        $environment['OperatorApi__ApiKeys__engineer-secret'] = 'engineer'
        $environment['OperatorApi__BootstrapAdminPassword'] = $bootstrapPassword
        $environment['OperatorApi__UserStorePath'] = (Join-Path $securityRoot 'users.json')
        $environment['OperatorApi__DataProtectionPath'] = (Join-Path $securityRoot 'protection-keys')
        $componentUrls = @(
            'http://127.0.0.1:5202/ready',
            'http://127.0.0.1:5105/ready',
            'http://127.0.0.1:5201/ready',
            'http://127.0.0.1:5207/ready',
            'http://127.0.0.1:5203/ready',
            'http://127.0.0.1:5204/ready',
            'http://127.0.0.1:5205/ready',
            'http://127.0.0.1:5208/ready',
            'http://127.0.0.1:5209/ready',
            'http://127.0.0.1:5210/ready',
            'http://127.0.0.1:5211/ready',
            'http://127.0.0.1:5212/ready',
            'http://127.0.0.1:5213/ready',
            'http://127.0.0.1:8222/healthz'
        )
        for ($index = 0; $index -lt $componentUrls.Count; $index++) {
            $environment["OperatorApi__Components__${index}__ReadyUrl"] = $componentUrls[$index]
        }
    }
    if ($service.Name -eq 'camera') {
        $environment['Camera__Adapter'] = if ($TestMode) { 'Simulator' } else { 'Arena' }
        $environment['Camera__StorageServiceUrl'] = 'http://127.0.0.1:5105'
    }
    if ($service.Name -eq 'orchestrator') {
        $demoEnabled = $TestMode -and [Environment]::GetEnvironmentVariable('MeasurementWorkflow__RunDemoPipeline', 'Process') -eq 'true'
        $environment['MeasurementWorkflow__SeedDemoData'] = $demoEnabled.ToString().ToLowerInvariant()
        $environment['MeasurementWorkflow__RunDemoPipeline'] = $demoEnabled.ToString().ToLowerInvariant()
        $environment['MeasurementWorkflow__DemoStageDelaySeconds'] = '4'
    }
    if ($service.Name -eq 'depth') { $environment['Depth__StorageBaseUrl'] = 'http://127.0.0.1:5105' }
    if ($service.Name -eq 'segmentation') {
        $environment['Segmentation__StorageBaseUrl'] = 'http://127.0.0.1:5105'
        $environment['Segmentation__Backend'] = if ($TestMode) { 'Deterministic' } else { 'StoneVision' }
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command dotnet).Source
    $startInfo.WorkingDirectory = $serviceRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $assemblyPath = Join-Path $serviceRoot "$($service.Project).dll"
    if (-not (Test-Path $assemblyPath)) { throw "Published service not found: $assemblyPath. Run deploy.ps1." }
    $startInfo.Arguments = "`"$assemblyPath`""
    $oldValues = @{}
    foreach ($entry in $environment.GetEnumerator()) {
        $oldValues[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    try { $process = [System.Diagnostics.Process]::Start($startInfo) }
    finally {
        foreach ($entry in $oldValues.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
    }
    Write-SmartMetrixProcessState -Path $pidFile -Process $process -ExpectedPath $assemblyPath
    Set-Content -LiteralPath $stdout -Value "Started $($service.Name) at $(Get-Date -Format o); process output is available through Windows process diagnostics."
    Set-Content -LiteralPath $stderr -Value ''
    Write-Host "Started $($service.Name) on http://127.0.0.1:$($service.Port) (PID $($process.Id))"
}

Start-Sleep -Seconds 3
& (Join-Path $PSScriptRoot 'status.ps1')
