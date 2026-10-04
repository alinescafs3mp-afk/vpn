# V3 source candidate packaging. Run only after validating the exact source. No SCM/TUN/WFP or installer claims.
[CmdletBinding()]
param([switch]$IncludePinnedCore)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if ($env:OS -ne 'Windows_NT') { throw 'Use Windows x64, PowerShell 7 and the SDK in global.json.' }
& (Join-Path $PSScriptRoot 'test-v3.ps1')
$commit = 'source-archive-see-SOURCE_MANIFEST'
$manifestHash = $null
$outerManifest = Join-Path (Split-Path -Parent $root) 'SOURCE_MANIFEST.json'
if (Test-Path -LiteralPath $outerManifest) {
    $identity = Get-Content -LiteralPath $outerManifest -Raw | ConvertFrom-Json
    $commit = $identity.commit
    $manifestHash = (Get-FileHash -LiteralPath $outerManifest -Algorithm SHA256).Hash.ToLowerInvariant()
}
if (Test-Path (Join-Path $root '.git')) {
    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit.' }
    if (git status --porcelain --untracked-files=normal) { throw 'Source has tracked changes or untracked files; commit the intended source or use a verified source archive.' }
}
$out = Join-Path $root 'artifacts/AutoVPN-V3-win-x64-DEVELOPMENT'
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
Copy-Item LICENSE,THIRD_PARTY_NOTICES.md,docs/GROK_BUILD_HANDOFF_V3.md,docs/checkpoints/V3.md,docs/GROK_BUILD_HANDOFF_V3B.md,docs/checkpoints/V3B.md,README_V3B_RU.md $out
@'
AutoVPN V3B / DEVELOPMENT ONLY / Not a working computer-wide VPN release

Launch desktop\AutoVpn.Desktop.exe. A same-user console broker is in service/.
SCM/TUN/WFP, a privileged runtime handoff and an installer remain unfinished.
Do not register this console executable as a service or remove refusing adapters.

V3 adds an owned non-TUN process backend, controller readiness checks, precise
stop/cancellation ownership, actual process-exit observation, two-target broker
eligibility and truthful protection state. It still refuses production TUN.
The runtime is deliberately not available as an elevated Windows component.
Only normal managed tests run during this packaging command. Their raw TRX is
included under evidence/. Native, WPF and Windows networking gates are separate.

This build has no Authenticode signature and installs no driver.
Use a fresh or backed-up Windows profile. Read GROK_BUILD_HANDOFF_V3B.md first.
Unknown ping, speed, country and capability are not measurements.
'@ | Set-Content (Join-Path $out 'READ-ME-FIRST.txt') -Encoding utf8
New-Item -ItemType Directory (Join-Path $out 'evidence') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'artifacts/test-results/results.trx') -Destination (Join-Path $out 'evidence/normal-results.trx')
@{ sourceCommit=$commit; sourceManifestSha256=$manifestHash; sourceAttribution='Local source identity, not a remote-publication or reproducible-build attestation'; checkpoint='V3B'; version='0.1.3'; status='DEVELOPMENT_NOT_VPN_RELEASE'; normalTestsRunDuringPackaging=$true; nativeTestsRunDuringPackaging=$false; windowsNetworking='NOT_RUN'; normalTestEvidence='evidence/normal-results.trx'; includesPinnedCore=[bool]$IncludePinnedCore; utc=[DateTime]::UtcNow.ToString('o'); sdk=(dotnet --version); target='win-x64' } | ConvertTo-Json | Set-Content (Join-Path $out 'BUILD-INFO.json') -Encoding utf8
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
