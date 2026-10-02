# AutoVPN: independent hard audit and mandatory remediation directive

**Recipient:** Grok, implementation lead.  
**Owner communication:** Russian only. Code, identifiers, this directive and engineering reports may remain English.  
**Repository:** `alinescafs3mp-afk/vpn`  
**Audit date:** 2026-10-02  
**Audited commit:** `0b79fb9c135ffb5a510b41319fc3c53cea2d3ac6`  
**Audited tree:** `e2b02f9f179d6f2e9a170812c2e34adc054972b7`  
**Previous implementation/audit commit:** `f0f72d131e9465d9e35122c2c9699deafa667891`  
**Controlling product specification:** `docs/IMPLEMENTATION_DIRECTIVE.md`, Git blob `3601c30aed8b8720b6044a1bfa454e24925a16cb`.  
**Verdict:** **V1 NOT ACCEPTED. A compiling foundation exists, but the installed end-to-end VPN product is not implemented.**

This is an implementation assignment, not a request for another audit-only commit. Fix the defects below, finish the already-agreed v1 functionality, and provide evidence. Do not weaken the original requirements, delete adverse tests, or substitute documentation for executable behavior.

## 1. Evidence boundary and meaning of this verdict

The audit inspected the pinned repository tree, application entrypoints, UI wiring, domain policies, IPC, parsers, generated core configuration, fetch/probe/refresh helpers, persistence/recovery, tests, manifests, build scripts, architecture/status reports and the existing GitHub Actions result. Source references below are repository-relative and refer to the audited commit unless another commit is explicitly stated. Function names are provided instead of fabricated source line numbers.

The original attached directive and the committed directive have the same Git blob hash. This remediation does not introduce a different product scope.

**Independently inspected existing CI evidence:** run `37058056396`, job `111007530738`, for the audited commit. Its log records Ubuntu 24.04.5, .NET SDK 10.0.112, a successful Release solution build with zero warnings/errors, and 36 passed / 0 failed / 0 skipped tests. `AUTOVPN_MIHOMO_PATH` is empty. This verifies what that existing run did; it is not a new test execution by this auditor. The release-list API returned no published releases at inspection time.

**Not executed by this auditor:** a local .NET build/test run, the proposed reproductions below, Windows UI/SCM/DPAPI/TUN/WFP tests, packet captures, live public-node probes, installer execution, or verification of the reported local archive/binary hashes. No independent dependency vulnerability database scan or exhaustive Git-history secret scan was completed. These limitations must not be converted into green checks.

Evidence labels:

- **STATIC:** the defect or missing connection is visible in the inspected source. Proposed reproductions still need to be executed.
- **GAP:** a required implementation or runtime proof is absent from this snapshot.
- **CI:** an observation from the existing workflow/log, not a claim that a user journey passed.
- **WINDOWS VERIFY:** a platform-specific consequence requiring actual Windows confirmation.

Many security/correctness defects are currently latent because the shipped composition refuses to start a real tunnel. They are not claims of a demonstrated live exploit or measured DNS leak. They must be corrected before enabling the corresponding production path.

Priorities are engineering priorities, not CVSS scores. **P0** means a missing release-blocking user journey or protection implementation. **P1** means serious correctness, safety or trust-boundary failure. **P2** means required functionality, robustness, performance, packaging or evidence quality that still needs closure before v1 acceptance. The 34 groups below are not 34 independently exploited vulnerabilities.

The repository's `IMPLEMENTATION_STATUS.md`, `AUDIT.md` and evidence manifest correctly disclose major limitations. Preserve that honesty. The problem is incomplete functionality and remaining defects, not a need to relabel `NOT_RUN` as success.

## 2. Architectural and safety constraints that remain binding

Keep the existing C#/.NET/WPF/Mihomo baseline unless a demonstrated incompatibility justifies a short ADR. Do not restart the project or introduce unrelated engines, cloud services, account systems, mobile clients or a general-purpose networking platform.

The original architecture keeps subscription HTTP fetching, untrusted document parsing and the full user catalogue in the **unelevated desktop/application side**. The privileged broker owns narrowly scoped core/network lifecycle operations, protected effect state and a bounded emergency runtime set. Do not solve the current disconnected pipeline by moving arbitrary subscription parsing, arbitrary URLs, writable database authority or general-purpose commands into LocalSystem. Background catalogue work may continue while the existing desktop is in its tray lifecycle; it does not require another permanent agent.

A broker must independently validate typed bounded requests, the owner's identity and the current operation context. It must not accept raw external YAML, arbitrary executable/profile paths, firewall rules or shell commands. Keep ordinary UI operation unelevated. Establish a protected, explicit boundary for executable files, DLL search, service configuration and effect journals.

Preserve useful existing controls: conservative refusing adapters, bounded IPC frames, nested forbidden-field rejection, secure certificate defaults, separation of imported nodes from external routing policy, identity independent of display labels, preservation on fetch failure/304, explicit production secret protection, and truthful unavailable-test reporting.

Never run destructive networking tests on the machine carrying your only control session. Use an authorized disposable Windows machine/VM with an independent recovery path. Do not contact arbitrary endpoints from hostile fixtures. Synthetic addresses in this document are parser fixtures, not probe targets.

## 3. Findings and mandatory fixes

### F01. P0: the product composition cannot start a VPN

**Evidence: STATIC + GAP.** `src/AutoVpn.Service/Program.cs`, `src/AutoVpn.Infrastructure/Broker/ICoreController.cs`, `UnavailableNetworkGuard.cs`, `src/AutoVpn.Infrastructure/Core/MihomoProcessController.cs`, `src/AutoVpn.Service/AutoVpn.Service.csproj`.

The service constructs `RefusingCoreController` and `UnavailableNetworkGuard`. The former never starts Mihomo; the latter never installs protection. `MihomoProcessController` provides configuration validation, not a running core supervisor. The service entrypoint is a console lifetime rather than a completed installed SCM service. A self-contained build does not supply these missing operations.

**Required fix:** implement the real, owned core lifecycle and an SCM-compatible broker lifetime with clean startup, stop, shutdown and recovery behavior. Retain console mode only as an explicit development mode. Wire the production composition, protected asset lookup, readiness and supervision. Keep refusing behavior for genuinely unsupported environments or missing validated components; never replace it with a success-returning fake.

**Closure:** the installed UI controls the installed broker and an actual pinned Mihomo process. A controlled HTTPS request crosses the intended TUN route. Missing/tampered assets or failed readiness never produce `Connected`. Real Windows evidence is mandatory.

### F02. P0: discovery, refresh and actual candidate testing are not a running application workflow

**Evidence: STATIC + GAP.** `MainWindow.xaml.cs`, `Service/Program.cs`, `Refresh/RefreshMerge.cs`, `Probe/ProbeCoordinator.cs`, `Fetch/GithubTreeParser.cs`, `config/*.json`, `docs/ARCHITECTURE.md`.

Useful helpers exist, but there is no complete product coordinator invoking discovery, download, parse, reconciliation, local validation and publication. There is no production `IProbeTransport`. Registry files explicitly say they are not loaded. The inventory CLI counts source paths; it does not implement subscription updates or prove a node works.

**Required fix:** connect an unelevated application coordinator to the real catalogue and UI. Implement startup/manual/scheduled refresh, bounded mirror fallback, cancellation, resumable validation, progress, source-level outcomes and durable successful state. Separate catalogue refresh from the small active runtime-set transaction so successful refreshes do not restart a healthy tunnel.

**Closure:** launch with no database, import the configured source families, verify candidates and publish measured eligible nodes without test code pre-populating state. Repeat after source failure, restart and offline startup. No placeholder callback may stand in for this journey.

### F03. P0: onboarding and UI controls are disconnected from authoritative state

**Evidence: STATIC.** `src/AutoVpn.Desktop/MainWindow.xaml`, `MainWindow.xaml.cs`, `src/AutoVpn.Domain/Settings.cs`, `Broker/BrokerEngine.cs`, `Contracts/Ipc.cs`.

The disclosure checkbox is checked locally but is not persisted to the broker's required `DisclosureAccepted` setting. Existing tests bypass this by constructing an already-consented catalogue. The button checks the disclosure before deciding whether to disconnect, so removing the check can also prevent a safety action. Server/subscription/favorite views are placeholders. Several controls do not affect an authoritative setting. There is no complete startup/reconnection snapshot subscription. IPC failure can be rendered as disconnected even when the service's actual state is unknown. Explicit Exit shuts down the UI without the promised disconnect sequence.

**Required fix:** implement revisioned authorized settings and view models backed by real data. Safety actions must remain available without reaccepting onboarding. Distinguish `Broker unavailable / state unknown` from `Disconnected`; preserve last-known protection information. Implement startup synchronization, live updates, reconnect/resync, real lists and parameter binding. Window-close may hide to tray; explicit Exit must perform the specified disconnect/recovery handshake or explain why it could not finish. A crash must not be treated as consent to release protection.

**Closure:** clean first-run and UI restart journeys pass through the real IPC boundary, without seeded consent or fabricated measurements. Simulate loss of IPC while connected; the UI must not claim a verified disconnection.

### F04. P1: a single IPC client can monopolize control, including Disconnect

**Evidence: STATIC.** `Broker/LocalIpcServer.cs`: `AcceptLoop`, `ServeOneAsync`, `DrainUntilCloseAsync`, `RoundTripAsync`.

There is one pipe instance and one serial request path. Header/body reads have no per-client deadline. The server waits for the complete engine operation before accepting another request, then drains the client until it closes. An idle connection, partial frame, long-running Start, or a client that holds the connection after receiving its reply can prevent subsequent commands. The client's three-second connect timeout does not bound its response-body read. Direct concurrent calls to `BrokerEngine` in a unit test do not prove real-pipe cancellation works.

**Required fix:** bounded handshake/frame/write deadlines, prompt close after one-shot responses, authenticated bounded sessions, and an asynchronous operation protocol. Serialize authority/state decisions, not entire slow I/O operations. Long jobs must return an operation handle and post guarded completion events. Disconnect/cancel must be processed while Start is waiting. Do not introduce unrestricted concurrent mutations of `BrokerEngine` as the fix. Surface persistent pipe-creation failure instead of retrying forever every 50 ms.

**Closure:** actual pipe tests cover partial headers/bodies, oversized frames, held-open replies and a Start blocked on a gate while a second client requests Disconnect. Completion and resource cleanup must be bounded.

### F05. P1: Windows peer identity and owner authorization are not implemented

**Evidence: STATIC + WINDOWS VERIFY.** `Broker/PipePeer.cs`, `LocalIpcServer.cs`, `Application/IpcDispatcher.cs`, `Service/Program.cs`, `Persistence/SecretProtector.cs`.

Windows peer inspection falls back to the fixed `windows-user` identity rather than an inspected client token/SID/session. `CurrentUserOnly` is not the required authorization scheme for an unelevated owner controlling a broker under another service account. The existing owner lease also lacks a complete disconnect/restart/session lifecycle. This is not evidence that every local user currently has access; it is an unimplemented production boundary.

**Required fix:** explicit restrictive pipe ACLs, actual OS peer identity/session checks, remote-client rejection, a defined authorized owner lease and authenticated server discovery. Address pipe squatting and stale leases without allowing an arbitrary first snapshot caller to become owner. Design DPAPI scopes and protected store paths for the actual installed identities. Do not broadly grant service reconfiguration rights or make a writable catalogue a privileged authority.

**Closure:** two real Windows accounts, the actual service account, a second session, denied remote access, reconnect/lease recovery and a spoofed server are tested. Same-user Linux `SO_PEERCRED` evidence remains useful but cannot close this item.

### F06. P1: revision, replay and idempotency guarantees are bypassable or incomplete

**Evidence: STATIC.** `Application/IpcDispatcher.cs`, `Broker/BrokerEngine.cs`, `Contracts/Ipc.cs`, desktop request construction.

A zero expected revision bypasses state checking, and the desktop routinely uses zero. Only a small request-id window is retained; duplicate delivery is not a durable or lease-scoped idempotency result. Some operations change runtime state without advancing the same authoritative revision. Future concurrent transport handling would expose checks performed outside the actual state transition to races. `RecoverOwned` is not sufficiently separated from an active-session lifecycle.

**Required fix:** define operation-specific revisions, broker boot/owner-lease identity, generation-bound mutations and replay semantics. Compare revisions inside the serialized decision. Return the original result for a legitimate retry where appropriate; reject stale work after disconnect/restart. Reconcile recovery commands with the current session instead of deleting protection underneath a live state.

**Closure:** duplicate Connect, a delayed old request after Disconnect, reordered runtime-set updates, lease renewal and recovery during an active session cannot resurrect or silently mutate the wrong operation.

### F07. P1: production confirmation and core cleanup are not bound to a specific attempt

**Evidence: STATIC.** `Broker/BrokerEngine.cs`: `ConfirmProduction` and connect/failover paths; `Domain/TunnelReducer.cs`; `Broker/ICoreController.cs`.

`ConfirmProduction(bool, reason)` does not identify the node, digest, network epoch or core instance whose path was verified. Generations are not a unique identity for every connect/switch attempt. Health reports are similarly incomplete. A delayed positive result can therefore be applied to a later connecting session after real asynchronous validation is added. A global `StopAsync` is not an owned process handle; stale cancellation must not stop a newer core. Failover also needs an explicit tested switch/restart transaction, not another unscoped Start call.

**Required fix:** carry an immutable context containing broker boot id, owner lease, operation id, node id, configuration digest, network epoch and core generation. Check it again when accepting every completion. Use owned process/runtime handles, protected controller authentication, scoped cancellation and bounded kill-and-wait cleanup. Retain the controller endpoint/secret securely rather than generating and discarding them. Exceptions after arming need explicit state transitions and cleanup policy.

**Closure:** old validation successes, old core-exit events and late cleanup from attempt A cannot confirm, stop or rewrite attempt B. Test this with controlled delayed completions through the transport.

### F08. P1: a confirmed core exit can leave the UI truthfully-unjustifiable `Connected` state

**Evidence: STATIC.** `Domain/Failover.cs`, `Broker/BrokerEngine.cs`, `tests/AutoVpn.UnitTests/BehaviorTests.cs`: `SwitchBudgetStaysOnTheCurrentSession`.

When the switch budget/cooldown is reached, policy returns `WaitCooldown`; the broker can leave the state unchanged even for `CoreExit`. The existing test explicitly expects `Connected` after this event. Retaining the selected node id is not proof that the process or tunnel still exists. Separately, protected blocked states need a deliberate retry path after uplink recovery; requiring a direct unprotected interval is not an acceptable recovery strategy.

**Required fix:** distinguish a live session during an optimization cooldown from an actual outage during a failover cooldown. On confirmed loss, preserve protection and selected-node history but report blocked/reconnecting and accurate process status. Implement bounded guarded retries for pinned, offline and no-eligible-server states under their policies. Fix the incorrect test expectation.

**Closure:** core exits before/at/after the switch cap never remain `Connected`. A target outage alone does not needlessly flap a genuinely healthy tunnel. Uplink recovery can resume safely without an implicit release of protection.

### F09. P1: failure and disconnect cleanup do not consistently preserve the protection contract

**Evidence: STATIC.** `Broker/BrokerEngine.cs`, `Domain/TunnelReducer.cs`, `Persistence/EffectJournal.cs`.

A failed core start after successful arming can automatically disarm protection. Disconnect disarm/recovery sequencing needs to ensure the guard is released last, not before remaining network effects are reconciled. Cancellation and exceptions must not skip cleanup or silently turn a protected failure into ordinary direct traffic.

**Required fix:** explicitly model protected connection failure, authorized disconnect, optional owner-selected unprotected mode and recovery failure. A protected failure remains protected until authorized release or a safe guarded retry. Restore owned network changes before releasing the session guard. Run bounded critical cleanup independently of the already-cancelled request token, while retaining generation/ownership checks. An authorized protection-off setting must be honored and visibly warned about, not silently ignored.

**Closure:** inject failure at each stage: preflight, arm, process spawn, readiness, production validation, switch, stop and restoration. Verify state truth and the specified packet-blocking behavior throughout.

### F10. P0: the protection backend and crash-safe recovery contract are still absent

**Evidence: STATIC + GAP.** `Broker/NetworkGuard.cs`, `UnavailableNetworkGuard.cs`, `Service/Program.cs`, `Recovery/Program.cs`, `scripts/test-windows-admin.ps1`.

The interface and refusing adapter do not implement WFP, adapter/DNS/route ownership or recovery. In-memory armed flags and a TUN `strict-route` setting are not evidence of protection across all promised failure modes.

**Required fix:** implement narrow owned OS effects, protected journal integration, stable ownership identifiers, rollback and startup reconciliation. Verify survival behavior for core crash, broker crash, UI crash, shutdown and restart. Dynamic filters that vanish with the broker cannot alone substantiate a claim that protection survives broker death. Document the actual supported threat and lifetime model. Keep bootstrap/LAN exceptions narrow and visible. Never reset the entire firewall, routing table or DNS configuration.

**Closure:** actual Windows packet-level positive and negative controls show protection and restoration, including unrelated pre-existing rules and competing VPN/network changes. The emergency recovery executable must independently reconcile only AutoVPN-owned effects.

### F11. P1: corrupt or missing journal state can become a false clean recovery on a later run

**Evidence: STATIC; Windows file-handle behavior also WINDOWS VERIFY.** `Persistence/EffectJournal.cs`: `Open`, `Inspect`, `MoveAside`, `Recover`; `Recovery/Program.cs`.

A corrupt journal is quarantined and replaced, but the unknown-recovery condition is held in the current object rather than persisted. Reopening the newly empty database loses that condition and can yield a clean result despite unknown prior OS effects. Missing journal files also produce a success-shaped message without OS reconciliation. The journal's quarantine helper moves only the main file, unlike the catalogue's sidecar handling. The invalid-header path attempts a move while its read handle is still open, which needs correction and Windows verification.

**Required fix:** preserve a durable protected recovery-unknown marker until owned OS state is actually reconciled. Missing/corrupt metadata is not proof of absent effects. Close handles before quarantine, preserve relevant SQLite WAL/SHM state consistently, handle integrity/schema exceptions and validate unique removed-effect identities against observed ownership. Recovery must not depend on a healthy user catalogue.

**Closure:** corrupt journal, recover once, terminate, reopen and recover again; the second run must not become clean without real reconciliation. Repeat with missing journal, existing effects, WAL recovery and read-handle constraints on Windows.

### F12. P1: generated DNS policy does not prove user DNS remains inside the chosen tunnel

**Evidence: STATIC + primary configuration documentation; live leakage NOT_RUN.** `Core/MihomoProfileGenerator.cs`, DNS ADR, reference R2 below.

The generator writes plain DoH nameservers and proxy-server bootstrap resolvers without an explicit tested routing/detour policy for internal user-domain DNS. Rules matching application destination port 53 do not establish how the core's own resolver traffic is sent. The core documentation distinguishes these mechanisms.

**Required fix:** separate endpoint/bootstrap resolution from user-domain resolution. Configure an explicit supported internal DNS path through the intended outbound, with narrowly approved bootstrap exceptions and no recursion through the same unresolved endpoint. Validate the exact pinned core behavior; do not mechanically add a setting without testing its bootstrap consequences. Preserve the selected IPv6 policy and protect or block all advertised address families.

**Closure:** packet capture plus controlled resolver/HTTPS endpoints covers DNS cache miss/hit, IPv4/IPv6, UDP/TCP DNS, encrypted DNS, disconnect, reconnect and core crash. Use a direct-path negative control; report which bootstrap traffic is intentionally allowed.

### F13. P1: literal-address checks are not a complete destination safety boundary

**Evidence: STATIC.** `Domain/NodeSemantics.cs`: `EndpointSafety`; generator and future fetch/probe dialers.

The current checks cover several literal private/metadata addresses but do not validate the actual A/AAAA results of a public-looking hostname and bind those results to the connection. There is no complete resolution/rebinding policy. The IPv4 special-use checks also need a systematic range review rather than selected octets. Documentation addresses deliberately accepted for fixtures must not force a weakened production policy.

**Required fix:** validate all resolved addresses under an explicit destination policy and prevent re-resolution from bypassing the approved decision. Apply the boundary separately to subscription origins, proxy endpoints and approved test targets. Keep local controlled test endpoints behind an explicit test-only allow policy. Do not create a general arbitrary-URL probe capability in the privileged broker.

**Closure:** controlled resolver fixtures cover mixed public/private answers, IPv4-mapped IPv6, loopback, link-local, metadata, hostname aliases and rebinding between resolution and dial. No hostile fixture performs a real external connection.

### F14. P1: healthy nodes can remain permanently untested, and old probes can be stamped with a new epoch

**Evidence: STATIC.** `Probe/ProbeCoordinator.cs`: `RunAsync`; `Domain/Eligibility.cs`; `Application/Catalogue.cs`.

The probe loop skips every `Healthy`/`Degraded` node without checking freshness or epoch. After expiration or a physical-network change, eligibility can reject the old assessment while the loop still refuses to recheck it. Conversely, the result is stamped with `catalogue.NetworkEpoch` after the await, so work started on an old network can be published as new-network evidence.

**Required fix:** schedule using freshness, purpose and captured context, not a stored health label alone. Capture immutable node/configuration/network/policy context before I/O and reject mismatched completion. Use completion-time measurements and monotonic durations. Apply the separate short pre-connect freshness requirement to the actual candidate immediately before selection.

**Closure:** a 31-minute-old healthy node is rechecked; an epoch-changed healthy node is rechecked; a delayed success spanning an epoch change is discarded rather than relabeled. A pre-connect check older than the specified 60 seconds is not reused as fresh admission.

### F15. P1: the admission scheduler does not meet the verification, fairness or traffic contract

**Evidence: STATIC + GAP.** `Probe/ProbeCoordinator.cs`, `Domain/ProductLimits.cs`, scheduling helpers.

One transport Boolean and one latency value currently publish `Healthy`; there is no real isolated transport or two-target admission chain. A dictionary caps total attempts per endpoint at two instead of concurrent attempts. The same first two failing variants can starve a later working variant on every pass. The 20-second loop budget is only checked between awaits and does not cancel a stuck request; it also is not the specified per-candidate admission budget. Retry-after, fair queue progress and cumulative traffic caps are not enforced by constants alone.

**Required fix:** implement native-config validation plus actual candidate-local HTTPS checks against the approved independent targets, response/TLS validation, capability classification, request cancellation and bounded sample history. Implement concurrency semaphores, resumable fair queues, endpoint fairness, real retry backoff and accounting. Honor the original per-request/per-node budgets and a separate explicit cycle budget. Do not infer speed or median latency from invented history. Manual throughput tests and live transfer rate are distinct features with separate budgets.

**Closure:** three variants on one endpoint eventually all receive an opportunity; a stuck request is terminated; cancellation leaves untested work pending; an uplink/target outage does not condemn unrelated nodes. With working production node A and broken candidate B, B must fail even while TUN A is active. Repeat the inverse and concurrent-worker cases. This path-isolation proof is mandatory.

### F16. P1: parsing failure can be committed as a valid empty source snapshot

**Evidence: STATIC.** `Refresh/RefreshMerge.cs`: `Ingest`; `Import/SubscriptionImporter.cs`; `Application/Catalogue.cs`: `ApplySnapshot`.

`Balanced` verifies record-count arithmetic, not document validity. A malformed document represented as one invalid record is balanced; the refresh code can commit its zero accepted nodes as `Complete=true`. Previous artifact membership is then removed or made historical. The old node object may remain, so this is not a claim that every credential is physically deleted. Nevertheless it corrupts current-source state and future retention decisions.

**Required fix:** introduce explicit document validity/completeness and snapshot outcome. Distinguish valid empty subscriptions from fetch failure, HTML, unrecognized schema, parse error and truncation. Preserve the last successful artifact membership on invalid/incomplete snapshots. Persist content identity and fetch metadata atomically. A 304 without a usable local representation must trigger a bounded unconditional refetch.

**Closure:** seed a successful artifact, then feed malformed JSON/YAML, HTML, truncated data, 304-without-cache and genuine empty content. Only the last recognized successful empty snapshot may remove current membership. Other source families remain independent.

### F17. P1: malformed records can escape parser containment or masquerade as valid empty data

**Evidence: STATIC.** `Import/ShareLinkParser.cs`, `SubscriptionImporter.cs`, `XrayOutboundParser.cs`, `Fetch/GithubTreeParser.cs`.

Malformed JSON inside a Base64 VMess URI can raise `JsonException` outside the URI parser's catch boundary. Wrong JSON shapes such as an object in `outbounds`, nonnumeric fields passed to numeric accessors, or malformed nested records can raise exceptions not handled by the batch wrapper. Conversely an unrelated object can be interpreted as an empty subscription. Tree-parser field types also need validation.

**Required fix:** validate schema kinds before enumeration/access, contain recoverable per-record failures and classify whole-document failures explicitly. Maintain size/depth/record limits before expensive work. Strictly validate decoding and Unicode assumptions; do not normalize malformed credential bytes silently. Catch expected parse failures with precise reason codes rather than hiding all internal errors behind a blanket success-shaped empty list.

**Closure:** a malformed record beside a valid record does not terminate the entire batch. Wrong top-level and nested types, invalid VMess JSON, duplicate/conflicting keys, malformed Unicode and over-limit inputs produce bounded deterministic failures, never an authoritative empty snapshot.

### F18. P1: emitted protocol fields and imported TLS defaults are not protocol-specific

**Evidence: STATIC + pinned upstream schema.** `Core/MihomoProfileGenerator.cs`: `AppendProxy`; `Import/ClashProxyParser.cs`; references R3-R6.

The generator uses `servername` for every protocol even though, for example, the pinned Trojan schema uses `sni`. VMess cipher selection is dropped because `cipher` is emitted only for Shadowsocks. A normal Clash Trojan definition without an extra `tls: true` is classified as plaintext although Trojan's schema already implies its TLS behavior. These are not fixed by parsing one synthetic URI per protocol or accepting one VLESS profile with `-t`.

**Required fix:** explicit protocol-specific import defaults and emitter schemas. Preserve the requested SNI, cipher, authentication, transport and verification semantics. Distinguish actual protocol encryption properties from a missing unrelated field. Reject unsupported combinations rather than silently defaulting to a different connection.

**Closure:** golden import-to-canonical-to-native profiles for each supported protocol/format, native validation of every emitted protocol and controlled handshakes where SNI/cipher changes demonstrably affect the result. Include Trojan without a `tls` Boolean and nondefault SNI cases.

### F19. P1: unknown security values can bypass the plaintext policy

**Evidence: STATIC.** `NodeSemantics.Classify`, `IsPlaintext`, `CanonicalIdentity.NormalizeSecurity`, `ShareLinkParser`, `MihomoProfileGenerator.AppendProxy`.

For VLESS, `IsPlaintext` rejects null/empty/`none` but does not whitelist recognized secure values. An unknown nonempty `security` value can pass classification. The generator enables TLS only for `tls`/`reality`, so that same accepted record can be emitted without TLS.

Synthetic regression input, for parsing only:

```text
vless://11111111-1111-4111-8111-111111111111@203.0.113.10:443?encryption=none&security=bogus&type=tcp&sni=www.example.com
```

**Required fix:** closed protocol/security/transport enumerations, validated combinations and a shared fail-closed boundary at import, runtime-set acceptance and generation. Native validation is additional proof, not a replacement for security policy. Do not fix this by enabling plaintext or ignoring certificate errors.

**Closure:** unknown values are invalid/unsupported before any probe or core launch. Generated profiles for accepted protected records cannot omit required security. Add mutation/property tests around case, empty and unknown values.

### F20. P1: accepted transport and wrapper semantics are silently lost

**Evidence: STATIC.** `Import/ShareLinkParser.cs`, `ClashProxyParser.cs`, `XrayOutboundParser.cs`, `Domain/NodeSemantics.cs`, `Broker/NodeWireFactory.cs`, `Contracts/Ipc.cs`, generator.

`PacketEncoding`, `Up` and `Down` do not survive the full runtime wire path. Accepted Hysteria port options and header/transport fields are incomplete. Paths are emitted as WebSocket options even when the transport requires different handling. SS `obfs-local` renaming does not fully translate its option names to the core's schema. Nested option validation is weaker than top-level validation. Xray parsing selects only the first `vnext`/user and does not implement all claimed server-array and transport structures.

**Required fix:** an explicit compatibility matrix and typed mapping for every supported semantic field. Expand actual wrapper records without pretending unrelated client routing policy is a node field. Preserve supported nested options exactly; classify unimplemented options with actionable unsupported reasons. Do not silently drop security/connection fields just to improve the accepted count. Shared raw/YAML/Base64/Xray representations must be compared against their full node semantics.

**Closure:** round-trip and native tests cover plugin option translation/types, WS/gRPC/other admitted transport options, Hysteria port/bandwidth options, packet encoding and multi-record wrappers. Negative fixtures prove unsupported nested fields never become a different apparently valid node.

### F21. P2: normalization changes opaque connection values and needs a migration strategy

**Evidence: STATIC.** `Domain/NodeSemantics.cs`: `CanonicalIdentity.Normalize` and `EmptyToNull`.

Trimming is applied to values such as path, service name, plugin options and obfuscation password. Some of these are opaque or whitespace-sensitive. Identity normalization must not change the bytes that the server expects. Fixes to these rules also change existing digests and cannot safely inherit previous health evidence blindly.

**Required fix:** separate normalization rules for hostnames/enumerations from opaque credentials and protocol fields. Define canonical representation of typed plugin options without altering meaningful values. Bump/version the canonicalizer where semantics change and implement migration/reconciliation of user metadata and source provenance. Invalidate assessments that no longer refer to identical effective configuration.

**Closure:** leading/trailing significant path/password bytes survive import and generation; distinct effective configurations remain distinct. Migration preserves favorites and owner choices where identity can be proven, never invents a successful assessment for changed semantics.

### F22. P1: HTTP timeout and redirect policy are not enforced by the transport

**Evidence: STATIC + official .NET documentation.** `Fetch/PolicyHttpFetcher.cs`: `GetAsync`; references R7-R8.

With `ResponseHeadersRead`, `HttpClient.Timeout` does not bound subsequent body consumption. The code reads that body with only the caller token. A server can return headers and then stall indefinitely. An injected handler with automatic redirects enabled can bypass the class's manual redirect loop. Host-only approval also leaves origin/path/port policy, effective destination validation and ETag scoping underspecified.

**Required fix:** own/configure the handler explicitly, disable implicit redirects, choose direct/proxy routing intentionally and avoid ambient proxy surprises. Apply one bounded deadline across the complete attempt, including body reads, and an explicit total retry budget. Validate request/effective redirect origins and approved repository paths, scope conditional validators correctly, bound decoded response sizes and handle per-source failures without destroying the current catalogue. Respect rate-limit/backoff responses.

**Closure:** a real controlled HTTP handler/server test holds the body after headers and is cancelled on time. Redirect tests use the production handler configuration, not only a fake that never auto-redirects. Out-of-registry destinations cannot be reached behind an apparently approved starting URL.

### F23. P1: failed SQLite writes leave the in-memory authority changed

**Evidence: STATIC.** `Persistence/SqliteCatalogue.cs`: mutating methods and `Save`; `Application/Catalogue.cs`.

The memory catalogue is mutated before the database transaction commits. If persistence fails, SQLite rollback does not roll back the memory mutation. A failed settings/snapshot/favorite operation can therefore still affect running decisions and then disappear on restart. Mutable catalogue objects and unlocked read paths also need a defined concurrency model when the real coordinator is connected.

**Required fix:** one authoritative transactional mutation boundary: validate/stage changes, commit durable state, then publish an immutable revision, or provide equivalent reliable rollback. Define serialization for readers/writers and avoid exposing mutable collections across asynchronous operations. Validate persisted digests/settings against their schema and reconcile transient active-session state with actual runtime state at startup.

**Closure:** inject commit failures into settings, snapshots and assessments. A reported failed operation cannot be visible as committed in memory or after restart. Concurrent refresh/probe/UI reads produce consistent revisions without collection mutation exceptions.

### F24. P2: full-database rewrites and incomplete retention do not scale to the stated catalogue

**Evidence: STATIC.** `SqliteCatalogue.Save`, `MemoryCatalogue.ApplySnapshot`, `EvictOverflow`, `Domain/Scheduling.cs` retention policy.

Each assessment write deletes and rebuilds the complete catalogue, including re-protecting every node secret. Testing many nodes produces approximately quadratic catalogue work instead of targeted updates. Several lookups are also linear. Retention helpers and limits are not a complete enforced admission/expiry policy; overflow containing pending/current nodes is not handled by evicting only failed missing nodes.

**Required fix:** targeted transactional inserts/updates, batched assessment persistence, indexed lookups and bounded histories. Make retention/expiry/admission policies executable, including last-success age, missing-source nodes and protected favorites/current sessions. Never delete owner favorites merely to hit a numeric cap; expose intentional overflow or stop admitting low-priority work under a documented policy.

**Closure:** deterministic load tests with the specified catalogue size measure SQL writes, protector calls, memory and responsiveness. One assessment must not rewrite/re-encrypt every credential. Restart, expiry and overflow tests preserve required user data.

### F25. P2: source discovery, completeness and snapshot identity need durable runtime implementation

**Evidence: STATIC + GAP.** `Fetch/GithubTreeParser.cs`, `Domain/Coverage.cs`, `Inventory/Program.cs`, `config/source-manifest.json`, `mirrors.json`.

Fixed seed-family matching and path-count reports are not complete dynamic discovery. Unknown families can remain unmatched. Tree-response identity, requested commit and individual blob/content identity need distinct fields rather than a generic `CommitSha`. Blob modes, safe relative paths and complete discovery outcomes must be validated. A successful fetch of a mirror's LICENSE file is not proof of every subscription representation or future availability.

**Required fix:** bootstrap from the reviewed registry, discover all relevant subscription families/representations and persist provenance, ETags and last-good snapshots. Keep commit/tree/blob/content identities distinct. Treat missing/truncated/rate-limited discovery as incomplete, not permission to prune. Ignore executable/symlink/submodule/document/image/Tor-only resources as defined in the original scope; account for them in coverage rather than silently claiming every resource is a VPN node. Support reviewed mirror URL construction and bounded recovery from unavailable GitHub.

**Closure:** changed upstream names, a new family, removed representations, overlapping formats, incomplete trees and mirror disagreement produce honest coverage with no lost healthy fallback set. Refresh must not stay frozen to the initial inventory commit.

### F26. P2: country constraints depend on inconsistent labels and discard useful conflict evidence

**Evidence: STATIC.** `Domain/TextPolicy.cs`: `CountryLabels`; importer/canonical catalogue mapping; `Domain/Failover.cs`; settings and broker selection.

Country extraction produces a code/name pair but the catalogue commonly stores the localized name. Strict comparisons against free-form settings can mismatch `DE` and `Германия`. Conflicting labels and differing representations need persistent provenance instead of latest-label overwrites. Advertised country is not a verified exit-location measurement.

**Required fix:** canonical country codes, separately localized display names and explicit unknown/conflict states. Apply strict/preferred country policies consistently to first connect, manual selection, standby construction and failover. Preserve source-claimed geography separately from any opt-in measured exit metadata. Unknown/conflicted values must not satisfy a strict filter silently.

**Closure:** Russian/English/code/flag forms of the same country behave identically; contradictory labels remain visible; strict-country failover never escapes its constraint, and preferred mode actually implements its documented preference.

### F27. P2: runtime standbys and ranking are not the bounded measured policy described by the UI

**Evidence: STATIC.** `Broker/BrokerEngine.cs`: runtime-set construction and selection; `Domain/Failover.cs`: `PickDiverse`; `Domain/Ranking.cs`; settings.

A separate helper for diverse standby selection does not make the broker's accepted set bounded/diverse. Runtime-set requests need deduplication and the specified small cap. Ranking must use real sample history; constant sample counts, absent speed history and an ignored selected ranking mode do not implement the advertised preferences. A fresh-looking standby must still be eligible under current exclusions/source/country/security policy at actual use time.

**Required fix:** bounded, deduplicated runtime records validated by the broker, endpoint diversity, a stable selected session and measured ranking inputs. Apply current policy and freshness at selection, not just when staging the list. Treat unknown throughput honestly and avoid unnecessary optimization switching.

**Closure:** oversized/duplicate/active-node standby submissions are handled deterministically; exclusions changed after staging take effect; fresh measurements can change ranking under the chosen mode, while a slightly lower ping does not disrupt a healthy pinned/dwell-protected session.

### F28. P2: scheduling bounds and monotonic timing are not consistently enforced

**Evidence: STATIC.** `Domain/Scheduling.cs`, `Settings.cs`, `Clock.cs`, broker cooldown code.

The jitter calculation can fall below the documented minimum interval and has signed-seed edge cases. Large/unknown setting values are not comprehensively bounded. A monotonic clock abstraction exists, but stateful timing is not consistently based on injected monotonic time. A wall-clock change must not reset switch budgets or make stale observations look newly valid.

**Required fix:** validate enum/schema values and bounded numeric settings; normalize random seeds safely; clamp the final interval, not only its pre-jitter input. Use monotonic time for in-process budgets/timeouts/cooldowns and conservative UTC timestamps for persistence. Connect physical-network/sleep/resume detection while excluding self-generated TUN changes from spurious epoch storms.

**Closure:** minimum intervals, negative/maximum seeds, integer limits, unknown enums, clock rollback/forward, sleep/resume and self-owned adapter events have deterministic tested outcomes. Missed schedules coalesce instead of replaying a burst.

### F29. P1: native validation, temporary profiles and diagnostic redaction need hard boundaries

**Evidence: STATIC.** `Core/MihomoProcessController.cs`, `Domain/TextPolicy.cs`: `SecretRedactor`; future real core supervisor.

The validator hashes a path and later executes it without a complete protected-path/ownership contract. Temporary profiles contain credentials. Output collection is not a complete bounded logging design, and kill/delete/cancellation cleanup needs confirmation of child exit. The generic redactor does not cover all controller-secret, Base64 URI, quoted JSON and whitespace-containing secret forms. A regex over a few field names cannot prove diagnostics are safe.

**Required fix:** protected official asset staging and verified execution paths, no writable DLL/executable search locations, restricted temporary storage and an owned reaper. Bound stdout/stderr before retention and redact structurally using the known secret set before logging/export. Distinguish cancellation from timeout and retain the original failure if cleanup also fails. Never expose full generated YAML or live subscriptions in diagnostics or commits.

**Closure:** secret canaries in raw, encoded, quoted and space-containing forms do not appear in logs, exports or retained temporary files. Cancelled/hung validators and cores are killed and awaited without taking down a newer owned instance. Tampered assets and writable-path substitution are rejected.

### F30. P0: there is no installable and independently retrievable v1 release

**Evidence: STATIC + GAP.** `scripts/package.ps1`, `verify-release.ps1`, service project, `docs/evidence/build-manifest.json`, release-list API.

Packaging currently publishes four folders, explicitly without an installer, Mihomo, Wintun or service registration. The reported local `.tar.xz` is not a GitHub release artifact. The verification script is a literal `NOT_RUN`/exit-2 placeholder. These are honest statements, but they do not satisfy install/connect/disconnect/uninstall.

**Required fix:** create the specified Windows package/installer with explicit protected locations, prerequisites, reviewed core/driver assets, service identity/ACLs, startup integration and safe upgrades/uninstall. Publish retrievable artifacts and hashes from a clean build. Handle unavailable signing credentials honestly; do not fabricate Authenticode or defeat warning/security controls. Do not turn a placeholder verifier green without implementing verification.

**Closure:** install on a clean supported Windows machine, launch unelevated, connect, update, restart, recover, upgrade and uninstall while preserving unrelated network state and the owner's chosen data policy. Another machine can retrieve and verify the same artifact.

### F31. P1: green tests currently overstate important boundaries and miss the target OS

**Evidence: STATIC + CI.** `tests/AutoVpn.UnitTests/BehaviorTests.cs`, `ImportTests.cs`, `.github/workflows/ci.yml`, `scripts/test.ps1`, `test.sh`, `test-windows-admin.ps1`.

Two tests unconditionally expect `NotWindows`; their expected results are wrong on Windows. The optional Mihomo fact silently returns when its environment variable is absent, so an unexecuted check counts as passed. Scripts clear that variable. The test named for a second pipe owner uses same-owner clients. The Disconnect race test calls the engine directly, not through the blocking server. The switch-budget test encodes F08's false-connected result. Existing fixtures seed consent and health, hiding first-run gaps. Admin tests are not executable tests yet.

**Required fix:** first add regressions for this audit and correct false assertions. Separate unit, native-configuration, controlled integration and Windows admin/UI suites with explicit prerequisites and honest skipped/NOT_RUN accounting. Add actual Windows build/unit/non-destructive integration CI, plus an authorized isolated admin acceptance runner. Collect structured results and artifacts. An optional check not run must not be presented as a successful native validation.

**Closure:** tests fail against the relevant audited defects, pass after their fixes and exercise production composition/boundaries. Report exact commands, environment, outcomes and skips rather than a larger undifferentiated pass count.

### F32. P2: the pinned Mihomo license is incorrectly recorded, and supply-chain evidence is incomplete

**Evidence: STATIC + pinned upstream file.** `config/core-manifest.json`, `THIRD_PARTY_NOTICES.md`; upstream `MetaCubeX/mihomo` at `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`.

The repository labels this Mihomo component MIT. The actual LICENSE fetched at that exact upstream commit contains GNU GPL version 3. Correct this mismatch before distribution. Determine precise SPDX qualification from the upstream licensing notices; do not infer an `-only`/`-or-later` suffix solely from the generic license text. This audit did not establish that all dependencies are vulnerability-free or that the recorded downloaded bytes were independently verified.

**Required fix:** reconcile component licenses/notices and source-distribution records for what is actually shipped, including Wintun's actual distribution terms. Generate a transitive SBOM and reproducible dependency records, review official release provenance and pin build inputs appropriately. Verify staged assets at build/install/run boundaries. A locally recorded hash is an identity record, not an upstream signature.

**Closure:** generated package contents, SBOM, notices and core manifests agree with the actual pinned files. No assertion of signatures/attestations exists without the corresponding verified evidence.

### F33. P2: source-to-artifact provenance and completion reports must be mechanically consistent

**Evidence: STATIC + GAP.** `docs/evidence/build-manifest.json`, `HANDOFF.md`, implementation status, compatibility/coverage/security documents.

The manifest identifies the audit commit while explaining that the archive was packed before that commit object existed. That alone does not prove different binary inputs, but it leaves source/artifact binding unproven without a clean input tree record. Documentation also describes capabilities at different levels: helper implementation, wired runtime and tested Windows behavior are not interchangeable.

**Required fix:** build from an explicit clean source commit/tree and generate the manifest from that invocation. Record dependency/core/driver identities, artifact digest/size and structured test results. Treat later documentation-only amendments separately instead of pretending an artifact contains its own later commit. Update compatibility and source coverage to describe actual mappings and measurements, not intended ones.

**Closure:** a reviewer can trace the retrievable artifact to exact build inputs and acceptance evidence. `IMPLEMENTED`, `WIRED`, `TESTED`, `NOT_RUN`, `BLOCKED` and `READY` have distinct meanings and cannot be substituted for one another.

### F34. P2: the promised simple polished Windows experience is not finished

**Evidence: STATIC + UI execution GAP.** `Desktop/App.xaml`, `MainWindow.xaml`, `MainWindow.xaml.cs`, application settings.

The current fixed light resources and static screens do not implement system/light/dark themes, usable measured-node views, consistent loading/empty/error states or the full tray and accessibility behavior. Compiling WPF and setting a DPI property do not prove the interface works at different scaling levels.

**Required fix:** finish the original four-view simple design with real commands/data, clear active-versus-healthy-versus-disabled states, timestamps and reasons, keyboard navigation, sensible focus, accessible labels and readable high-contrast/theme behavior. Keep advanced controls out of the default flow. Present latency, benchmark speed and current traffic separately; unknown values remain unknown. Do not add a dashboard full of nonfunctional buttons.

**Closure:** actual Windows screenshots and an exercised click/keyboard matrix at representative DPI scales and themes, including no eligible servers, ongoing refresh, source failure, connection in progress, protected outage and recovery. No visual-only mock can close a functional requirement.

## 4. Mandatory regression pack

Create real tests from these scenarios. They are proposed audit regressions, **not tests claimed to have been run by this auditor**. Use controlled clocks, resolvers, local test servers and synthetic credentials. Deliberately test the production transport/composition, not only isolated policies.

| ID | Required negative or end-to-end control | Principal findings |
|---|---|---|
| AT01 | Fresh install, no seeded settings/database, consent, discover, verify, list, connect | F01-F03 |
| AT02 | Idle/partial-frame client and held-open response cannot starve control | F04 |
| AT03 | Slow Start through a real named pipe followed by real-pipe Disconnect | F04, F07 |
| AT04 | Two Windows users, actual service identity, remote denial and spoofed server | F05 |
| AT05 | Duplicate/stale mutation after disconnect, lease renewal and service restart | F06 |
| AT06 | Delayed production success/core exit/cleanup from A after attempt B begins | F07 |
| AT07 | CoreExit at switch cap becomes protected non-connected state | F08 |
| AT08 | Failure at every arm/start/verify/stop/restore stage preserves the policy | F09-F10 |
| AT09 | Corrupt journal, recovery, process restart, second recovery; no false clean state | F11 |
| AT10 | Missing journal with owned effects; WAL and unrelated changes survive recovery | F10-F11 |
| AT11 | Packet-observed DNS/IPv6/bootstrap routing before/during/after tunnel failure | F12 |
| AT12 | Mixed/private DNS answers and rebinding blocked by the actual dial policy | F13 |
| AT13 | Expired healthy and new-epoch healthy nodes are rechecked | F14 |
| AT14 | Old-epoch success is discarded, including when epoch changes during await | F14 |
| AT15 | Third same-endpoint variant eventually runs after two repeatedly failing variants | F15 |
| AT16 | Hung probes cancel; daily bytes and endpoint concurrency are truly bounded | F15 |
| AT17 | Production A works, candidate B broken: B cannot pass through A; inverse/concurrent cases | F15 |
| AT18 | Malformed/HTML/truncated source preserves last-good membership; recognized empty does not | F16 |
| AT19 | Invalid Base64-VMess JSON and wrong nested JSON types cannot abort a whole refresh | F17 |
| AT20 | Protocol-specific SNI/cipher/TLS defaults and every admitted transport round-trip | F18, F20 |
| AT21 | Unknown `security=bogus` is rejected before native launch | F19 |
| AT22 | Opaque bytes and canonicalizer migration do not inherit wrong health evidence | F21 |
| AT23 | Headers-then-stalled-body and redirect bypass tests use production HTTP settings | F22 |
| AT24 | SQLite commit-failure injection leaves durable and published state consistent | F23 |
| AT25 | One assessment is not a full catalogue rewrite; retention and fair overflow verified | F24 |
| AT26 | New/removed family, incomplete discovery, 304 without cache and mirror disagreement | F25 |
| AT27 | Country conflicts, strict/prefer policy, exclusion change and bounded diverse standbys | F26-F27 |
| AT28 | Clock changes, jitter limits, sleep/NIC events and no self-TUN epoch storm | F28 |
| AT29 | Encoded/quoted/spaced secret canaries and cancelled child-process cleanup | F29 |
| AT30 | Clean install, upgrade, explicit Exit, UI crash, broker/core crash, recovery, uninstall | F03, F10, F30 |
| AT31 | Real Windows test expectations; unavailable native/admin tests are not green no-ops | F31 |
| AT32 | Package provenance, component licenses, transitive SBOM and exact artifact retrieval | F32-F33 |
| AT33 | Real Russian UI, themes, DPI, keyboard, tray and all empty/error/protected states | F34 |

Tests for public subscription availability are inherently environment/time-specific. Keep the reproducible controlled suite separate. Live smoke tests must report the network, time, candidate/context and actual result without publishing credentials or claiming global availability.

## 5. Implementation order and deliverables

### Work package A: establish a failing baseline and correct pure logic

Preserve the audited snapshot as the comparison baseline. Add the relevant adverse tests before changing behavior. Fix malformed-input containment, unknown security, protocol mapping, opaque identity, invalid-snapshot semantics, probe freshness/context/fairness, SQL transactional consistency and false-connected failover logic. These tasks do not require a Windows admin machine. Do not stop at documenting the lack of Windows.

Deliver coherent tested commits, the protocol compatibility matrix and the first audit closure entries. Keep original good behavior covered. Do not spend this phase polishing empty screens or rewriting the stack.

### Work package B: finish the unelevated application workflow

Implement catalogue persistence, reviewed source/target registries, discovery/fetch/parse/reconcile/probe coordination, actual settings, first-run consent, bounded speed tests, live UI state and cancellation. Provide controlled real-core non-TUN integration where the environment supports it. Keep source parsing and broad network fetching out of the privileged broker. Demonstrate that fresh state reaches real measured eligibility without test seeding.

### Work package C: establish the real control and ownership boundary

Implement bounded responsive IPC, actual Windows identity/ACLs, strict operation context, idempotency, the serialized state actor, core handle supervision and protected effect ownership. Integrate actual service lifetime and broker restart reconciliation. Keep long I/O out of the command authority critical section. Exercise races through real transport.

### Work package D: complete Windows TUN/protection and recovery

Connect the real core/guard only after their safety contracts exist. Execute the original Windows test plan plus the adverse controls above on an authorized isolated environment. Verify DNS/IPv6 behavior, candidate-path isolation, active health, standby use, sleep/network changes, crash protection and owned-state restoration with actual OS/packet observations. Refusal on unsupported hosts remains correct; permanent refusal on every Windows host is not an implementation of v1.

### Work package E: package and accept the product, not the unit-test count

Produce the installer/artifact, accurate license/SBOM/provenance records, operating/recovery instructions and a complete UI matrix. Re-run from a clean supported Windows installation. Tie results to the exact source and artifact identities. Publish the deliverable where the owner can retrieve it.

Do not run these packages as an unbounded autonomous loop. Work in finite slices, close child processes/tasks, record recovery context and continue only when the previous slice has a concrete result. If a genuine external blocker remains, finish independent work and publish a recoverable checkpoint with the exact missing prerequisite. `BLOCKED` is not `DONE`.

## 6. Closure reporting contract

Create `docs/AUDIT_FIX_STATUS.md` with one row for every F01-F34 and a linked AT-test set. Each row must contain:

```text
Finding | Status | Fix commit | Production paths changed | Regression test IDs
Executed environment | Evidence path/run | Remaining limitation
```

Use only meaningful states: `OPEN`, `IN_PROGRESS`, `IMPLEMENTED_NOT_VALIDATED`, `BLOCKED`, `VERIFIED`. A source change alone cannot mark a Windows behavior verified. Preserve this audit file as the review baseline; put closure evidence in the separate status/report instead of editing findings away.

If a finding appears inapplicable on a newer branch, demonstrate the exact code and adverse test result. A justified correction is welcome; silent omission is not. Track pre-existing blockers separately from newly discovered regressions, but neither category may vanish from acceptance.

The final handoff must include the pushed commit, clean build input tree, artifact location/size/SHA-256, exact platform/core/driver versions, executed commands and structured results, explicitly unavailable tests, recovery instructions and remaining risks. Native `-t`, a controlled SOCKS connection and actual Windows TUN/protection are three different evidence levels.

A release-ready verdict requires the original mandatory acceptance gates and the applicable adverse tests here to pass. Do not force-push, overwrite unrelated owner work, commit live credentials/runtime profiles/databases, silently loosen certificate checking, fake a country/ping/speed or claim that mock objects proved packet protection.

Start with repository/environment inspection, confirm the current branch against the audited commit, and implement Work package A. Report meaningful progress and real blockers to the owner in Russian. The goal remains a usable simple Windows VPN client, not a permanently safe but nonfunctional scaffolding demonstration.

## 7. Primary references and audit provenance

These references support specific API/schema statements. Product findings otherwise derive from the pinned application source paths listed above.

- **R1: audited source and CI.** https://github.com/alinescafs3mp-afk/vpn/tree/0b79fb9c135ffb5a510b41319fc3c53cea2d3ac6 ; https://github.com/alinescafs3mp-afk/vpn/actions/runs/37058056396
- **R2: Mihomo DNS configuration**, including internal resolver routing and proxy-server resolution: https://wiki.metacubex.one/en/config/dns/
- **R3: pinned Trojan implementation**, including the `sni` field: https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/adapter/outbound/trojan.go ; https://wiki.metacubex.one/en/config/proxies/trojan/
- **R4: VMess schema:** https://wiki.metacubex.one/en/config/proxies/vmess/
- **R5: Hysteria2 and TUIC schemas:** https://wiki.metacubex.one/en/config/proxies/hysteria2/ ; https://wiki.metacubex.one/en/config/proxies/tuic/
- **R6: Shadowsocks/plugin schema:** https://wiki.metacubex.one/en/config/proxies/ss/
- **R7: official .NET `HttpCompletionOption` documentation**, specifically timeout behavior with `ResponseHeadersRead`: https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0
- **R8: official .NET redirect behavior:** https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.allowautoredirect?view=net-10.0
- **R9: actual pinned Mihomo license:** https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/LICENSE

Live documentation can change. Validate implementation details against the exact pinned core/SDK and preserve the corresponding source evidence in the fix report. No reference to an upstream document substitutes for execution of the product's acceptance tests.

**End of directive.**
