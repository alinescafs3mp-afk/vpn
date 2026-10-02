# Publishes a self-contained win-x64 folder. This is not an installer.
# It does not download Mihomo or Wintun and it does not register a service.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\win-x64"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
# Short names match the local tar layout (desktop/service/recovery/inventory).
$projects = @(
    @{ Project = "src\AutoVpn.Desktop\AutoVpn.Desktop.csproj"; Name = "desktop" },
    @{ Project = "src\AutoVpn.Service\AutoVpn.Service.csproj"; Name = "service" },
    @{ Project = "src\AutoVpn.Recovery\AutoVpn.Recovery.csproj"; Name = "recovery" },
    @{ Project = "src\AutoVpn.Inventory\AutoVpn.Inventory.csproj"; Name = "inventory" }
)
foreach ($item in $projects) {
    dotnet publish (Join-Path $root $item.Project) -c Release -r win-x64 --self-contained true -o (Join-Path $out $item.Name) --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
Write-Host "Published to $out"
Write-Host "NOT an installer. Unsigned. Windows behavior NOT_RUN."
