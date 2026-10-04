using System.Text.Json;
using AutoVpn.Infrastructure.Core;

namespace AutoVpn.UnitTests;

public sealed class AstraV3InstallationTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"assets\":false}")]
    [InlineData("{broken")]
    public void InvalidManifestDoesNotAuthorizeExecutable(string manifest)
    {
        using var fixture = new ManifestFixture(manifest);
        Assert.Null(CoreInstallation.Read(fixture.Root).Sha256);
    }

    [Fact]
    public void MissingManifestDoesNotAuthorizeExecutable()
    {
        using var fixture = new ManifestFixture(null);
        Assert.Null(CoreInstallation.Read(fixture.Root).Sha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactlyOnePlatformPinIsRequired(bool duplicate)
    {
        var name = OperatingSystem.IsWindows() ? "mihomo-windows-amd64.exe" : "mihomo-linux-amd64";
        var pin = new { name, sha256 = new string('a', 64) };
        var pins = duplicate ? new[] { pin, pin } : new[] { pin };
        using var fixture = new ManifestFixture(JsonSerializer.Serialize(new { assets = pins }));
        Assert.Equal(duplicate ? null : new string('a', 64), CoreInstallation.Read(fixture.Root).Sha256);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void MalformedHashDoesNotAuthorizeExecutable(string hash)
    {
        var name = OperatingSystem.IsWindows() ? "mihomo-windows-amd64.exe" : "mihomo-linux-amd64";
        using var fixture = new ManifestFixture(JsonSerializer.Serialize(new { assets = new[] { new { name, sha256 = hash } } }));
        Assert.Null(CoreInstallation.Read(fixture.Root).Sha256);
    }

    private sealed class ManifestFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "autovpn-v3-pin-" + Guid.NewGuid().ToString("N"));
        public ManifestFixture(string? manifest)
        {
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            if (manifest is not null) File.WriteAllText(Path.Combine(Root, "config", "core-manifest.json"), manifest);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
