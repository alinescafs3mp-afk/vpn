using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Probe;
using Xunit;

namespace AutoVpn.UnitTests;

// Acceptance expectations. These tests never install TUN or firewall rules.
public sealed class IndependentRound3Tests_BrokerTls
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task B01_TightenedCertificatePolicyMustInvalidateUnconfirmedSession()
    {
        var catalogue = Catalogue(insecure: true);
        var core = new GateCore();
        var engine = Engine(catalogue, core);
        var response = await Connect(engine, catalogue);
        Assert.True(response.Ok);
        catalogue.Settings = catalogue.Settings with { AllowInsecureCertificates = false, Revision = 2 };
        Confirm(engine, catalogue);
        Assert.NotEqual(TunnelPhase.Connected, engine.State.Phase);
    }

    [Fact]
    public async Task B02_AutomaticallySelectedNodeExcludedDuringStartMustNotBeCommitted()
    {
        var catalogue = Catalogue();
        var core = new GateCore { GateAt = 1 };
        var engine = Engine(catalogue, core);
        var connect = engine.HandleAsync(Request(engine, IpcOperations.Connect, new ConnectPayload
        { NodeId = "", Digest = "", NetworkEpoch = catalogue.NetworkEpoch }), CancellationToken.None);
        await core.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        catalogue.TrySetExcluded(engine.State.ActiveNodeId!, true);
        core.Release.TrySetResult();
        var response = await connect;
        Assert.False(response.Ok, "Automatically selected candidate was committed after exclusion during asynchronous start.");
    }

    [Fact]
    public async Task B03_NetworkEpochChangeDuringStartMustRejectCommit()
    {
        var catalogue = Catalogue();
        var core = new GateCore { GateAt = 1 };
        var engine = Engine(catalogue, core);
        var connect = Connect(engine, catalogue);
        await core.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        catalogue.SetNetworkEpoch(2);
        core.Release.TrySetResult();
        var response = await connect;
        Assert.False(response.Ok, "A start selected on the old physical network was still committed.");
    }

    [Fact]
    public async Task B04_CoreExitMustClearConnectedBeforeReplacementReadiness()
    {
        var catalogue = Catalogue();
        var core = new GateCore { GateAt = 2 };
        var engine = Engine(catalogue, core);
        await Connect(engine, catalogue);
        Confirm(engine, catalogue);
        await Standby(engine, catalogue);
        var switching = Health(engine, catalogue);
        await core.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Assert.NotEqual(TunnelPhase.Connected, engine.State.Phase);
            Assert.False(engine.Snapshot().CoreRunning);
        }
        finally
        {
            core.Release.TrySetResult();
            await switching;
        }
    }

    [Fact]
    public async Task B05_PolicyChangedDuringFailoverMustNotLeaveConnected()
    {
        var catalogue = Catalogue();
        var core = new GateCore { GateAt = 2 };
        var engine = Engine(catalogue, core);
        await Connect(engine, catalogue);
        Confirm(engine, catalogue);
        await Standby(engine, catalogue);
        var switching = Health(engine, catalogue);
        await core.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        catalogue.Settings = catalogue.Settings with { LanAccess = false, Revision = 2 };
        core.Release.TrySetResult();
        await switching;
        Assert.NotEqual(TunnelPhase.Connected, engine.State.Phase);
        Assert.False(engine.Snapshot().CoreRunning);
    }

    [Fact]
    public async Task B06_AlreadyCancelledConnectMustNotArmProtection()
    {
        var catalogue = Catalogue();
        var guard = new Guard();
        var engine = new BrokerEngine(catalogue, guard, new GateCore(), clock: new FakeClock { UtcNow = Now });
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try { await Connect(engine, catalogue, stop.Token); }
        catch (OperationCanceledException) { }
        Assert.Equal(0, guard.Arms);
    }

    [Fact]
    public async Task B07_ExplicitUnprotectedModeMustNotRequireAnArmedGuard()
    {
        var catalogue = Catalogue();
        catalogue.Settings = catalogue.Settings with { ProtectionOnConnect = false };
        var guard = new Guard { ReportUnarmedWhenProtectionDisabled = true };
        var engine = new BrokerEngine(catalogue, guard, new GateCore(), clock: new FakeClock { UtcNow = Now });
        var response = await Connect(engine, catalogue);
        Assert.True(response.Ok, "An explicitly selected unprotected session is still refused solely because its guard is not armed.");
        Assert.False(response.Snapshot!.ProtectionArmed);
    }

    [Fact]
    public async Task T01_Authenticated200HeadersAloneMustNotProveExpected204()
    {
        await using var fixture = new TlsSocksFixture("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 100\r\n\r\n");
        var result = await Socks5Client.ExchangeAsync(fixture.Endpoint, fixture.Target,
            TimeSpan.FromSeconds(15), fixture.Trust, CancellationToken.None);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task T02_NonHttpStatusLineMustNotBecomeAuthenticatedSuccess()
    {
        await using var fixture = new TlsSocksFixture("NOT-HTTP 204 Fine\r\nContent-Length: 0\r\n\r\n");
        var result = await Socks5Client.ExchangeAsync(fixture.Endpoint, fixture.Target,
            TimeSpan.FromSeconds(15), fixture.Trust, CancellationToken.None);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task T03_Control_AuthenticatedExpected204CanPass()
    {
        await using var fixture = new TlsSocksFixture("HTTP/1.1 204 No Content\r\n\r\n");
        var result = await Socks5Client.ExchangeAsync(fixture.Endpoint, fixture.Target,
            TimeSpan.FromSeconds(15), fixture.Trust, CancellationToken.None);
        Assert.True(result.Authenticated);
        Assert.Equal(204, result.Status);
        Assert.Null(result.Failure);
    }

    private static MemoryCatalogue Catalogue(bool insecure = false)
    {
        var catalogue = new MemoryCatalogue();
        catalogue.Settings = catalogue.Settings with { DisclosureAccepted = true, AllowInsecureCertificates = insecure };
        for (var index = 0; index < 2; index++)
        {
            var semantics = new NodeSemantics
            {
                Protocol = ProtocolKind.Vless, Host = "203.0.113." + (10 + index), Port = 443,
                UserId = "11111111-1111-4111-8111-111111111111", Security = "tls", Encryption = "none",
                Transport = "tcp", SkipCertVerify = insecure,
            };
            catalogue.ApplySnapshot(new SnapshotCommit
            {
                ArtifactId = "synthetic-" + index, FamilyId = "black-vless", ContentHash = "synthetic",
                Complete = true, NowUtc = Now, Nodes = [new SnapshotNode
                {
                    Digest = CanonicalIdentity.Digest(semantics), Semantics = semantics, Label = "synthetic",
                    ArtifactId = "synthetic-" + index, FamilyId = "black-vless",
                }],
            });
        }
        foreach (var node in catalogue.Nodes)
        {
            catalogue.ApplyAssessment(node.NodeId, new AssessmentSnapshot
            { Digest = node.Digest, NetworkEpoch = 1, Health = HealthState.Healthy, LastSuccessUtc = Now, MedianLatencyMs = 20 });
        }
        return catalogue;
    }

    private static BrokerEngine Engine(MemoryCatalogue catalogue, GateCore core)
        => new(catalogue, new Guard(), core, clock: new FakeClock { UtcNow = Now });
    private static Task<IpcResponse> Connect(BrokerEngine engine, ICatalogue catalogue, CancellationToken token = default)
        => engine.HandleAsync(Request(engine, IpcOperations.Connect, new ConnectPayload
        {
            NodeId = catalogue.Nodes[0].NodeId, Digest = catalogue.Nodes[0].Digest,
            NetworkEpoch = catalogue.NetworkEpoch, ProtectionRequired = catalogue.Settings.ProtectionOnConnect,
        }), token);
    private static void Confirm(BrokerEngine engine, ICatalogue catalogue)
    {
        var snapshot = engine.Snapshot();
        engine.ConfirmProduction(snapshot.BootId!, snapshot.Generation, snapshot.OperationId,
            snapshot.ActiveNodeId, catalogue.NetworkEpoch, true, null);
    }
    private static Task<IpcResponse> Standby(BrokerEngine engine, ICatalogue catalogue)
        => engine.HandleAsync(Request(engine, IpcOperations.ApplyRuntimeSet, new { standbys = new[]
        {
            new StandbyCandidate { NodeId = catalogue.Nodes[1].NodeId, EndpointKey = "synthetic", Country = "", SourceFamilyId = "black-vless" },
        }}), CancellationToken.None);
    private static Task<IpcResponse> Health(BrokerEngine engine, ICatalogue catalogue)
        => engine.HandleAsync(Request(engine, IpcOperations.ReportHealth, new HealthPayload
        { FailureKind = nameof(FailureKind.CoreExit), ConsecutiveFailures = 3, NetworkEpoch = catalogue.NetworkEpoch }), CancellationToken.None);
    private static IpcRequest Request(BrokerEngine engine, string operation, object payload)
        => new() { ProtocolVersion = ProductLimits.IpcProtocolVersion, RequestId = Guid.NewGuid().ToString("N"),
            ExpectedStateRevision = engine.Snapshot().Revision, Operation = operation,
            Payload = JsonSerializer.SerializeToElement(payload, IpcJson.Options) };
    private sealed class Guard : INetworkGuard
    {
        public int Arms { get; private set; }
        public bool ReportUnarmedWhenProtectionDisabled { get; init; }
        public GuardResult Arm(GuardRequest request)
        { Arms++; return new GuardResult(true, !ReportUnarmedWhenProtectionDisabled || request.ProtectionRequired, null, []); }
        public GuardResult Disarm(long generation) => new(true, false, null, []);
        public GuardResult Recover(IReadOnlyList<OwnedEffect> effects) => new(true, false, null, effects.Select(x => x.Id).ToArray());
    }
    private sealed class GateCore : ICoreController
    {
        public int GateAt { get; init; } = -1;
        private int _starts;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _starts) == GateAt)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            return new CoreStartResult(true, null);
        }
        public Task StopAsync(long generation, string operationId, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class TlsSocksFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _certificate;
        private readonly Task _serve;
        public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;
        public Uri Target { get; } = new("https://localhost/generate_204");
        public X509Certificate2Collection Trust => new(_certificate);
        public TlsSocksFixture(string response)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
            _listener.Start();
            _serve = Serve(response);
        }
        private async Task Serve(string response)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                var hello = new byte[3]; await stream.ReadExactlyAsync(hello, _stop.Token);
                await stream.WriteAsync(new byte[] {5, 0}, _stop.Token);
                var head = new byte[5]; await stream.ReadExactlyAsync(head, _stop.Token);
                var rest = new byte[head[4] + 2]; await stream.ReadExactlyAsync(rest, _stop.Token);
                await stream.WriteAsync(new byte[] {5, 0, 0, 1, 127, 0, 0, 1, 0, 0}, _stop.Token);
                await using var ssl = new SslStream(stream, false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, _stop.Token);
                var buffer = new byte[4096];
                var used = 0;
                while (used < buffer.Length)
                {
                    var count = await ssl.ReadAsync(buffer.AsMemory(used), _stop.Token);
                    if (count == 0) { return; }
                    used += count;
                    if (Encoding.ASCII.GetString(buffer, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal)) { break; }
                }
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                await ssl.FlushAsync(_stop.Token);
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or AuthenticationException) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            await _serve.WaitAsync(TimeSpan.FromSeconds(3));
            _certificate.Dispose(); _stop.Dispose();
        }
    }
}
