$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)
Remove-Item Env:AUTOVPN_MIHOMO_PATH -ErrorAction SilentlyContinue
dotnet build AutoVpn.slnx -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test AutoVpn.slnx -c Release --nologo
exit $LASTEXITCODE
