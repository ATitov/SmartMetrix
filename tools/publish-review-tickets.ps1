param(
    [string]$Repository = 'ATitov/SmartMetrix',
    [string]$TicketsPath = (Join-Path $PSScriptRoot 'review-tickets-2026-09-30.json')
)
$ErrorActionPreference = 'Stop'
& gh auth status
if ($LASTEXITCODE -ne 0) { throw 'GitHub authentication is unavailable. Run gh auth login first.' }
$existingJson = & gh issue list --repo $Repository --state all --limit 1000 --json title,url
if ($LASTEXITCODE -ne 0) { throw 'Could not read existing issues; publication stopped.' }
$existing = @($existingJson | ConvertFrom-Json)
$tickets = Get-Content -LiteralPath $TicketsPath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($ticket in $tickets) {
    $match = $existing | Where-Object { $_.title -eq $ticket.title } | Select-Object -First 1
    if ($match) {
        Write-Output ('Already exists: ' + $match.url + ' ' + $ticket.title)
        continue
    }
    $bodyPath = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText($bodyPath, $ticket.body, [System.Text.UTF8Encoding]::new($false))
        $createdUrl = & gh issue create --repo $Repository --title $ticket.title --body-file $bodyPath
        if ($LASTEXITCODE -ne 0) { throw ('Issue creation failed: ' + $ticket.title + '. Publication stopped; rerun checks existing titles.') }
        $url = ($createdUrl | Out-String).Trim()
        Write-Output ($url + ' ' + $ticket.title)
        $existing += [pscustomobject]@{ title = $ticket.title; url = $url }
    }
    finally {
        Remove-Item -LiteralPath $bodyPath -Force -ErrorAction SilentlyContinue
    }
}
