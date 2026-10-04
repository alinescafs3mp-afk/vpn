[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$base = (Resolve-Path -LiteralPath $PackageDirectory).Path
$manifest = Get-Content (Join-Path $base 'SHA256-MANIFEST.json') -Raw | ConvertFrom-Json
$known = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest) {
    $path = [IO.Path]::GetFullPath((Join-Path $base $entry.path))
    if (-not $path.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes package root.' }
    if (-not $known.Add($path)) { throw "Duplicate package entry: $($entry.path)" }
    $file = Get-Item -LiteralPath $path
    if ($file.LinkType) { throw "Package symlink is not allowed: $($entry.path)" }
    if ($file.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Package identity mismatch: $($entry.path)" }
}
Get-ChildItem -LiteralPath $base -File -Recurse | ForEach-Object {
    if ($_.Name -ne 'SHA256-MANIFEST.json' -and -not $known.Contains($_.FullName)) { throw "Unlisted file: $($_.FullName)" }
}
Write-Host 'Package file identities verified. This does NOT establish Windows networking or release readiness.'
