# Builds a DEVELOPMENT bundle. It does not claim a working installed TUN/WFP product.
[CmdletBinding()]
param([switch]$SkipTests, [switch]$IncludePinnedCore)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if ($env:OS -ne 'Windows_NT') { throw 'Run packaging on Windows with PowerShell 7 and .NET SDK from global.json.' }
if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'test.ps1') }
$out = Join-Path $root 'artifacts/AutoVPN-astra-r1-win-x64-development'
$marker = Join-Path $out '.autovpn-generated-package'
if (Test-Path $out) {
    if (-not (Test-Path -LiteralPath $marker)) { throw "Refusing to delete an unmarked directory: $out" }
    Remove-Item -LiteralPath $out -Recurse -Force
}
New-Item -ItemType Directory -Force $out | Out-Null
Set-Content -LiteralPath $marker -Value 'AutoVPN generated development package' -Encoding utf8
$projects = @(
    @{ Project = 'src/AutoVpn.Desktop/AutoVpn.Desktop.csproj'; Name = 'desktop' },
    @{ Project = 'src/AutoVpn.Service/AutoVpn.Service.csproj'; Name = 'service' },
    @{ Project = 'src/AutoVpn.Recovery/AutoVpn.Recovery.csproj'; Name = 'recovery' },
    @{ Project = 'src/AutoVpn.Inventory/AutoVpn.Inventory.csproj'; Name = 'inventory' }
)
foreach ($item in $projects) {
    dotnet publish $item.Project -c Release -r win-x64 --self-contained true '-p:Platform=AnyCPU' -o (Join-Path $out $item.Name) --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($item.Project)" }
}
if ($IncludePinnedCore) {
    $corePath = & (Join-Path $PSScriptRoot 'fetch-core.ps1') -Destination (Join-Path $root 'artifacts/core')
    $coreDirectory = Join-Path $out 'core'; New-Item -ItemType Directory -Force $coreDirectory | Out-Null
    Copy-Item -LiteralPath $corePath -Destination (Join-Path $coreDirectory 'mihomo.exe')
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/MetaCubeX/mihomo/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/LICENSE' -OutFile (Join-Path $coreDirectory 'LICENSE-MIHOMO.txt') -TimeoutSec 60
    Set-Content -LiteralPath (Join-Path $coreDirectory 'UPSTREAM.txt') -Encoding utf8 -Value 'Mihomo v1.19.32. Source: https://github.com/MetaCubeX/mihomo/tree/88dcbf7f1614a67c3b36b848ee3592dfa92ada36 . Executable identity is checked against config/core-manifest.json. No signature is claimed.'
}
Copy-Item 'LICENSE' $out
Copy-Item 'docs/checkpoints/V1.md' (Join-Path $out 'CHECKPOINT-V1.md')
Copy-Item 'THIRD_PARTY_NOTICES.md' $out
Copy-Item 'docs/GROK_BUILD_HANDOFF.md' $out
@'
AutoVPN / Astra R1 / DEVELOPMENT ONLY

This package contains real compiled Windows executables, not an installer and NOT an accepted VPN release.
The desktop and current-user console broker can be exercised without changing routes/firewall/TUN.
The production SCM/TUN/WFP/recovery implementation is still incomplete. This bundle cannot provide a usable computer-wide VPN connection yet.
Do not disable safeguards or present compilation as a passed Windows networking gate.

Launch: desktop\AutoVpn.Desktop.exe
Optional console broker: service\AutoVpn.Service.exe
Close the desktop through its tray Exit item, then stop the console broker with Ctrl+C.
No service registration, driver installation, administrator elevation or silent network changes are performed by these launch steps.
The user catalogue stays under the current user's LocalApplicationData\AutoVPN.
See GROK_BUILD_HANDOFF.md and the source implementation status.
'@ | Set-Content -LiteralPath (Join-Path $out 'READ-ME-FIRST.txt') -Encoding utf8
$commit = 'source-archive'
if (Test-Path (Join-Path $root '.git')) { $commit = (git rev-parse HEAD).Trim(); if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit.' } }
@{ sourceCommit = $commit; status = 'DEVELOPMENT_NOT_VPN_RELEASE'; testsSkippedDuringPackaging = [bool]$SkipTests; utc = [DateTime]::UtcNow.ToString('o'); sdk = (dotnet --version); target = 'win-x64' } | ConvertTo-Json | Set-Content (Join-Path $out 'BUILD-INFO.json') -Encoding utf8
$hashes = Get-ChildItem $out -File -Recurse | Sort-Object FullName | ForEach-Object {
    @{ path = [IO.Path]::GetRelativePath($out, $_.FullName).Replace('\','/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$hashes | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $out 'SHA256-MANIFEST.json') -Encoding utf8
$zip = $out + '.zip'
if (Test-Path $zip) { Remove-Item -LiteralPath $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content ($zip + '.sha256') -Encoding ascii
Write-Host "Built DEVELOPMENT bundle: $zip"
Write-Host 'NOT release-ready: installed TUN, WFP, DNS/IPv6, recovery and installer gates remain open.'
