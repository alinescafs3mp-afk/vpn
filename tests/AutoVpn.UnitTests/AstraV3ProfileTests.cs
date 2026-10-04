using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Core;

namespace AutoVpn.UnitTests;

public sealed class AstraV3ProfileTests
{
    internal static string Profile(int controller = 18080, int socks = 18081, bool tun = false) => MihomoProfileGenerator.Build(new()
    {
        Secret = new string('a', 48), ControllerPort = controller, SocksPort = socks,
        Tun = tun, LanAccess = false, Nodes = [],
        LoopbackHosts = new Dictionary<string, string> { ["fixture.example"] = "127.0.0.1" },
    });

    [Fact]
    public void GeneratedNonTunProfileHasExplicitLocalEndpoints()
    {
        var contract = RuntimeProfileContract.Parse(Profile());
        Assert.Equal(18080, contract.ControllerPort); Assert.Equal(18081, contract.SocksPort);
        Assert.Equal(new string('a', 48), contract.ControllerSecret);
    }

    [Theory]
    [InlineData("tun: { enable: true }\n")]
    [InlineData("tun: { enable: false }\n")]
    [InlineData("tun: { enable: TRUE }\n")]
    public void EveryTopLevelTunFormIsRefused(string addition)
    {
        var exception = Assert.Throws<InvalidDataException>(() => RuntimeProfileContract.Parse(Profile() + addition));
        Assert.Equal("WINDOWS_TUN_NOT_VALIDATED", exception.Message);
    }

    [Theory]
    [InlineData("external-controller: '127.0.0.1:18080'", "external-controller: '0.0.0.0:18080'")]
    [InlineData("listen: 127.0.0.1", "listen: 0.0.0.0")]
    [InlineData("allow-lan: false", "allow-lan: true")]
    [InlineData("type: socks", "type: tun")]
    [InlineData("port: 18081", "port: 18080")]
    [InlineData("mode: rule", "mode: direct")]
    [InlineData("store-selected: false", "store-selected: true")]
    [InlineData("dns:\n  enable: false", "dns:\n  enable: false\n  listen: '0.0.0.0:53'")]
    public void UnsafeRuntimeChangesAreRejected(string before, string after)
    {
        var yaml = Profile().Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(before, yaml, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => RuntimeProfileContract.Parse(yaml.Replace(before, after, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("mixed-port: 12345\n")]
    [InlineData("external-controller-pipe: 'unprotected-pipe'\n")]
    [InlineData("external-controller: '127.0.0.1:18082'\n")]
    [InlineData("---\nmode: direct\n")]
    [InlineData("hosts: &cycle { x: *cycle }\n")]
    [InlineData("hosts: !custom {}\n")]
    public void AdditionalIngressDuplicateKeysDocumentsAndYamlReferencesAreRejected(string addition)
    {
        Assert.Throws<InvalidDataException>(() => RuntimeProfileContract.Parse(Profile() + addition));
    }

    [Fact]
    public void SecretIsNotIncludedInDefaultJsonOrRecordText()
    {
        var contract = RuntimeProfileContract.Parse(Profile());
        Assert.DoesNotContain(contract.ControllerSecret, contract.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(contract.ControllerSecret, JsonSerializer.Serialize(contract), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[")]
    [InlineData("- sequence-not-map")]
    public void MalformedProfilesFailClosed(string yaml)
    {
        Assert.Throws<InvalidDataException>(() => RuntimeProfileContract.Parse(yaml));
    }

    [Fact]
    public void DeepAndOversizedProfilesAreRejectedBeforeGraphConstruction()
    {
        Assert.Throws<InvalidDataException>(() => RuntimeProfileContract.Parse(new string('[', 40) + "0" + new string(']', 40)));
        Assert.Throws<InvalidDataException>(() => RuntimeProfileContract.Parse(new string('x', 512 * 1024 + 1)));
    }

    [Fact]
    public async Task TunRefusalPrecedesExecutableLookupAndNeverStartsAProcess()
    {
        var runtime = new MihomoRuntimeProcess(null, null);
        var result = await runtime.StartAsync(Profile(tun: true), default);
        Assert.False(result.Started); Assert.Equal(UnavailableNetworkGuard.PlatformReason(), result.ReasonCode);
        Assert.False(runtime.IsRunning); await runtime.StopAsync(default);
    }

    [AstraV3NativeFact]
    public async Task NativeRuntimeOwnsLocalPortsThenStopsItsExactProcess()
    {
        // No public subscription, proxy peer, TUN, system DNS, or firewall changes.
        // An official core is explicitly provisioned and hash-checked by the native test command.
        var first = new TcpListener(IPAddress.Loopback, 0); first.Start();
        var second = new TcpListener(IPAddress.Loopback, 0); second.Start();
        var controller = ((IPEndPoint)first.LocalEndpoint).Port;
        var socks = ((IPEndPoint)second.LocalEndpoint).Port;
        first.Stop(); second.Stop();
        await using var owner = new OwnedCoreSupervisor(() => new MihomoRuntimeProcess(
            Environment.GetEnvironmentVariable("AUTOVPN_MIHOMO_PATH"), TestCorePins.ExpectedHash));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var result = await owner.StartAsync(Profile(controller, socks), 1, "native-fixture", deadline.Token);
        Assert.True(result.Started, result.ReasonCode); Assert.True(owner.IsRunning(1, "native-fixture"));
        await owner.StopAsync(1, "native-fixture", deadline.Token);
        Assert.False(owner.IsRunning(1, "native-fixture")); Assert.Equal("Idle", owner.Snapshot().Phase);
        Assert.Equal(AutoVpn.Domain.ReasonCodes.Canceled,
            (await owner.StartAsync(Profile(controller, socks), 1, "native-fixture", deadline.Token)).ReasonCode);
    }
}

public sealed class AstraV3NativeFactAttribute : FactAttribute
{
    public AstraV3NativeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUTOVPN_MIHOMO_PATH")))
            Skip = "Requires explicitly provisioned official core; not a passing native result.";
        else if (OperatingSystem.IsWindows())
            Skip = "Native runtime smoke is Linux-only in this checkpoint; Windows privilege/ACL gate requires its own authorized fixture.";
    }
}
