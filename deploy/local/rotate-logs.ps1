[CmdletBinding()]
param([int]$RetentionDays = 30, [long]$MaximumFileBytes = 104857600)

$root = 'C:\DEPLOY_LOG'
if (-not (Test-Path $root)) { exit 0 }
$threshold = (Get-Date).AddDays(-[Math]::Max(1, $RetentionDays))
Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.jsonl' | ForEach-Object {
    if ($_.LastWriteTime -lt $threshold) { Remove-Item -LiteralPath $_.FullName -Force }
    elseif ($_.Length -gt $MaximumFileBytes) {
        $archive = "$($_.FullName).$((Get-Date).ToString('HHmmss')).archive"
        Move-Item -LiteralPath $_.FullName -Destination $archive
    }
}
