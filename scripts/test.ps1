[CmdletBinding()]
param([switch]$Native, [string]$CorePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$filter = 'FullyQualifiedName!~S603_DisabledFamily'
if ($Native) {
    if (-not $CorePath) { $CorePath = $env:AUTOVPN_MIHOMO_PATH }
    if (-not $CorePath -or -not (Test-Path -LiteralPath $CorePath -PathType Leaf)) { throw 'Native tests require -CorePath pointing to the pinned Mihomo executable.' }
    $manifest = Get-Content (Join-Path $root 'config/core-manifest.json') -Raw | ConvertFrom-Json
    $assetName = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'mihomo-windows-amd64.exe' } else { 'mihomo-linux-amd64' }
    $hash = ($manifest.assets | Where-Object name -eq $assetName).sha256
    if ((Get-FileHash -LiteralPath $CorePath -Algorithm SHA256).Hash -ne $hash) { throw 'Pinned core hash mismatch. No native process launched.' }
    $env:AUTOVPN_MIHOMO_PATH = (Resolve-Path -LiteralPath $CorePath).Path
    $env:R5_CORE_PATH = $env:AUTOVPN_MIHOMO_PATH; $env:R5_CORE_HASH = $hash
    $env:R6_CORE_PATH = $env:AUTOVPN_MIHOMO_PATH; $env:R6_CORE_HASH = $hash
} else {
    'AUTOVPN_MIHOMO_PATH','R5_CORE_PATH','R5_CORE_HASH','R6_CORE_PATH','R6_CORE_HASH' | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
    $filter += '&FullyQualifiedName!~Native_Round5&FullyQualifiedName!~Round6NativeHandshakeTests'
}
dotnet build AutoVpn.slnx -c Release --nologo '-p:Platform=Any CPU'
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
New-Item -ItemType Directory -Force (Join-Path $root 'artifacts/test-results') | Out-Null
dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --no-build --nologo '-p:Platform=AnyCPU' --filter $filter --logger 'trx;LogFileName=results.trx' --results-directory artifacts/test-results --blame-hang-timeout 90s --blame-hang-dump-type none
if ($LASTEXITCODE -ne 0) { throw 'Tests failed or aborted. Read artifacts/test-results/results.trx.' }
