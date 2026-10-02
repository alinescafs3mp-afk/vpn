# Publishes a self-contained win-x64 folder. This is not an installer.
# It does not download Mihomo or Wintun and it does not register a service.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\win-x64"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$projects = @(
    "src\AutoVpn.Service\AutoVpn.Service.csproj",
    "src\AutoVpn.Recovery\AutoVpn.Recovery.csproj",
    "src\AutoVpn.Inventory\AutoVpn.Inventory.csproj",
    "src\AutoVpn.Desktop\AutoVpn.Desktop.csproj"
)
foreach ($project in $projects) {
    $name = Split-Path (Split-Path $project -Parent) -Leaf
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true -o (Join-Path $out $name) --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
Write-Host "Published to $out"
Write-Host "NOT an installer. Unsigned. Windows behavior NOT_RUN."
