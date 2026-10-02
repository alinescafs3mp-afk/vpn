$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)
dotnet build AutoVpn.slnx -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
