$ErrorActionPreference = 'Stop'
$python = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
if (!(Test-Path -LiteralPath $python)) { throw 'Create the calibration .venv as described in README.md.' }
& $python (Join-Path $PSScriptRoot 'compare_depth.py') @args
exit $LASTEXITCODE
