using System.Text.Json;

namespace AutoVpn.Infrastructure.Core;

public sealed record CoreInstallation(string? Path, string? Sha256)
{
    public static CoreInstallation Read(string baseDirectory)
    {
        var binary = Environment.GetEnvironmentVariable("AUTOVPN_MIHOMO_PATH");
        if (string.IsNullOrWhiteSpace(binary))
        {
            var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, "..", "core", "mihomo.exe"));
            binary = File.Exists(candidate) ? candidate : null;
        }
        var manifest = System.IO.Path.Combine(baseDirectory, "config", "core-manifest.json");
        try
        {
            if (!File.Exists(manifest)) return new(binary, null);
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return new(binary, null);
            var expectedName = OperatingSystem.IsWindows() ? "mihomo-windows-amd64.exe" : "mihomo-linux-amd64";
            var pins = assets.EnumerateArray().Where(asset => asset.ValueKind == JsonValueKind.Object &&
                asset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == expectedName).ToArray();
            if (pins.Length != 1 || !pins[0].TryGetProperty("sha256", out var hash) || hash.ValueKind != JsonValueKind.String)
                return new(binary, null);
            var value = hash.GetString();
            return new(binary, value is { Length: 64 } && value.All(Uri.IsHexDigit) ? value : null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new(binary, null); }
    }
}
