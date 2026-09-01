# Run from an elevated PowerShell session.
$ErrorActionPreference = 'Stop'
$root = 'C:\DEPLOY\SmartMetrix'
$startAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$root\start.ps1`""
$startupTrigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName 'SmartMetrix-Startup' -Action $startAction -Trigger $startupTrigger -Principal $principal -Force -ErrorAction Stop | Out-Null

$watchdogAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$root\watchdog.ps1`""
$watchdogTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
Register-ScheduledTask -TaskName 'SmartMetrix-Watchdog' -Action $watchdogAction -Trigger $watchdogTrigger -Principal $principal -Force -ErrorAction Stop | Out-Null
Write-Host 'SmartMetrix startup and watchdog tasks installed.'
