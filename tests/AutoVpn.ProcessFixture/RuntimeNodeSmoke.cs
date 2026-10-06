using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Probe;
using YamlDotNet.RepresentationModel;

/// <summary>Real local startup/exit evidence. No proxy-peer connection or VPN claim.</summary>
internal static class RuntimeNodeSmoke
{
    internal static async Task<int> RunAsync()
    {
        var cases = new List<CaseResult>();
        bool? standardUser = null;
        var sessionId = 0;
        string? failure = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                (standardUser, sessionId) = CheckWindowsIdentity();
                Require(standardUser == true, "NATIVE_STANDARD_PRIMARY_TOKEN_REQUIRED");
            }
            if (Environment.GetEnvironmentVariable("AUTOVPN_RUNTIME_LAB_START_GATE") == "1")
            {
                using var gate = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                // Console's synchronized reader may block synchronously even via ReadLineAsync.
                // No core exists yet; a missing gate ends this finite fixture with failure.
                var admission = Task.Run(Console.ReadLine, CancellationToken.None);
                Require(await admission.WaitAsync(gate.Token) == "START", "NATIVE_JOB_ADMISSION_REQUIRED");
            }
            var core = Environment.GetEnvironmentVariable("AUTOVPN_MIHOMO_PATH");
            var hash = Environment.GetEnvironmentVariable("R6_CORE_HASH");
            Require(!string.IsNullOrEmpty(core) && File.Exists(core) && hash is { Length: 64 } &&
                hash.All(Uri.IsHexDigit), "NATIVE_PINNED_CORE_REQUIRED");
            var selected = Environment.GetEnvironmentVariable("AUTOVPN_RUNTIME_LAB_PROTOCOL");
            var protocols = new[] { ProtocolKind.Vless, ProtocolKind.Vmess, ProtocolKind.Trojan,
                ProtocolKind.Shadowsocks, ProtocolKind.Hysteria2, ProtocolKind.Tuic };
            Require(selected is null || protocols.Any(p => p.ToString() == selected), "NATIVE_PROTOCOL_INVALID");
            foreach (var protocol in protocols.Where(p => selected is null || p.ToString() == selected))
                cases.Add(await RunCaseAsync(protocol, core!, hash!));
        }
        catch (Exception ex) { failure = SafeCode(ex); }
        var passed = failure is null && cases.Count > 0 && cases.All(item => item.passed);
        Console.WriteLine(JsonSerializer.Serialize(new { passed, standardUser, sessionId, failure, cases }));
        return passed ? 0 : 1;
    }

    private static async Task<CaseResult> RunCaseAsync(ProtocolKind protocol, string core, string hash)
    {
        var result = new CaseResult { protocol = protocol.ToString() };
        var raw = FixtureNode(protocol);
        var selection = RuntimeNodeSelection.Create(raw);
        var runtime = new MihomoRuntimeProcess(core, hash);
        var owner = new OwnedCoreSupervisor(() => runtime);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Process? child = null;
        RuntimeOwnedResources? resources = null;
        try
        {
            var start = await owner.StartNodeAsync(selection, 1, "node-runtime", budget.Token);
            Require(start.Started, "NATIVE_START_" + start.ReasonCode);
            resources = runtime.OwnedResources;
            Require(resources is not null, "NATIVE_RESOURCES_MISSING");
            child = Process.GetProcessById(resources!.ProcessId);
            _ = child.Handle; // Retain an actual handle before Stop; do not look up a reused PID later.
            result.ownedPorts = !child.HasExited &&
                ProbeWorker.ProcessOwnsLoopbackPort(child.Id, resources.ControllerPort) &&
                ProbeWorker.ProcessOwnsLoopbackPort(child.Id, resources.SocksPort);
            Require(result.ownedPorts, "NATIVE_PORT_OWNER_MISMATCH");
            var bytes = await File.ReadAllBytesAsync(Path.Combine(resources.DirectoryPath, "config.yaml"), budget.Token);
            var yaml = new UTF8Encoding(false, true).GetString(bytes);
            var stream = new YamlStream(); stream.Load(new StringReader(yaml));
            var root = (YamlMappingNode)stream.Documents.Single().RootNode;
            var proxies = (YamlSequenceNode)root.Children[new YamlScalarNode("proxies")];
            Require(proxies.Children.Count == 1, "NATIVE_PROXY_COUNT");
            var proxy = (YamlMappingNode)proxies.Children[0];
            string? Scalar(string key) => proxy.Children.TryGetValue(new YamlScalarNode(key), out var value)
                ? ((YamlScalarNode)value).Value : null;
            result.stringsPreserved = Scalar("password") == raw.Password && Scalar("uuid") == raw.UserId &&
                Scalar("server") == raw.Host;
            if (raw.Path is not null)
            {
                var ws = (YamlMappingNode)proxy.Children[new YamlScalarNode("ws-opts")];
                result.stringsPreserved &= ((YamlScalarNode)ws.Children[new YamlScalarNode("path")]).Value == raw.Path;
            }
            Require(result.stringsPreserved, "NATIVE_STRING_CHANGED");
            Require(!root.Children.ContainsKey(new YamlScalarNode("tun")), "NATIVE_TUN_FORBIDDEN");
            var profile = RuntimeProfileContract.Parse(yaml);
            using (var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            using (var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 4096 })
            {
                var url = "http://127.0.0.1:" + profile.ControllerPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/version";
                using var rejected = await client.GetAsync(url, budget.Token);
                Require(rejected.StatusCode == HttpStatusCode.Unauthorized, "NATIVE_CONTROLLER_AUTH_MISSING");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ControllerSecret);
                using var accepted = await client.SendAsync(request, budget.Token);
                using var version = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(budget.Token));
                result.authenticatedVersion = accepted.StatusCode == HttpStatusCode.OK &&
                    version.RootElement.GetProperty("version").GetString()?.TrimStart('v') == ProductLimits.CoreVersion.TrimStart('v');
                Require(result.authenticatedVersion, "NATIVE_CONTROLLER_VERSION_MISMATCH");
            }
            await owner.StopAsync(1, "node-runtime", budget.Token);
            await child.WaitForExitAsync(budget.Token);
            result.processExited = child.HasExited && !runtime.IsRunning;
            result.directoryRemoved = !Directory.Exists(resources.DirectoryPath);
            Require(result.processExited && result.directoryRemoved, "NATIVE_CLEANUP_UNCONFIRMED");
            using var controller = Reclaim(resources.ControllerPort);
            using var socks = Reclaim(resources.SocksPort);
            result.portsReleased = true;
            result.admissionSealed = (await owner.StartNodeAsync(selection, 1, "node-runtime", budget.Token)).ReasonCode == ReasonCodes.Canceled &&
                (await runtime.StartNodeAsync(selection, budget.Token)).ReasonCode == "CORE_CLOSING";
            Require(result.admissionSealed, "NATIVE_OWNER_RESURRECTED");
            result.passed = true;
        }
        catch (Exception ex) { result.failure = SafeCode(ex); }
        finally
        {
            try { await owner.DisposeAsync(); }
            catch (Exception) { result.cleanupFailure = "NATIVE_CLEANUP_UNCONFIRMED"; result.passed = false; }
            child?.Dispose();
        }
        return result;
    }

    private static CorePortLease Reclaim(int port)
    {
        Require(CorePortLease.TryReserve(port, out var lease), "NATIVE_PORT_NOT_RELEASED");
        return lease!;
    }

    private static NodeSemantics FixtureNode(ProtocolKind protocol)
    {
        // Public numeric destination is never contacted: this fixture sends only /version to
        // the local controller. Synthetic credentials include valid non-BMP characters.
        var raw = new NodeSemantics { Protocol = protocol, Host = "8.8.8.8", Port = 443,
            Security = protocol == ProtocolKind.Shadowsocks ? "aead" : "tls", Sni = "fixture.example" };
        return protocol switch
        {
            ProtocolKind.Vless => raw with { UserId = "11111111-2222-3333-4444-555555555555", Transport = "grpc", ServiceName = "native-fixture" },
            ProtocolKind.Vmess => raw with { UserId = "11111111-2222-3333-4444-555555555555", Encryption = "auto",
                Transport = "ws", Path = "/native/🙂?literal=\\u1234&quote='" },
            ProtocolKind.Trojan => raw with { Password = "пароль-🙂-'\\u1234" },
            ProtocolKind.Shadowsocks => raw with { Password = "密码-🙂-'\\u1234", Sni = null, Encryption = "aes-128-gcm" },
            ProtocolKind.Hysteria2 => raw with { Password = "native-🙂-'\\u1234" },
            ProtocolKind.Tuic => raw with { UserId = "11111111-2222-3333-4444-555555555555", Password = "native-🙂-'\\u1234", Congestion = "cubic" },
            _ => throw new SmokeFailure("NATIVE_PROTOCOL_INVALID"),
        };
    }

    [SupportedOSPlatform("windows")]
    private static (bool, int) CheckWindowsIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var impersonation = WindowsIdentity.GetCurrent(ifImpersonating: true);
        using var current = Process.GetCurrentProcess();
        var expected = Environment.GetEnvironmentVariable("AUTOVPN_RUNTIME_LAB_EXPECTED_SID");
        return (impersonation is null && !identity.IsSystem && identity.User?.Value == expected && expected is not null &&
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) && current.SessionId > 0, current.SessionId);
    }

    private static void Require(bool condition, string code) { if (!condition) throw new SmokeFailure(code); }
    private static string SafeCode(Exception ex) => ex is SmokeFailure failure ? failure.Code :
        ex is InvalidDataException && ex.Message.Length is > 0 and <= 64 &&
            ex.Message.All(ch => char.IsAsciiLetterUpper(ch) || char.IsAsciiDigit(ch) || ch == '_') ? ex.Message :
        ex is OperationCanceledException ? "NATIVE_DEADLINE" : "NATIVE_FAILURE_" + ex.GetType().Name;
    private sealed class SmokeFailure(string code) : Exception { internal string Code { get; } = code; }
    private sealed class CaseResult
    {
        public required string protocol { get; init; }
        public bool passed { get; set; }
        public bool ownedPorts { get; set; }
        public bool authenticatedVersion { get; set; }
        public bool stringsPreserved { get; set; }
        public bool processExited { get; set; }
        public bool directoryRemoved { get; set; }
        public bool portsReleased { get; set; }
        public bool admissionSealed { get; set; }
        public string? failure { get; set; }
        public string? cleanupFailure { get; set; }
    }
}
