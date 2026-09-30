$ErrorActionPreference = 'Stop'
$python = Join-Path $PSScriptRoot '.venv\Scripts\pythonw.exe'
if (!(Test-Path -LiteralPath $python)) { throw 'Create the calibration .venv as described in README.md.' }
Start-Process -FilePath $python -ArgumentList ('"' + (Join-Path $PSScriptRoot 'compare_gui.py') + '"') -WindowStyle Hidden
