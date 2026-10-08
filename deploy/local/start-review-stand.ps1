[CmdletBinding()]
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$workspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$standRoot = Join-Path $workspaceRoot 'artifacts/integration-stand'
$runRoot = Join-Path $standRoot 'application'
$logs = Join-Path $runRoot 'logs'
New-Item -ItemType Directory -Force -Path $runRoot, $logs | Out-Null
$connections = Get-Content -LiteralPath (Join-Path $standRoot 'connections.local.json') -Raw | ConvertFrom-Json
$configuration = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'services.psd1')
. (Join-Path $PSScriptRoot 'process-state.ps1')
if (-not $NoBuild) {
    & dotnet build (Join-Path $workspaceRoot 'SmartMetrix.sln') --no-restore -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
}
foreach ($service in $configuration.Services) {
    $listener = Get-NetTCPConnection -State Listen -LocalPort $service.Port -ErrorAction SilentlyContinue | Select-Object -First 1
    $state = Read-SmartMetrixProcessState -Path (Join-Path $runRoot "$($service.Name).pid.json")
    if ($listener -and (-not $state -or $listener.OwningProcess -ne $state.Pid -or -not (Test-SmartMetrixProcessState $state))) {
        throw "Port $($service.Port) belongs to another process"
    }
}
$credentialPath = Join-Path $runRoot 'credentials.local.json'
if (-not (Test-Path -LiteralPath $credentialPath)) {
    @{username='admin';password=('Sm!' + [Guid]::NewGuid().ToString('N'));engineerUsername='engineer';engineerPassword=('Sm!' + [Guid]::NewGuid().ToString('N'))} |
        ConvertTo-Json | Set-Content -LiteralPath $credentialPath -Encoding utf8
}
$credentials = Get-Content -LiteralPath $credentialPath -Raw | ConvertFrom-Json
$common = @{
    ASPNETCORE_ENVIRONMENT='Production'; SmartMetrix__DeploymentProfile='Local'
    Persistence__Provider='Postgres'; ConnectionStrings__SmartMetrix=$connections.postgres
    SmartMetrix__Messaging__Url=$connections.nats; SmartMetrix__Messaging__StreamName='SMARTMETRIX_REVIEW_STAND'
    SmartMetrix__FileLogging__Directory=$logs; Logging__LogLevel__Default='Warning'; Logging__EventLog__LogLevel__Default='None'
    Storage__Endpoint=$connections.minio; Storage__AccessKey=$connections.minioAccessKey; Storage__SecretKey=$connections.minioSecretKey
    Storage__Bucket='smartmetrix-demo-20260930'; Camera__Adapter='Arena'; Segmentation__Backend='StoneVision'
    Camera__StorageServiceUrl='http://127.0.0.1:5105'; Depth__StorageBaseUrl='http://127.0.0.1:5105'; Segmentation__StorageBaseUrl='http://127.0.0.1:5105'
    Pipeline__Enabled='false'; Pipeline__RigId='demo-review-rig'; CaptureTrigger__Enabled='false'
    MeasurementWorkflow__SeedDemoData='false'; MeasurementWorkflow__RunDemoPipeline='false'
    CloudSync__DeleteArtifactsAfterAcknowledgement='false'
}
$serviceNames = @{storage='storage';orchestrator='orchestrator';camera='camera';quality='quality';depth='depth';segmentation='segmentation';trigger='trigger';calibration='calibration';positioning='positioning';georeference='georeference';'block-analysis'='analysis';'control-points'='controlPoints';'cloud-sync'='sync'}
$scopeIds = @('demo-ekg-12','demo-ekg-15','demo-ekg-20')
$excavators = @('ЭКГ-12','ЭКГ-15','ЭКГ-20')
foreach ($service in $configuration.Services) {
    $pidFile = Join-Path $runRoot "$($service.Name).pid.json"
    $state = Read-SmartMetrixProcessState -Path $pidFile
    if ($state -and (Test-SmartMetrixProcessState $state)) { Write-Host "$($service.Name) already running"; continue }
    $serviceRoot = Join-Path $runRoot "services/$($service.Name)"
    & dotnet publish (Join-Path $workspaceRoot "src/Services/$($service.Project)/$($service.Project).csproj") --no-build --no-restore -c Release -o $serviceRoot --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($service.Name)" }
    $environment = $common.Clone()
    $environment['ASPNETCORE_URLS'] = "http://127.0.0.1:$($service.Port)"
    if ($service.Name -eq 'api-gateway') {
        $environment['OperatorApi__BootstrapAdminPassword']=$credentials.password
        $environment['OperatorApi__DataProtectionPath']=(Join-Path $runRoot 'protection-keys')
        $environment['OperatorApi__LogRoot']=$logs
        $environment['OperatorApi__OrchestratorUrl']='http://127.0.0.1:5201'
        for ($i=0; $i -lt $scopeIds.Count; $i++) {
            $prefix="Workstations__Scopes__${i}__"
            $environment[$prefix+'Id']=$scopeIds[$i]
            $environment[$prefix+'SiteId']='demo-quarry'
            $environment[$prefix+'ExcavatorId']=$excavators[$i]
            $environment[$prefix+'RigId']='demo-review-rig'
            $environment[$prefix+'CoordinateSystemId']='карьер:локальная'
            foreach ($dependency in $configuration.Services) {
                if ($serviceNames.ContainsKey($dependency.Name)) { $environment[$prefix+'Services__'+$serviceNames[$dependency.Name]]="http://127.0.0.1:$($dependency.Port)" }
            }
        }
    }
    $assembly=Join-Path $serviceRoot "$($service.Project).dll"
    $process=Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @('"'+$assembly+'"') -WorkingDirectory $serviceRoot -Environment $environment -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logs "$($service.Name).stdout.log") -RedirectStandardError (Join-Path $logs "$($service.Name).stderr.log") -PassThru
    Write-SmartMetrixProcessState -Path $pidFile -Process $process -ExpectedPath $assembly
    Write-Host "Started $($service.Name) on port $($service.Port)"
}
$deadline=[DateTimeOffset]::UtcNow.AddSeconds(60)
foreach ($service in $configuration.Services) {
    while ($true) {
        try { $null=Invoke-WebRequest "http://127.0.0.1:$($service.Port)/health" -TimeoutSec 2; break }
        catch { if ([DateTimeOffset]::UtcNow -ge $deadline) { throw "Health check failed: $($service.Name). See $logs" }; Start-Sleep -Milliseconds 300 }
    }
}
$session=[Microsoft.PowerShell.Commands.WebRequestSession]::new()
$null=Invoke-RestMethod 'http://127.0.0.1:5190/api/auth/login' -Method Post -ContentType application/json -Body (@{username=$credentials.username;password=$credentials.password}|ConvertTo-Json) -WebSession $session
$csrf=Invoke-RestMethod 'http://127.0.0.1:5190/api/auth/csrf' -WebSession $session
$users=Invoke-RestMethod 'http://127.0.0.1:5190/api/v1/administrator/users/' -WebSession $session
if (-not ($users | Where-Object username -eq $credentials.engineerUsername)) {
    $body=@{username=$credentials.engineerUsername;displayName='Инженер тестового стенда';role='engineer';additionalRoles=@('operator');scopeIds=$scopeIds;password=$credentials.engineerPassword}|ConvertTo-Json
    $null=Invoke-RestMethod 'http://127.0.0.1:5190/api/v1/administrator/users/' -Method Post -ContentType application/json -Body $body -WebSession $session -Headers @{'X-CSRF-Token'=$csrf.token}
}
Write-Host 'All 14 application processes respond on /health. UI: http://127.0.0.1:5190/login/'
Write-Host "Credentials: $credentialPath"
