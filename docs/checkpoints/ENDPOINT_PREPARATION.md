# Endpoint preparation checkpoint (2026-10-06)

Base: acc5f548c392230b445a784299b8654a3ef5e2e5.
Independent increment on published main. Assembly stays 0.1.7; this is not a cumulative V3I release.
V3H is still a separate unpublished candidate. Its blocked publication is not retried here.
No branch creation, force push, installed-service modification or privileged runtime activation.

## Implemented

ProxyEndpointResolver performs one bounded absolute-name DNS lookup, validates every
returned address and pins the first permitted numeric IP in an execution-only copy.
Mixed public/private answers fail closed. Literal addresses do not query DNS.
DNS failures and capacity exhaustion are environment outcomes, not invented remote
TLS failures. A timeout/canceled waiter cannot free the admission slot of an unfinished
underlying lookup; its completion/fault is observed. Four lookups at most are outstanding.
The resolver itself opens no connections and makes no reachability or trust claim.

The normal NonTunCoreProbeTransport path now calls it before creating a worker or
profile directory. DNS and profile preparation consume the existing startup budget.
The stored node, canonical digest and assessment identity are not replaced by the
execution copy. Explicit existing loopback fixtures remain synthetic and cannot
silently exempt an unrelated hostname. Certificate validation is not weakened.

TLS SNI and transport host defaults were reviewed against pinned Mihomo v1.19.32:
VLESS/VMess WS explicit SNI, then Host, then original server; Trojan WS SNI default;
HTTP default Host and separate gRPC/TLS name. Numeric binding keeps those names.
ALPN collections are snapshotted before awaiting. The result's default JSON/ToString
exclude the credential-bearing execution node.

## Deliberate limits

This is conservative address admission, not a universal routability oracle. Special
IPv4/IPv6 space, documentation ranges, NAT64, 6to4, scoped addresses and local names
are refused. Domains using plugins are refused pending plugin-specific rebinding
review; numeric plugin endpoints keep their existing semantics. One OS-selected
address is pinned, not the fastest; there is no multi-address fallback or application
DNS cache. OS resolver caching remains possible. Production connectivity with numeric
pins and all optional protocol/transport combinations is NOT_RUN.

## Local validation before publication

Full solution builds passed with zero warnings/errors. 122 new tests passed.
Six final complete Linux suites: each 741 unique cases, 726 pass, 0 fail, 15 explicit
Windows-specific skips. Native pinned Mihomo and TLS diagnostics enabled.
The six-basic-protocol native case uses mihomo -t only, not a connection.
CI results are PENDING until completed run logs/artifacts for this source are read.
The existing CI will enforce 741 cases, unchanged 15 Linux / 5 Windows skip lists.

The initial full local run on the intermediate 733-case source had 717 pass, 1 fail,
15 skips. Existing AstraV3EResourceTests.TcpAndUdpAreReservedTogetherAndReleased failed
while rebinding a just-released TCP port. Its exact cause is not established; its
code/assertions were not changed and the failed TRX/log are retained. Six later
passes do not prove that intermittent issue fixed.
One old fake-executable test now supplies its explicit loopback fixture so it still
reaches and verifies CORE_START_FAILED; its assertions and deadlines are unchanged.

## Next

Read full Linux/Windows CI artifacts and independently verify counts and source blobs.
Do not transfer old installed-status success to V3H's unpublished handoff.
Before service-owned execution, independently validate/rebind endpoints there too;
never run the V3H staged placeholder-port buffer or accept an executable path from IPC.
TUN, WFP, system DNS/IPv6 protection, network recovery, normal installer and Windows11
remain unaccepted. Main's unprivileged runtime refusal is unchanged.

References: IANA IPv4/IPv6 special-purpose registries; .NET 10 Dns.GetHostAddressesAsync;
MetaCubeX/mihomo tag v1.19.32 adapter/outbound/vless.go, trojan.go and
transport/vmess/websocket.go. No upstream code was copied into this resolver.
