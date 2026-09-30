$ErrorActionPreference = 'Stop'
$python = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
if (!(Test-Path -LiteralPath $python)) {
    throw 'Create .venv and install requirements.txt as described in README.md.'
}
& $python (Join-Path $PSScriptRoot 'calibrate.py') @args
exit $LASTEXITCODE
