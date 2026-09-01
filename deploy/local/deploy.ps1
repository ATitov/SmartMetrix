[CmdletBinding()]
param([string]$Destination = 'C:\DEPLOY\SmartMetrix')

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$configuration = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'services.psd1')
$servicesRoot = Join-Path $Destination 'services'
$infrastructureRoot = Join-Path $Destination 'infrastructure'
$runtimeRoot = Join-Path $Destination 'runtime'

& (Join-Path $PSScriptRoot 'stop.ps1')
New-Item -ItemType Directory -Force -Path $Destination, $servicesRoot, $infrastructureRoot, $runtimeRoot | Out-Null

& dotnet build (Join-Path $repoRoot 'SmartMetrix.sln') --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

foreach ($service in $configuration.Services) {
    $project = Join-Path $repoRoot "src\Services\$($service.Project)\$($service.Project).csproj"
    $output = Join-Path $servicesRoot $service.Name
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    & dotnet publish $project --configuration Release --no-build --no-restore --output $output
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($service.Project)." }
    Write-Host "Published $($service.Name) to $output"
}

$sourceTools = Join-Path $repoRoot 'data\local-deployment\tools'
$nats = Get-ChildItem -LiteralPath $sourceTools -Recurse -Filter 'nats-server.exe' -ErrorAction Stop | Select-Object -First 1
$minio = Join-Path $sourceTools 'minio.exe'
if (-not $nats -or -not (Test-Path $minio)) { throw 'Local NATS/MinIO binaries are missing.' }
Copy-Item -LiteralPath $nats.FullName -Destination (Join-Path $infrastructureRoot 'nats-server.exe') -Force
Copy-Item -LiteralPath $minio -Destination (Join-Path $infrastructureRoot 'minio.exe') -Force

foreach ($file in 'start.ps1','stop.ps1','status.ps1','services.psd1','deploy.ps1','watchdog.ps1','rotate-logs.ps1','install-autostart.ps1') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $Destination $file) -Force
}

Write-Host "SmartMetrix deployed to $Destination"
