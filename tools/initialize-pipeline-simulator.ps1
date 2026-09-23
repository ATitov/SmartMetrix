[CmdletBinding()]
param(
    [string]$FramesDirectory = (Join-Path $PSScriptRoot '..\artifacts\pipeline-simulator\frames'),
    [switch]$Register,
    [string]$CalibrationUrl = 'http://127.0.0.1:5208',
    [string]$PositioningUrl = 'http://127.0.0.1:5209'
)

$ErrorActionPreference = 'Stop'
$FramesDirectory = [IO.Path]::GetFullPath($FramesDirectory)
New-Item -ItemType Directory -Force -Path $FramesDirectory | Out-Null
$random = [Random]::new(1701)
$source = [byte[]]::new(128 * 64)
for ($i = 0; $i -lt $source.Length; $i++) { $source[$i] = $random.Next(90, 230) }
for ($camera = 0; $camera -lt 3; $camera++) {
    $pixels = [byte[]]::new($source.Length)
    for ($y = 0; $y -lt 64; $y++) {
        for ($x = 0; $x -lt 128; $x++) {
            if ($x + $camera * 6 -lt 128) { $pixels[$y * 128 + $x] = $source[$y * 128 + $x + $camera * 6] }
        }
    }
    $name = 'camera-' + [char](97 + $camera) + '.raw'
    [IO.File]::WriteAllBytes((Join-Path $FramesDirectory $name), $pixels)
}
Write-Host "Prepared synthetic Mono8 frames (128x64): $FramesDirectory"
Write-Host 'Configure Camera__Adapter=Simulator, Camera__SimulatorFrameDirectory=<directory above>, Depth__Backend=Cpu, Pipeline__RigId=pipeline-simulator-v1, MeasurementWorkflow__RunDemoPipeline=false before starting services.'
if (-not $Register) { return }

function Send-Json([string]$Uri, $Body) {
    Invoke-RestMethod -Uri $Uri -Method Post -ContentType 'application/json' -Body (ConvertTo-Json -InputObject $Body -Depth 20 -Compress)
}

$rigId = 'pipeline-simulator-v1'
$records = @(Invoke-RestMethod -Uri "$CalibrationUrl/api/calibrations/?rigId=$rigId")
$active = $records | Where-Object { $_.status -eq 'Active' } | Select-Object -First 1
if (-not $active) {
    $identity = @(1, 0, 0, 0, 1, 0, 0, 0, 1)
    $cameras = @('A', 'B', 'C') | ForEach-Object {
        @{
            cameraId = $_
            intrinsics = @{ fx = 80; fy = 80; cx = 64; cy = 32; width = 128; height = 64 }
            distortion = @(0); rotation = $identity; translation = @(0, 0, 0)
            rectificationMapUri = 's3://calibration/synthetic-identity'
        }
    }
    $record = Send-Json "$CalibrationUrl/api/calibrations/" @{
        rigId = $rigId; cameras = @($cameras); geometry = @{ abMetres = 0.7; bcMetres = 0.8; acMetres = 1.5 }
        rigToPlatform = @{ rotation = $identity; translation = @(0, 0, 0) }; reprojectionErrorPixels = 0.1
    }
    $active = Send-Json "$CalibrationUrl/api/calibrations/$($record.id)/activate" @{
        validFrom = [DateTimeOffset]::UtcNow.AddHours(-1).ToString('o'); validTo = $null; actor = 'pipeline-simulator'
    }
}
try {
    Send-Json "$PositioningUrl/api/transforms" @{
        excavatorId = 'EX-SIM'; coordinateSystemId = 'quarry:simulator'; version = 1
        excavatorToQuarry = @{ translationMetres = @{ x = 10; y = 20; z = 30 }; rotation = @{ x = 0; y = 0; z = 0; w = 1 } }
        validFrom = [DateTimeOffset]::UtcNow.AddDays(-1).ToString('o'); validTo = $null
    } | Out-Null
} catch {
    if ([int]$_.Exception.Response.StatusCode -ne 409) { throw }
}
$sample = @{
    sourceType = 0; sourceId = 'pipeline-simulator'; hardwareTimestampNanoseconds = 1000000000
    positionMetres = @{ x = 1; y = 2; z = 3 }; orientation = @{ x = 0; y = 0; z = 0; w = 1 }
    covariance = [double[]]::new(36)
}
Send-Json "$PositioningUrl/api/positioning/EX-SIM/samples" @($sample) | Out-Null
Write-Host "Registered calibration $($active.id) and simulator pose. Start a measurement for EX-SIM in quarry:simulator. Results are test data."
