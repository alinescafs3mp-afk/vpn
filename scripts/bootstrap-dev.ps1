$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)
dotnet --info
Write-Host "SDK pin is global.json. This script does not download Mihomo or change the network."
