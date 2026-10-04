[CmdletBinding()]
param([string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/core'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content (Join-Path $root 'config/core-manifest.json') -Raw | ConvertFrom-Json
$isWin = $env:OS -eq 'Windows_NT'
$archiveName = if ($isWin) { 'mihomo-windows-amd64-v1.19.32.zip' } else { 'mihomo-linux-amd64-v1.19.32.gz' }
$binaryName = if ($isWin) { 'mihomo-windows-amd64.exe' } else { 'mihomo-linux-amd64' }
New-Item -ItemType Directory -Force $Destination | Out-Null
$archive = Join-Path $Destination $archiveName
$url = "https://github.com/MetaCubeX/mihomo/releases/download/$($manifest.tag)/$archiveName"
Invoke-WebRequest -Uri $url -OutFile $archive -TimeoutSec 120
$archiveHash = ($manifest.assets | Where-Object name -eq $archiveName).sha256
if (-not $archiveHash -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) { Remove-Item $archive; throw 'Core archive hash mismatch.' }
$binary = Join-Path $Destination $binaryName
if ($isWin) {
    $expand = Join-Path $Destination ('extract-' + [guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $archive -DestinationPath $expand
    try {
        $candidates = @(Get-ChildItem $expand -File -Recurse -Filter '*.exe')
        if ($candidates.Count -ne 1) { throw 'Pinned archive does not contain exactly one executable.' }
        Copy-Item -LiteralPath $candidates[0].FullName -Destination $binary -Force
    } finally { Remove-Item -LiteralPath $expand -Recurse -Force }
} else {
    $archiveStream = [IO.File]::OpenRead($archive)
    try {
        $gzip = [IO.Compression.GZipStream]::new($archiveStream, [IO.Compression.CompressionMode]::Decompress)
        $output = [IO.File]::Create($binary)
        try { $gzip.CopyTo($output) } finally { $output.Dispose(); $gzip.Dispose() }
    } finally { $archiveStream.Dispose() }
    chmod 755 $binary
    if ($LASTEXITCODE -ne 0) { throw 'Cannot make core executable.' }
}
$hash = ($manifest.assets | Where-Object name -eq $binaryName).sha256
if (-not $hash -or (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $hash) { Remove-Item $binary; throw 'Core executable hash mismatch.' }
Write-Output (Resolve-Path -LiteralPath $binary).Path
