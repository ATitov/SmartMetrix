[CmdletBinding()]
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$workspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$runRoot = Join-Path $workspaceRoot 'data/workspace-run'
$logs = Join-Path $runRoot 'logs'
$configuration = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'services.psd1')
New-Item -ItemType Directory -Force -Path $runRoot, $logs | Out-Null
if (-not $NoBuild) {
    & dotnet build (Join-Path $workspaceRoot 'SmartMetrix.sln') --no-restore -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
# Avoid replacing or signalling another deployment. Reuse this run only when its processes are stopped.
foreach ($service in @($configuration.Services) + @(@{Port=9000}, @{Port=9001}, @{Port=4222}, @{Port=8222})) {
    $probe = [System.Net.Sockets.TcpClient]::new()
    try { if ($probe.ConnectAsync('127.0.0.1', $service.Port).Wait(300) -and $probe.Connected) { throw "Port $($service.Port) is already occupied; leave that process unchanged." } }
    catch [System.AggregateException] { }
    finally { $probe.Dispose() }
}
$credentialPath = Join-Path $runRoot 'credentials.json'
if (-not (Test-Path -LiteralPath $credentialPath)) {
    @{ username='admin'; password=('Sm!' + [Guid]::NewGuid().ToString('N')) } | ConvertTo-Json | Set-Content -LiteralPath $credentialPath -Encoding utf8
}
$credentials = Get-Content -Raw -LiteralPath $credentialPath | ConvertFrom-Json
if (-not $credentials.storagePassword) { $credentials | Add-Member storagePassword ('Sm!' + [Guid]::NewGuid().ToString('N')) }
if (-not $credentials.engineerPassword) { $credentials | Add-Member engineerPassword ('Sm!' + [Guid]::NewGuid().ToString('N')) }
$credentials | ConvertTo-Json | Set-Content -LiteralPath $credentialPath -Encoding utf8
$infrastructure = 'C:/DEPLOY/SmartMetrix/infrastructure'
foreach ($item in @(
    @{name='minio';file=(Join-Path $infrastructure 'minio.exe');args=@('server', ('"' + (Join-Path $runRoot 'minio') + '"'), '--address','127.0.0.1:9000','--console-address','127.0.0.1:9001');environment=@{MINIO_ROOT_USER='smartmetrix';MINIO_ROOT_PASSWORD=$credentials.storagePassword}},
    @{name='nats';file=(Join-Path $infrastructure 'nats-server.exe');args=@('-a','127.0.0.1','--jetstream','--store_dir',('"' + (Join-Path $runRoot 'nats') + '"'),'--http_port','8222');environment=@{}}
)) {
    if (-not (Test-Path -LiteralPath $item.file)) { throw "Infrastructure executable not installed: $($item.name)" }
    Start-Process -FilePath $item.file -ArgumentList $item.args -Environment $item.environment -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logs "$($item.name).stdout.log") -RedirectStandardError (Join-Path $logs "$($item.name).stderr.log") | Out-Null
}
$common = @{
    ASPNETCORE_ENVIRONMENT='Production'
    SmartMetrix__DeploymentProfile='Local'
    SmartMetrix__FileLogging__Directory=$logs
    Logging__EventLog__LogLevel__Default='None'
    MeasurementWorkflow__SeedDemoData='false'
    MeasurementWorkflow__RunDemoPipeline='false'
    Camera__Adapter='Arena'
    Segmentation__Backend='StoneVision'
    Storage__AccessKey='smartmetrix'
    Storage__SecretKey=$credentials.storagePassword
}
$serviceNames = @{ storage='storage'; orchestrator='orchestrator'; camera='camera'; quality='quality'; depth='depth'; segmentation='segmentation'; trigger='trigger'; calibration='calibration'; positioning='positioning'; georeference='georeference'; 'block-analysis'='analysis'; 'control-points'='controlPoints'; 'cloud-sync'='sync' }
$processes = @()
foreach ($service in $configuration.Services) {
    $serviceRoot = Join-Path $runRoot "services/$($service.Name)"
    & dotnet publish (Join-Path $workspaceRoot "src/Services/$($service.Project)/$($service.Project).csproj") --no-build --no-restore -c Release -o $serviceRoot | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($service.Name)" }
    $environment = $common.Clone()
    $environment['ASPNETCORE_URLS'] = "http://127.0.0.1:$($service.Port)"
    $environment['Camera__StorageServiceUrl'] = 'http://127.0.0.1:5105'
    $environment['Depth__StorageBaseUrl'] = 'http://127.0.0.1:5105'
    $environment['Segmentation__StorageBaseUrl'] = 'http://127.0.0.1:5105'
    if ($service.Name -eq 'api-gateway') {
        $environment['OperatorApi__BootstrapAdminPassword'] = $credentials.password
        $environment['OperatorApi__UserStorePath'] = (Join-Path $runRoot 'users.json')
        $environment['OperatorApi__LogRoot'] = $logs
        $environment['OperatorApi__DataProtectionPath'] = (Join-Path $runRoot 'protection-keys')
        $environment['Workstations__Scopes__0__Id'] = 'local'
        $environment['Workstations__Scopes__0__SiteId'] = 'UNCONFIGURED'
        $environment['Workstations__Scopes__0__ExcavatorId'] = 'UNCONFIGURED'
        $environment['Workstations__Scopes__0__RigId'] = 'UNCONFIGURED'
        $environment['Workstations__Scopes__0__CoordinateSystemId'] = 'UNCONFIGURED'
        foreach ($dependency in $configuration.Services) {
            if ($serviceNames.ContainsKey($dependency.Name)) {
                $environment["Workstations__Scopes__0__Services__$($serviceNames[$dependency.Name])"] = "http://127.0.0.1:$($dependency.Port)"
            }
        }
    }
    $assembly = Join-Path $serviceRoot "$($service.Project).dll"
    $process = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @('"' + $assembly + '"') -WorkingDirectory $serviceRoot -Environment $environment -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logs "$($service.Name).stdout.log") -RedirectStandardError (Join-Path $logs "$($service.Name).stderr.log") -PassThru
    $processes += @{name=$service.Name;pid=$process.Id;startedAt=$process.StartTime.ToUniversalTime().ToString('o');assembly=$assembly;port=$service.Port}
    $processes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'processes.json') -Encoding utf8
}
Write-Host 'Application processes launched: http://127.0.0.1:5190/login/'
Write-Host "Credentials stored locally: $credentialPath"
Write-Host 'No simulator, demo measurements or deterministic segmentation are enabled. Missing hardware/services remain unavailable.'
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
for ($attempt=0; $attempt -lt 15; $attempt++) {
    try { $null = Invoke-WebRequest http://127.0.0.1:5190/health -TimeoutSec 2; break }
    catch { if($attempt -eq 14){ throw }; Start-Sleep -Seconds 1 }
}
$null = Invoke-RestMethod http://127.0.0.1:5190/api/auth/login -Method Post -ContentType application/json -Body (@{username=$credentials.username;password=$credentials.password}|ConvertTo-Json) -WebSession $session
$csrf = Invoke-RestMethod http://127.0.0.1:5190/api/auth/csrf -WebSession $session
$users = Invoke-RestMethod http://127.0.0.1:5190/api/v1/administrator/users/ -WebSession $session
if (-not ($users | Where-Object username -eq 'engineer')) {
    $body = @{username='engineer';displayName='Локальный инженер';role='engineer';additionalRoles=@('operator');scopeIds=@('local');password=$credentials.engineerPassword}|ConvertTo-Json
    $null = Invoke-RestMethod http://127.0.0.1:5190/api/v1/administrator/users/ -Method Post -ContentType application/json -Body $body -Headers @{'X-CSRF-Token'=$csrf.token} -WebSession $session
}
Write-Host 'Use engineer with engineerPassword from credentials.json for the working panels.'
