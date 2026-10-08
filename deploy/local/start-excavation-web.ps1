[CmdletBinding()]
param([int]$Port = 5191, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Port must be between 1024 and 65535.' }
$workspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$standRoot = Join-Path $workspaceRoot 'artifacts/excavation-web'
$sourceRoot = Join-Path $workspaceRoot 'src/Services/SmartMetrix.ApiGateway'
$assembly = Join-Path $sourceRoot 'bin/Release/net10.0/SmartMetrix.ApiGateway.dll'
$replays = Join-Path $workspaceRoot 'artifacts/excavator-replay'
New-Item -ItemType Directory -Force -Path $standRoot | Out-Null
. (Join-Path $PSScriptRoot 'process-state.ps1')
$pidPath = Join-Path $standRoot 'gateway.pid.json'
$state = Read-SmartMetrixProcessState -Path $pidPath
if ($state -and (Test-SmartMetrixProcessState $state)) {
    Write-Host 'Excavation web demo is already running. Port and credentials are in artifacts/excavation-web/credentials.local.json.'
    return
}
if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) { throw "Port $Port is occupied; choose another -Port." }
if (-not $NoBuild) {
    & dotnet build (Join-Path $workspaceRoot 'SmartMetrix.sln') --no-restore -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
& dotnet run --project (Join-Path $workspaceRoot 'tools/SmartMetrix.ExcavatorReplay') --no-build -c Release -- --output $replays
if ($LASTEXITCODE -ne 0) { throw 'Replay failed.' }
$credentialPath = Join-Path $standRoot 'credentials.local.json'
if (-not (Test-Path -LiteralPath $credentialPath)) {
    @{username='operator'; password=('Sm!' + [Guid]::NewGuid().ToString('N')); adminPassword=('Sm!' + [Guid]::NewGuid().ToString('N')); port=$Port} |
        ConvertTo-Json | Set-Content -LiteralPath $credentialPath -Encoding utf8
}
$credentials = Get-Content -LiteralPath $credentialPath -Raw | ConvertFrom-Json
$credentials.port = $Port
$credentials | ConvertTo-Json | Set-Content -LiteralPath $credentialPath -Encoding utf8
$environment = @{
    ASPNETCORE_URLS="http://127.0.0.1:$Port"; ASPNETCORE_CONTENTROOT=$sourceRoot
    ASPNETCORE_ENVIRONMENT='Development'; SmartMetrix__DeploymentProfile='Local'; Persistence__Provider='File'
    OperatorApi__BootstrapAdminPassword=$credentials.adminPassword
    OperatorApi__UserStorePath=(Join-Path $standRoot 'users.json'); OperatorApi__AuditPath=(Join-Path $standRoot 'audit.jsonl')
    OperatorApi__DataProtectionPath=(Join-Path $standRoot 'protection-keys')
    Workstations__Scopes__0__Id='demo-excavator'; Workstations__Scopes__0__SiteId='demo-quarry'
    Workstations__Scopes__0__ExcavatorId='demo-excavator'; Workstations__Scopes__0__RigId='demo-rig'
    Workstations__Scopes__0__CoordinateSystemId='demo-quarry'; 'ExcavationReports__Roots__demo-excavator'=$replays
    SmartMetrix__FileLogging__Directory=(Join-Path $standRoot 'logs'); Logging__LogLevel__Default='Warning'
}
$process = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @('"'+$assembly+'"') -WorkingDirectory $sourceRoot `
    -Environment $environment -WindowStyle Hidden -RedirectStandardOutput (Join-Path $standRoot 'gateway.stdout.log') `
    -RedirectStandardError (Join-Path $standRoot 'gateway.stderr.log') -PassThru
Write-SmartMetrixProcessState -Path $pidPath -Process $process -ExpectedPath $assembly
$url = "http://127.0.0.1:$Port"
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
while ($true) {
    try { $null = Invoke-WebRequest "$url/health" -TimeoutSec 2; break }
    catch { if ($process.HasExited -or [DateTimeOffset]::UtcNow -ge $deadline) { throw "Gateway did not start. See $standRoot logs." }; Start-Sleep -Milliseconds 300 }
}
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$null = Invoke-RestMethod "$url/api/auth/login" -Method Post -ContentType application/json `
    -Body (@{username='admin';password=$credentials.adminPassword} | ConvertTo-Json) -WebSession $session
$csrf = Invoke-RestMethod "$url/api/auth/csrf" -WebSession $session
$users = Invoke-RestMethod "$url/api/admin/users" -WebSession $session
if (-not ($users | Where-Object username -eq $credentials.username)) {
    $body = @{username=$credentials.username;password=$credentials.password;displayName='Оператор демо';role='operator';scopeIds=@('demo-excavator')} | ConvertTo-Json
    $null = Invoke-RestMethod "$url/api/v1/administrator/users/" -Method Post -ContentType application/json -Body $body `
        -WebSession $session -Headers @{'X-CSRF-Token'=$csrf.token}
}
Write-Host "UI: $url/excavation/"
Write-Host "Credentials: $credentialPath"
