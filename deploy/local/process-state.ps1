function Read-SmartMetrixProcessState {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        $state = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -ErrorAction Stop
        $processId = 0
        $startedAtUtcTicks = 0L
        if (-not [int]::TryParse([string]$state.pid, [ref]$processId)) { return $null }
        if (-not [long]::TryParse([string]$state.startedAtUtcTicks, [ref]$startedAtUtcTicks)) { return $null }
        if ($processId -le 0 -or [string]::IsNullOrWhiteSpace([string]$state.expectedPath)) { return $null }
        [pscustomobject]@{ Pid = $processId; StartedAtUtcTicks = $startedAtUtcTicks; ExpectedPath = [string]$state.expectedPath }
    } catch { return $null }
}

function Test-SmartMetrixProcessState {
    param([Parameter(Mandatory)]$State)

    $process = Get-Process -Id $State.Pid -ErrorAction SilentlyContinue
    if (-not $process) { return $false }
    try {
        $actualStartedAtUtcTicks = ($process.StartTime).ToUniversalTime().Ticks
        if ([Math]::Abs($actualStartedAtUtcTicks - $State.StartedAtUtcTicks) -gt [TimeSpan]::TicksPerSecond * 2) { return $false }
    } catch { return $false }

    try {
        $details = Get-CimInstance Win32_Process -Filter "ProcessId=$($State.Pid)" -ErrorAction Stop
        $identity = "$($details.ExecutablePath) $($details.CommandLine)"
        if (-not $identity.Contains($State.ExpectedPath, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    } catch {
        # Start time still protects against PID reuse when another security context hides the command line.
    }
    return $true
}

function Write-SmartMetrixProcessState {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$ExpectedPath)

    $state = [ordered]@{
        pid = $Process.Id
        startedAtUtcTicks = ($Process.StartTime).ToUniversalTime().Ticks
        expectedPath = $ExpectedPath
    }
    $temporary = "$Path.tmp"
    $state | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
