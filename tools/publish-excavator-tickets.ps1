param(
    [string]$Repository = 'ATitov/SmartMetrix',
    [string]$TicketsPath = (Join-Path $PSScriptRoot 'excavator-tickets-2026-10-04.json')
)
$ErrorActionPreference = 'Stop'
& gh auth status
if ($LASTEXITCODE -ne 0) { throw 'Run gh auth login before publishing.' }
$existingJson = & gh issue list --repo $Repository --state all --limit 1000 --json title,number,url
if ($LASTEXITCODE -ne 0) { throw 'Could not read existing issues.' }
$existing = @($existingJson | ConvertFrom-Json)
$plan = Get-Content -LiteralPath $TicketsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$numbers = @{}
foreach ($ticket in @($plan.items)) {
    $matches = @($existing | Where-Object { $_.title -eq $ticket.title })
    if ($matches.Count -gt 1) { throw ('Ambiguous existing title: ' + $ticket.title) }
    if ($matches.Count -eq 1) { $numbers[$ticket.key] = $matches[0].number }
}
function Publish-Ticket($Ticket, [string]$Body) {
    $matches = @($existing | Where-Object { $_.title -eq $Ticket.title })
    if ($matches.Count -gt 1) { throw ('Ambiguous existing title: ' + $Ticket.title) }
    if ($matches.Count -eq 1) {
        Write-Output ('Already exists: ' + $matches[0].url)
        return [int]$matches[0].number
    }
    $bodyPath = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText($bodyPath, $Body, [System.Text.UTF8Encoding]::new($false))
        $result = & gh issue create --repo $Repository --title $Ticket.title --body-file $bodyPath
        if ($LASTEXITCODE -ne 0) { throw ('Publication failed: ' + $Ticket.title + '. Rerun checks existing titles.') }
        $url = ($result | Out-String).Trim()
        if ($url -notmatch '/issues/(\d+)$') { throw ('Unrecognized created issue URL: ' + $url) }
        Write-Output ('Created: ' + $url)
        return [int]$Matches[1]
    }
    finally { Remove-Item -LiteralPath $bodyPath -Force -ErrorAction SilentlyContinue }
}
foreach ($ticket in @($plan.items)) {
    $dependencies = @($ticket.deps | ForEach-Object {
        if ($_ -match '^#\d+$') { $_ }
        elseif ($numbers.ContainsKey($_)) { '#' + $numbers[$_] }
        else { throw ('Unresolved dependency: ' + $_) }
    })
    $resolvedBody = $ticket.body.Replace(($ticket.deps -join ', ') + '.', ($dependencies -join ', ') + '.')
    $output = @(Publish-Ticket $ticket $resolvedBody)
    $numbers[$ticket.key] = [int]$output[-1]
    $output | Select-Object -SkipLast 1 | Write-Output
}
$roadmapBody = $plan.roadmap.body
foreach ($key in $numbers.Keys) { $roadmapBody = $roadmapBody.Replace('{{' + $key + '}}', '#' + $numbers[$key]) }
if ($roadmapBody -match '\{\{') { throw 'Unresolved roadmap dependencies.' }
$output = @(Publish-Ticket $plan.roadmap $roadmapBody)
$output | Select-Object -SkipLast 1 | Write-Output
