[CmdletBinding()]
param(
    [string]$ConnectionsFile = (Join-Path $PSScriptRoot '../artifacts/integration-stand/connections.local.json'),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath $ConnectionsFile -Raw | ConvertFrom-Json
$variables = @{
    SMARTMETRIX_TEST_POSTGRES = $settings.postgres
    SMARTMETRIX_TEST_NATS = $settings.nats
    SMARTMETRIX_TEST_MINIO = $settings.minio
    SMARTMETRIX_TEST_MINIO_ACCESS_KEY = $settings.minioAccessKey
    SMARTMETRIX_TEST_MINIO_SECRET_KEY = $settings.minioSecretKey
}
$previous = @{}
foreach ($name in $variables.Keys) {
    if ([string]::IsNullOrWhiteSpace($variables[$name])) { throw "Missing connection setting for $name" }
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
Push-Location (Join-Path $PSScriptRoot '..')
try {
    foreach ($name in $variables.Keys) { [Environment]::SetEnvironmentVariable($name, $variables[$name], 'Process') }
    $arguments = @('test', 'SmartMetrix.sln', '--no-restore', '--configuration', 'Release',
        '--logger', 'trx;LogFileName=full-integration.trx', '--results-directory', 'artifacts/integration-stand/test-results')
    if ($NoBuild) { $arguments += '--no-build' }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Integration tests failed (exit code $LASTEXITCODE)" }
}
finally {
    foreach ($name in $variables.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
}
