param(
    [Parameter(Mandatory = $true)]
    [string]$CoveragePath
)

$ErrorActionPreference = 'Stop'
[xml]$report = Get-Content -LiteralPath $CoveragePath -Raw
if (-not $report.coverage.packages) { throw 'Expected a Cobertura coverage report.' }
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$culture = [System.Globalization.CultureInfo]::InvariantCulture

'| Service | Lines | Branches |'
'|---|---:|---:|'
foreach ($service in Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src/Services') -Directory | Sort-Object Name) {
    $package = @($report.coverage.packages.package | Where-Object name -eq $service.Name)
    if ($package.Count -eq 0) {
        "| $($service.Name) | not measured | not measured |"
        continue
    }
    if ($package.Count -ne 1) { throw "Multiple packages found for $($service.Name). Supply one report." }
    $lines = (100 * [double]::Parse($package[0].GetAttribute('line-rate'), $culture)).ToString('F1', $culture)
    $branches = (100 * [double]::Parse($package[0].GetAttribute('branch-rate'), $culture)).ToString('F1', $culture)
    "| $($service.Name) | $lines% | $branches% |"
}
''
'Coverage measures instrumented .NET code only. Child service processes, native code and other languages require separate instrumentation.'
'Requirement acceptance also requires scenario results; line coverage does not establish correctness.'
