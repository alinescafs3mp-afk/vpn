# V2 development checkpoint only. No SCM/TUN/WFP or installer claims.
[CmdletBinding()]
param([switch]$IncludePinnedCore)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if ($env:OS -ne 'Windows_NT') { throw 'Use Windows x64, PowerShell 7 and the SDK in global.json.' }
& (Join-Path $PSScriptRoot 'test.ps1')
$commit = 'source-archive-see-SOURCE_MANIFEST'
if (Test-Path (Join-Path $root '.git')) {
    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit.' }
    if (git status --porcelain --untracked-files=no) { throw 'Tracked source is modified; commit it or use a verified source archive.' }
}
$out = Join-Path $root 'artifacts/AutoVPN-V2-win-x64-DEVELOPMENT'
$marker = Join-Path $out '.autovpn-generated-package'
if (Test-Path $out) {
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Refusing to remove an unmarked output directory.' }
    if ((Get-Item -LiteralPath $out).LinkType) { throw 'Output directory must not be a link.' }
    Remove-Item -LiteralPath $out -Recurse -Force
}
New-Item -ItemType Directory $out | Out-Null
'AutoVPN generated DEVELOPMENT package' | Set-Content -LiteralPath $marker -Encoding utf8
foreach ($item in @(
    @{ Project='src/AutoVpn.Desktop/AutoVpn.Desktop.csproj'; Name='desktop' },
    @{ Project='src/AutoVpn.Service/AutoVpn.Service.csproj'; Name='service' },
    @{ Project='src/AutoVpn.Recovery/AutoVpn.Recovery.csproj'; Name='recovery' },
    @{ Project='src/AutoVpn.Inventory/AutoVpn.Inventory.csproj'; Name='inventory' }
)) {
    dotnet publish $item.Project -c Release -r win-x64 --self-contained true '-p:Platform=AnyCPU' -o (Join-Path $out $item.Name) --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($item.Project)" }
}
if ($IncludePinnedCore) {
    $core = & (Join-Path $PSScriptRoot 'fetch-core.ps1')
    $dir = Join-Path $out 'core'; New-Item -ItemType Directory $dir | Out-Null
    Copy-Item -LiteralPath $core -Destination (Join-Path $dir 'mihomo.exe')
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/MetaCubeX/mihomo/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/LICENSE' -OutFile (Join-Path $dir 'LICENSE-MIHOMO.txt') -TimeoutSec 60
    'Official Mihomo v1.19.32 source: https://github.com/MetaCubeX/mihomo/tree/88dcbf7f1614a67c3b36b848ee3592dfa92ada36 . See config/core-manifest.json for archive/executable hashes. No signature claim.' | Set-Content (Join-Path $dir 'UPSTREAM.txt') -Encoding utf8
}
Copy-Item LICENSE,THIRD_PARTY_NOTICES.md,docs/GROK_BUILD_HANDOFF_V2.md,docs/checkpoints/V2.md $out
@'
AutoVPN V2 / DEVELOPMENT ONLY / Not a working computer-wide VPN release

Launch desktop\AutoVpn.Desktop.exe. A same-user console broker is in service/.
SCM/TUN/WFP, a privileged runtime handoff and an installer remain unfinished.
Do not register this console executable as a service or remove refusing adapters.

V2 adds continuous two-target candidate maintenance, automatic-check pause/resume,
consent revocation, pre-connect two-target verification and conservative payload
reservations. Public network work requires explicit consent; the packaged core
is optional. Without it the catalogue reports core unavailable, not fake success.

This build has no Authenticode signature and installs no driver.
Use a fresh or backed-up Windows profile. Read GROK_BUILD_HANDOFF_V2.md first.
Unknown ping, speed, country and capability are not measurements.
'@ | Set-Content (Join-Path $out 'READ-ME-FIRST.txt') -Encoding utf8
@{ sourceCommit=$commit; checkpoint='V2'; version='0.1.2'; status='DEVELOPMENT_NOT_VPN_RELEASE'; testsSkippedDuringPackaging=$false; includesPinnedCore=[bool]$IncludePinnedCore; utc=[DateTime]::UtcNow.ToString('o'); sdk=(dotnet --version); target='win-x64' } | ConvertTo-Json | Set-Content (Join-Path $out 'BUILD-INFO.json') -Encoding utf8
$hashes = Get-ChildItem $out -File -Recurse | Sort-Object FullName | ForEach-Object {
    @{ path=[IO.Path]::GetRelativePath($out,$_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$hashes | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $out 'SHA256-MANIFEST.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'verify-release.ps1') -PackageDirectory $out
$zip=$out+'.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content ($zip+'.sha256') -Encoding ascii
Write-Host "Built DEVELOPMENT only: $zip"
