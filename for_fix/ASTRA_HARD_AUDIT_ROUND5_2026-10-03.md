# AutoVPN: independent hard audit and completion directive, round 5

**Recipient:** Grok, implementation lead.  
**Owner communication:** Russian only. Engineering identifiers and this directive are English.  
**Repository:** `alinescafs3mp-afk/vpn`  
**Audit date:** 2026-10-03; execution timestamps are UTC.  
**Audited production commit:** `49e5bd54e41731b9b96b83789def31e86c701ee6`  
**Audited production tree:** `19276e45395d690414197a28355a2f0e8f8f7449`  
**Independent branch:** `audit/round5-49e5bd5-independent`  
**Canonical regression commit:** `ff87b7dcd36c86d441edf4bcc21813a81ad54647`  
**Additional Windows UI/workload commit:** `06f612780c146aedee9d5e8bd8f45ae9c5e93351`  
**Verdict:** **V1 NOT ACCEPTED. The previous counterexamples have substantially improved, and real native tests now pass on Windows, but the installed VPN journey is still missing and new independent tests expose serious integration defects.**

This is a remediation and implementation assignment, not another status-only audit. Read this document, `ROUND5_EVIDENCE_2026-10-03.json`, all previous audit directives, and `docs/IMPLEMENTATION_DIRECTIVE.md`. The original product requirements remain binding. The new tests are a regression floor, not a smaller substitute specification.

Preserve owner data and useful existing work. Keep the agreed C#/.NET/WPF/Mihomo baseline, ordinary UI use unelevated, and untrusted subscription fetching/parsing outside the privileged broker. Do not force-push, blindly merge the audit branch, disable adverse assertions or certificate verification, fabricate measurements, or trade a working recovery boundary for a green test count. The final result must be a usable simple Windows VPN client, not a permanently refusing demonstration.

## 1. Exact evidence boundary

### 1.1 Source coverage and independent execution

The pinned production snapshot contains **128 tracked files, 1,420,962 bytes, 27,738 text lines, and 65 C# files containing 19,849 lines**. Every file's Git blob hash was recomputed and the root tree was reconstructed from file names, modes and bytes. The hash matched the pinned tree. Review covered changed code and critical unchanged paths across entrypoints, UI, IPC, broker, discovery, HTTP, import, identity, persistence, recovery, probes, native processes, policies, tests, configuration, packaging and status reports. Byte verification and a whole-tree inventory are not proof of exhaustive execution coverage.

All **297 distinct file blobs reachable from 26 production-history commits**, totaling 3,492,147 bytes, were screened for five narrow signature categories: classic/fine-grained GitHub tokens, AWS access-key IDs, PEM private-key headers and OpenAI-key-like strings. No matches were found. This is not entropy-based/general credential detection or inspection of unreachable remote objects; it cannot establish that every possible secret is absent.

The final audit branch adds **10 files**: four workflows, two regression-test files and two small harness projects. All existing production files were byte-compared and are unchanged. The main-branch delivery is this directive plus its evidence JSON only. Port regression expectations deliberately; do not merge the branch wholesale. Some audit-only native facts intentionally require separately verified binaries, so ordinary CI that executes them without their environment is not the canonical baseline.

Baseline run **37133249313**, regression/native run **37133873472**, and final UI/workload run **37134634399** were initiated for this audit. These are not merely a reading of Grok's earlier pass count. Release builds passed on Linux and Windows. SDK **10.0.112**, runtime **10.0.12**. Windows was **Windows Server 2025 Datacenter 10.0.26100**, not a Windows 11 acceptance machine.

| Independently executed suite | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| Existing suite, Linux without optional native core | 158 | 1 | 3 | 162 |
| Existing suite, Windows without optional native core | 144 | 15 | 3 | 162 |
| Existing suite, Linux with verified pinned Mihomo | 162 | 0 | 0 | 162 |
| New round-5 adverse tests and controls, Linux | 5 | 29 | 0 | 34 |
| New round-5 adverse tests and controls, Windows | 5 | 29 | 0 | 34 |
| New native profile/connection suite, Linux | 10 | 0 | 0 | 10 |
| New native profile/connection suite, Windows | 10 | 0 | 0 | 10 |

The 29 new failing cases repeat on both platforms. They are **29 distinct failed expectations, not 58 separate defects and not a vulnerability count**. In-process guarded-state and synthetic transport tests prove those contracts; they are not evidence of packet leakage from an installed Windows service.

All **33 imported round-4 cases passed** in the Linux-native and Windows-baseline runs. In the no-core Linux run, one Q24 chunked-response case ended in `OperationCanceledException`; that case passed in the native run. Preserve this timing/cancellation evidence without claiming a proven semantic regression or that all runs were identical. The existing Windows 15 failures include **five IPC cases, nine file-lifetime/cleanup cases and one Linux-only `/usr/bin/pkill` fixture**. Do not label all 15 production vulnerabilities or restore unsafe behavior to satisfy obsolete fixtures.

### 1.2 Real Windows and Linux native proof

Both platform jobs downloaded the exact official **Mihomo v1.19.32**, verified archive and executable SHA-256 against the pinned manifest, and then executed it. Source pin: `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`. All identities are recorded in the accompanying JSON and raw artifacts.

The nine accepted `mihomo -t` cases were VLESS, VMess with omitted canonical AlterId, Trojan, Shadowsocks, Hysteria2, TUIC, and VLESS WebSocket/gRPC/H2. The prior omitted-AlterId native rejection is fixed for the tested profile. Configuration acceptance is not proof that every option is honored or every protocol handshake succeeds.

A separate test used the actual production `NonTunCoreProbeTransport`, an owned official Mihomo process, a controlled Shadowsocks AES-256-GCM peer and an authenticated local HTTPS target. Correct credentials reached both the proxy handshake and TLS target. Wrong credentials failed and did not increase the target's accept count. This passed on **both Windows and Linux**. Temporary certificate trust was fixture-local; the global trust store was not relaxed. These are non-TUN loopback tests, not public-node availability or active-TUN path-isolation evidence.

### 1.3 Actual WPF and persistence diagnostic

The final harness let the real WPF StartupUri create the single initial window, exercised four navigation handlers at **880x640 and 1120x760, 96 DPI**, and produced **eight actual rendered images**. The images were downloaded and visually inspected. Protection and LAN settings were toggled, then the window/catalogue was closed and reopened within the same process. The profile remained unconsented, so no public-source download, node probe, TUN or service installation was triggered.

**Normal same-process reopen failed** with `IOException` in `SqliteCatalogue.HasSqliteHeader` while the SQLite file remained in use. Only after a **diagnostic** `SqliteConnection.ClearAllPools()` did reopen work; the changed settings had persisted and were restored. The harness correctly returns a failing result for normal reopen despite the successful diagnostic control. Do not present the pool clear as a production fix, the images as a full functional UI pass, or same-process reopen as a whole-process restart/tray Exit test.

### 1.4 Fresh scale measurements and dependency screen

The current code was measured with 1,000, 5,000 and 10,000 synthetic SQLite nodes on both platforms, three single-assessment updates per size. At 10,000 nodes the three updates took **248.8, 269.4, 347.9 ms on Linux** and **492.2, 438.2, 387.8 ms on Windows**, allocating about **37.68 MB of managed memory per update**. No unrelated secret was re-protected. A test-only counting passthrough protector was used, not DPAPI. These are single-run micro-workloads, not portable timing guarantees, a full probe-throughput benchmark, or a long soak. Do not infer a before/after speedup from different runner samples.

The transitive NuGet advisory query returned **zero vulnerable package entries for nine projects**. This is scoped to that advisory source/time. Native Mihomo/Go/Wintun dependencies, installer contents, signatures and full SBOM/distribution compliance were not validated by that query.

### 1.5 Auditor corrections and unavailable gates

The first source-export job referenced a nonexistent local `main` branch in the audit checkout. It was corrected using an explicit pinned production ref; product builds and test results were not affected. Two intermediate WPF harness errors were also corrected: duplicate explicit/StartupUri window construction, then an invalid attempt to assign `StartupUri=null`. Those are auditor harness defects and are excluded from product findings. The final harness ran through the intended single-window startup and recorded the separate normal-reopen failure described above. The canonical new regression/native suites compiled on the first run.

**Not executed:** installed Windows TUN/WFP/SCM acceptance; production DNS/IPv6/crash-protection packet captures; Windows 11 install/upgrade/uninstall/reboot/sleep and two-account authorization; every supported protocol's actual handshake; simultaneous active-TUN candidate isolation; public subscription availability/speed; sustained catalogue/probe/UI soak; complete native dependency/SBOM/signature compliance. A missing real service or installer is an **implementation gap**, not only an external test gate.

Downloaded archives were CRC-checked and SHA-256-checked against GitHub metadata. Raw TRX, environments, core identities, advisory outputs, workloads, test sources and WPF images are retained in the evidence bundle. The JSON records exact run/job/artifact identities. GitHub artifact retention is finite; preserve the bundle.

## 2. Preserve genuine fixes

Credit the 162/162 native baseline and the 33 previous counterexamples that now pass. Preserve real target TLS authentication; stricter response handling; rejection of missing ordinary proof fields; invalidation after failed admission; retry-after checks; owner policy before ordinary on-demand dialing; persisted queue rotation; successful artifact 304 handling; closed journal-header handles, durable unknown markers and deduplicated recovery IDs; quoted-secret redaction; continuous child-output draining; Windows port ownership; copy-before-commit and stale-writer checks; VMess AlterId generation; selected Xray mapping fixes; and separation of read/safety response caches.

Do not mechanically repeat fixed allegations. In particular, **X02 is a passing control**: the tested missing second Shadowsocks password is not borrowed from the first endpoint. X03 is a loss of an explicitly requested proxy-certificate option resulting in stricter behavior, not an observed verification bypass. The new snapshot/proof/lifecycle counterexamples concern combinations and production integration still left open.

## 3. Mandatory findings and closure criteria

**EXECUTED** identifies the exact tested boundary, **STATIC** means the relevant code/call graph was reviewed without exercising its full deployed consequence, and **GAP** means required functionality or acceptance evidence is missing. P0/P1/P2 are engineering priorities, not CVSS scores. Source references below are relative to the pinned production commit. A source line is an anchor for that snapshot, not a promised line number after your changes.

### R5-01 [P0] Deliver the real installed Windows VPN, not only its safe refusal path

**Evidence:** GAP; production composition inspected.  
**Paths:** `src/AutoVpn.Service/Program.cs`, `Broker/ICoreController.cs`, `UnavailableNetworkGuard.cs`, service project, `scripts/package.ps1`.

The shipping entrypoint still constructs `RefusingCoreController` and `UnavailableNetworkGuard`, opens a console lifetime, and explicitly says this run installs no TUN or network filters. A successful non-TUN candidate test is an important advance but is not this missing user journey. The current status document honestly leaves v1 unaccepted. Missing implementation must not be reclassified as an external Windows-testing blocker.

**Required fix:** Implement the SCM-compatible broker, protected discovery of the reviewed official core/driver assets, operation-owned supervision, actual TUN startup, readiness and production-path verification, and the real protection/recovery backend. Keep the agreed .NET/WPF/Mihomo design; do not restart the project. Unsupported platforms and genuinely failed validation still refuse safely, but supported Windows must have a working production path. Finish this work after the bounded regression-fix slice rather than stopping after another test-only milestone.

**Close when:** A clean supported Windows installation lets the ordinary unelevated UI establish a controlled HTTPS exchange through the intended TUN path, report actual state, disconnect and restore only owned changes. Tampered assets and failed readiness never become Connected. Installed TUN/WFP/SCM acceptance remains NOT_RUN in this audit.

### R5-02 [P1] Complete Windows authorization and the narrow runtime handoff

**Evidence:** STATIC + five existing Windows pipe-test failures; installed two-account boundary NOT_RUN.  
**Paths:** `Broker/PipePeer.cs`, `LocalIpcServer.cs`, desktop/service entrypoints, `Contracts/Ipc.cs`, `BrokerEngine.Select`.

`PipePeer.Inspect` still rejects every non-Linux peer. This is conservative refusal, not Windows identity verification. The desktop's owner catalogue and consent still do not form a complete authorized handoff to a service running under its intended identity. Sending `ConnectPayload.Node` does not by itself admit a trusted runtime record; selection still consults the broker catalogue. Sharing a writable full database or changing its location cannot substitute for that boundary.

**Required fix:** Use restrictive pipe ACLs, OS-derived client SID/session, remote rejection, an explicit authorized owner lease and authenticated server discovery. Keep subscription fetching/parsing and the full user catalogue unelevated. Send only bounded typed settings and a small candidate/runtime set, bound to effective configuration, policy, network and operation context, and independently validate it in the broker. Define DPAPI/store ownership for the actual accounts. Do not turn LocalSystem into a general subscription parser, HTTP proxy, shell or arbitrary-profile launcher.

**Close when:** A fresh owner profile reaches the real service without seeded consent or seeded health. A second user/session, spoofed pipe server, stale lease, incorrect digest/epoch and oversized runtime set are rejected for the correct reasons. Existing Linux same-UID tests do not close the Windows gate.

### R5-03 [P1] Bind discovery to one immutable commit and validate the object shape

**Evidence:** EXECUTED S01, S02; control C01 passes on both platforms.  
**Paths:** `Fetch/GithubTreeParser.cs: TryReadCommitSha`, `Refresh/CatalogueCoordinator.cs:230,306`, `Fetch/ReviewedRegistry.cs`.

The frozen refresh target was improved: the application now resolves `main` through the commit endpoint. It then still fetches the mutable `registry.TreeApi` branch tree instead of the resolved commit's immutable tree. S01 advances the branch between those calls; the coordinator pairs commit C with a different tree D and reports complete discovery. S02 shows that a blob-shaped object containing a 40-character SHA is accepted as a commit object. These are synthetic API-response controls, not an assertion that GitHub normally returns blobs from its commit endpoint.

**Required fix:** Resolve an allowed branch to a validated commit object including its tree identity; fetch that commit/tree immutably and validate the response identity before building content URLs. Keep commit, tree, blob and content digest as distinct fields/types. Preserve the pin only as an explicit bootstrap/last-good fallback and expose degraded/fallback coverage honestly. Bind cached discovery to the same identities; reject contradictory object kinds and partial/truncated trees.

**Close when:** Moving-branch, stable-branch, cached-tree, timeout/fallback, malformed-object and mismatched-identity cases all produce coherent content URLs or an explicit incomplete result. C01 must continue to pass. Add a bounded live metadata smoke; this audit did not probe public subscription nodes.

### R5-04 [P1] A ledger is not a recoverable subscription snapshot

**Evidence:** EXECUTED S03.  
**Paths:** `CatalogueCoordinator.DownloadAsync` around `usableSnapshot` at line 536, `RefreshMerge`, `SourceLedger`, catalogue artifact metadata.

With an empty catalogue and only an old ledger entry containing ETag/hash/time, a 304 response is accepted without recovering the lost nonempty snapshot. S03 observes one request instead of the required unconditional second fetch, and no nodes are restored. The earlier valid-empty-source problem must not be fixed by assuming that every artifact with zero nodes is missing: valid empty and lost data are different states.

**Required fix:** Persist a committed artifact-snapshot record tied to a durable accepted representation, its version/content identity, source membership and validators. This can be a canonical accepted snapshot rather than an unencrypted raw subscription body. Treat the ledger as metadata, not proof that the referenced representation still exists. On a 304 with missing/unusable content, unconditionally refetch within the same bounded attempt policy; propagate a refetch-needed outcome rather than publishing success.

**Close when:** Lost catalogue with surviving ledger refetches and recovers. A genuinely committed valid empty snapshot can reuse 304 without an endless refetch loop. Crash/commit-failure controls cannot advance ETag or last-good identity without the corresponding durable snapshot.

### R5-05 [P1] Continue to a reviewed mirror when the primary body is unusable

**Evidence:** EXECUTED S04.  
**Paths:** `CatalogueCoordinator.DownloadAsync:569`, `RefreshAsync`, `SubscriptionImporter` document outcome.

Mirror selection stops at the first HTTP 200 before parsing establishes a usable subscription. A malformed JSON primary therefore prevents the valid reviewed mirror from being fetched. Preserving the previous catalogue is correct, but it does not implement the promised mirror-assisted refresh recovery.

**Required fix:** Make each source attempt end at validated document/snapshot eligibility, not merely a successful HTTP status. Classify malformed, HTML, unsupported whole-document and truncated responses precisely; preserve last-good state and try the next reviewed representation/mirror under one aggregate budget. Do not broaden the allowed destination registry or accept partial invalid data just to make fallback succeed.

**Close when:** HTTP failure, HTML and malformed 200 responses can fall back to a valid approved mirror; a valid primary does not cause needless duplicate downloads. Mirror disagreement, cancellation and retry exhaustion leave honest provenance and do not remove old healthy fallback nodes.

### R5-06 [P1] Schedule stable artifacts and validate durable queue state

**Evidence:** EXECUTED S05, S06; STATIC lifecycle review.  
**Paths:** `SourceLedger.Load/Save`, `CatalogueCoordinator.RefreshAsync:378`, `DiscoverAsync`, `RefreshScheduler`, desktop refresh timer.

The cursor now lives in the ledger, which fixes the earlier same-instance-only progress test. A persisted `RefreshCursor=-1` still escapes as IndexOutOfRangeException. Discovery 304 reuse does not advance the timestamp that participates in scheduling, so the old entry remains due. The one-minute timer consults all ledger URLs, including immutable historical URLs; absent entries and never-successful families also need distinct scheduling state.

**Required fix:** Validate and bound persisted fields before use, reconcile cursor progress by stable artifact identity when ordering/discovery changes, and handle integer limits. Separate last attempt, last validated check, content change and retry-after. Schedule live sources/artifacts, not every historical URL. A validated discovery 304 and artifact 304 should advance the appropriate check time only when their cache is usable. Bound ledger history, serialize writes, and publish catalogue/validator state coherently.

**Close when:** Negative/huge cursors, changed order, app restart and concurrent manual refresh cannot crash or starve artifacts. Repeated 304s do not create minute-by-minute refresh storms. A failed or never-successful family is neither hidden by a successful tree check nor retried without backoff.

### R5-07 [P1] Health proof needs an attempt lifetime and ordered publication

**Evidence:** EXECUTED P01, P02 (two orderings), P03; C02 passes.  
**Paths:** `Probe/ProbeCoordinator.cs:47,298`, `ProbeObservation`, `AssessmentSnapshot`, catalogue publication methods.

Missing proof fields are now rejected, but nonempty strings alone do not establish ownership or time. P01 reuses the exact old proof after a network epoch change and sees it stamped as new healthy evidence. P02 delays attempt A, completes newer B, then releases A: both stale-success-over-new-failure and stale-failure-over-new-success overwrite the newer assessment. P03 shows that an on-demand observation with `Success=true` and `Class=Unsupported` is accepted. These tests exercise the internal acceptance contract with synthetic transports; they are not claims of remotely forged live core traffic.

**Required fix:** Assign a unique owned attempt before I/O and carry candidate/configuration digest, target contract, epoch, policy revision, worker lifetime and selection purpose through completion. Reject consumed/replayed or superseded attempts. Serialize publication with a per-node generation/sequence and immutable state. Use a closed result model that cannot be success and unsupported/environment/canceled simultaneously. Apply the same boundary to routine and on-demand checks, without permissive defaults for mocks.

**Close when:** Old or duplicate proof cannot become fresh on another network, and an older completion cannot overwrite a newer observation in either direction. Contradictory classifications fail closed. Genuine matching proof and an owned connect/disconnect remain functional; C02 is a required positive control.

### R5-08 [P1] Finish the continuously maintained verified catalogue

**Evidence:** STATIC + GAP; earlier focused scheduler fixes preserved.  
**Paths:** `ProbeCoordinator`, `CatalogueCoordinator.ProbeAsync`, `ProductLimits`, `Scheduling.cs`, desktop timers, ranking/capability storage.

The product still lacks the full independently paced, fair validation lifecycle. Source updates and health freshness have different periods; invoking a short probe pass as a tail of source refresh is not enough to maintain the working list or finish a large backlog. The default path uses the first configured target. A single measurement including process startup is still not a latency median, a speed test or ongoing tunnel health.

**Required fix:** Implement a long-lived unelevated scheduler with bounded resumable queues, per-node work ownership, endpoint concurrency, retry pacing, on-demand priority, old-healthy maintenance and new-candidate admission. Validate the approved independent target contracts and retain bounded measurement history. Use completion timestamps and monotonic durations. Keep candidate-local probing isolated from the active production tunnel, and classify unsupported UDP/capabilities independently. Do not add another permanent agent or move broad parsing into the service.

**Close when:** A healthy catalogue remains maintained between source updates; every admitted source family and eligible candidate eventually progresses. Target/uplink failures do not condemn unrelated nodes. Prove candidate B cannot pass through active tunnel A with positive and negative path controls, then test runtime standby freshness and bounded switching without optimization flapping.

### R5-09 [P1] Carry deliberate manual selection through admission and confirmation

**Evidence:** EXECUTED B01 at ages 0 and 65 seconds.  
**Paths:** `BrokerEngine.SelectReadyAsync`, `AdmissionCandidate`, `SelectionHeld`, `ConfirmProduction:398`, `ProbeCoordinator.AdmitIfStaleAsync/Scheduled`.

The post-Start manual exclusion case was partly repaired. A fresh explicitly selected node excluded only from automatic selection now reaches Start, then confirmation blocks it because `ConfirmProduction` checks exclusion unconditionally. At 65 seconds old, on-demand admission rejects the same manual choice before it can refresh evidence. The layers still disagree about the original allowed manual override.

**Required fix:** Carry explicit selection purpose and owner intent through every admission, Start, confirmation, retry and failover boundary. Treat exclusion from automatic selection differently from disabled sources, forbidden security, invalid configuration, strict country or capability restrictions. Do not use a manual flag to bypass those hard restrictions. Require the original user-facing warning/confirmation wherever specified.

**Close when:** Both fresh and just-stale deliberate manual selections can reach a verified connection under the original policy, while automatic selection never uses an excluded node. Disabling a source, canceling or tightening security during either path still prevents an unauthorized commit.

### R5-10 [P1] Cancellation must settle the state as well as stop the process

**Evidence:** EXECUTED B02, B05; ordinary cancellation fixes not discarded.  
**Paths:** `BrokerEngine.ConnectAsync:137` after Start, `ReportHealthAsync:545` switch completion, request/operation cancellation model.

After a Start returns late under a canceled caller token, Connect returns failure and stops the owned test core but leaves the phase Connecting. A failover Start that returns after cancellation can still commit the replacement node. A precanceled entry check and a scoped Stop are therefore insufficient across the complete asynchronous lifecycle.

**Required fix:** Define one cancellation linearization point for each attempt, check it inside the serialized commit decision, invalidate its proof and transition to the appropriate blocked/restoring/canceled state. Preserve protection obligations and durable cleanup ownership. A client timeout is not automatically owner consent to release protection. Make pending operations queryable/cancelable independently of the transport wait, and bound cleanup with its own deadline rather than the canceled request token.

**Close when:** Cancel before arm, during spawn/readiness, before confirmation and during failover. Late success never resurrects a canceled selection; UI state does not remain indefinitely Connecting after confirmed child shutdown. Real-pipe Disconnect remains available during a blocked operation.

### R5-11 [P1] Record cleanup ownership before partially successful effects

**Evidence:** EXECUTED B03, B04 with strictly scoped owned-core doubles.  
**Paths:** `BrokerEngine.ConnectAsync` catch at line 270, `_operationId` / `_ownedOperationId`, `DisconnectAsync`, core supervisor contract.

A synthetic core acquires an operation-owned resource and then throws IOException. The broker clears the operation without retaining that resource for cleanup; a later Disconnect cannot remove it. A second case rejects a started attempt after policy change, fails its first Stop, and similarly loses the identity needed by the next Disconnect. Ordinary confirmation rejection now preserves an owned handle better, but ownership is still recorded too late for partial Start and failed-stop paths.

**Required fix:** Allocate a durable attempt/cleanup identity before acquiring effects. The supervisor must retain any partially spawned child even when Start throws, and the broker must keep that handle until stop/reconciliation is positively complete. Separate usability/proof state from resource ownership. Treat cleanup failure as retryable uncertain ownership, never as evidence that no process exists. Do not fall back to killing arbitrary processes by name or globally stopping a newer core.

**Close when:** For every partial spawn/readiness/policy-rejection/Stop exception, subsequent authorized cleanup targets exactly the original resource and can be retried. A newer session cannot be stopped by an old completion. The executed failures are lifecycle-contract tests, not evidence of an actual installed tunnel leaking packets.

### R5-12 [P1] Failover must preserve current candidate and session policy

**Evidence:** EXECUTED B06, B07; STATIC review of context rechecks.  
**Paths:** `BrokerEngine.ReportHealthAsync`, `ApplyRuntimeSet`, `CountryAllows`, profile construction and session settings.

After a DE standby is staged, its current advertised metadata can change to FI while effective connection semantics remain identical. Strict-DE failover still trusts the cached standby country and commits it. Separately, an explicit supported `ConnectPayload.LanAccess=false` is lost at failover, whose new profile reads the global true setting and adds DIRECT LAN routes. The latter is an API/session-policy regression; the current UI normally sends matching global and per-connect values, so it is not a measured default-UI leak.

**Required fix:** Freeze effective session policy deliberately or update it through an explicit revisioned operation; do not silently reconstruct it differently on switch. Re-evaluate current source/exclusion/country/capability/freshness after I/O, with completion-time context. Treat advertised country as source metadata, not verified exit geolocation, and keep unknown/conflicting countries out of strict matching. Bind health/core-exit events to the exact session and child they describe.

**Close when:** Changing standby metadata, source enablement, exclusions or network state cannot commit a disallowed target. An explicit LAN/protection policy survives retries/failover until intentionally changed. Strict, preferred and pinned modes obey their documented differences.

### R5-13 [P1] Probabilistic replay detection cannot gate fresh safety commands

**Evidence:** EXECUTED I01, 100,000 bounded synthetic dispatcher calls.  
**Paths:** `Application/IpcDispatcher.cs:100,108,179,195`, replay caches, owner/boot lease protocol.

The lifetime 256-command cap was removed, and retirement now includes a bounded Bloom-style filter. After 100,000 distinct successful synthetic mutations, the never-issued request ID `R5-disconnect-70` is rejected as `REPLAY_EXPIRED`. This is a false positive, not a replay. The test calls a side-effect-free handler, not 100,000 OS/network operations; it does not claim the whole service is permanently locked after that exact count.

**Required fix:** Use an exact bounded authenticated boot/lease-scoped sequence-window or equivalent protocol with explicit renewal. Keep idempotent result/uncertain-effect semantics and reject truly old requests even after individual entries age out. A probabilistic set can be a hint but cannot be the final authority denying Disconnect/recovery. Do not restore a lifetime cap, unbounded history or a reset that revives stale mutations.

**Close when:** Fresh safety commands continue to execute under long sessions and saturated caches; old, duplicate and reordered requests cannot repeat effects across retirement or restart. Test exact boundaries through the actual authenticated transport, not only a dispatcher mock.

### R5-14 [P1] Responsive IPC requires bounded operation I/O, not only bounded frames

**Evidence:** STATIC + GAP; existing Windows pipe failures remain distinct from the dispatcher tests.  
**Paths:** `LocalIpcServer.ServeOneAsync`, `IpcDispatcher.Dispatch`, broker guard/stop/recovery critical sections.

Frame reads and writes have limits, but the server still synchronously waits for the complete engine task, and duplicate in-flight requests synchronously wait for the original. Guard/effect I/O remains inside some state locks; cleanup can await Stop without an operation deadline. A bounded client round-trip can expire while server-side work continues under the service lifetime token. These behaviors must be reconciled before real network effects are enabled.

**Required fix:** Serialize authority decisions in a state actor, execute slow owned work outside that critical section, and post generation-bound completion events. Return operation tickets for long work, reserve safety capacity, and implement bounded cancellation/shutdown/recovery semantics. Preserve an explicit uncertain state after transport loss; do not assume timeout means no effects. Bound child kill-and-wait, guard operations and journal reconciliation without releasing protection prematurely.

**Close when:** Actual Windows pipe clients can query and request safety actions during stuck arm/start/stop/recovery and held/partial-client saturation. Session tasks, handles and replay waiters are either terminated or explicitly owned for recovery; no hidden global lock prevents Disconnect.

### R5-15 [P1] UI snapshots must be authenticated, correlated and internally consistent

**Evidence:** EXECUTED I02, I03; previous known-boot retirement fix preserved.  
**Paths:** `Application/UiSession.cs:164`, `UiSessionReducer.FromSnapshot`, `LocalIpcServer.RoundTripAsync`, desktop send/resync.

The mailbox accepts a response using unsupported protocol version 999 and lets it replace current state. A snapshot claiming Disconnected while `CoreRunning=true` is treated as a verified disconnect. The retired-boot set fixes the previously tested A-B-A ordering, but adopting any previously unseen boot still needs an authenticated current handshake and originating-request correlation.

**Required fix:** Validate protocol, request ID, transport/resync generation, broker boot and state ordering before accepting a response. Check snapshot invariants and preserve Unknown/uncertain state when fields contradict process or protection truth. Only a current authorized completion can prove disconnection or permit Exit. Bound retired-session tracking and resynchronize explicitly after server restart rather than trusting a different UUID alone.

**Close when:** Wrong protocol, wrong request, unexpected boot, delayed old replies and contradictory process/protection snapshots cannot clear safety uncertainty or resurrect Connected. Legitimate restart/resync and a genuinely clean disconnect remain usable.

### R5-16 [P1] Do not inherit another Xray user's semantics or discard certificate policy

**Evidence:** EXECUTED X01, X03; negative control X02 passes; native VMess omission case now passes.  
**Paths:** `Import/XrayOutboundParser.cs:109,122,209`, import normalization/canonicalizer/wire/generator compatibility matrix.

`ApplyEndpoint` uses each optional field or falls back to the first parsed user's value. X01 therefore gives the second VLESS user the first user's `xtls-rprx-vision` flow even though the second record omitted it. X03 loses an explicit `tlsSettings.allowInsecure=true` when owner opt-in is provided. Losing that flag currently makes verification stricter, not weaker; the defect is semantic fidelity/availability, not a demonstrated certificate bypass. X02 did not reproduce borrowing a first Shadowsocks server password, so no such defect is claimed.

**Required fix:** Normalize each endpoint/user from its own record plus only actual protocol-level defaults. Preserve supported TLS/REALITY/ALPN/transport/cipher/plugin values, or classify meaningful unsupported combinations explicitly. Do not silently inherit sibling credentials/options or default away an explicit value. Keep import, canonical digest, wire and emitted configuration consistent; when effective semantics change, migrate metadata without inheriting unproven health.

**Close when:** Zero/one/many wrappers, omitted and explicit values, multiple users, and format-equivalent records round-trip identically. Preserve the now-working VMess AlterId default and nine native profile checks. Extend controlled handshakes beyond the Shadowsocks case actually executed here.

### R5-17 [P1] Validate the complete HTTP field grammar after TLS

**Evidence:** EXECUTED W01, three authenticated wire cases; W02 and W03 controls pass.  
**Paths:** `Probe/NonTunCoreProbeTransport.cs: HeaderFraming` at line 478 and the response contract/byte accounting.

The tightened parser correctly rejects several previous status/framing cases, but still accepts ` Transfer-Encoding: chunked`, `X Bad: value`, and `X\0Bad: value` as successful authenticated 204 responses. All three negatives use real local TLS with a fixture-local trust root. Valid 204 is accepted and the untrusted-certificate control is rejected, so this is a parser-layer failure, not a TLS-fixture failure. No request-smuggling exploit or live public-proxy bypass is claimed.

**Required fix:** Validate field-name token syntax, control characters, leading whitespace/obsolete folding, separators and field-value constraints before processing named fields. Apply the approved target's exact status/body/framing contract with bounded fragmented reads and complete consumed-byte accounting. Reject contradictory or unsupported framing without waiting for a forbidden body indefinitely. Never relax target certificate verification to simplify parsing.

**Close when:** All three W01 cases fail with typed response errors, while W02/W03 retain their opposite outcomes. Add split/coalesced header, duplicate/conflicting length, delayed body, oversize and cancellation controls using the production parser and meaningful deadlines. See RFC 9110/9112.

### R5-18 [P1] Validate persisted settings and hostile input at every boundary

**Evidence:** EXECUTED D01 and S05; broader parser/registry/store review STATIC.  
**Paths:** `Domain/Settings.cs:32`, `SqliteCatalogue.Load`, `SourceLedger.Load`, registry loaders, URI/JSON/YAML/text entrypoints.

`ProductSettings.Validate` accepts a null DisabledFamilyIds collection even though downstream code dereferences it. Persisted cursor validation also allows an immediate index exception. Earlier Unicode containment and case-conflicting-key regressions improved and must not be reported as still unchanged. They do not establish a complete schema/decoding contract for every persisted or imported shape.

**Required fix:** Validate required/non-null collections, bounded values, revisions/schema versions, strings, enum definitions and path/origin structure before state publication. Apply compatible validation when loading persisted data, not just settings writes. Separate expected input faults from programmer/storage failures; quarantine or reject corrupt records without converting them to valid empty snapshots. Continue bounded byte-to-decoder-to-parser fuzzing, not only direct-string mutation.

**Close when:** Null collections/elements, negative/overflow revisions/cursors, future schemas, conflicting nested keys, malformed Unicode, truncated/compressed oversized bodies and mixed valid/invalid records produce typed bounded outcomes. Owner data and valid unknown-newer formats are preserved rather than silently rewritten.

### R5-19 [P1/P2] Account for consumed traffic and clock rollback consistently

**Evidence:** EXECUTED P04, D02; STATIC shared-budget/lifecycle review.  
**Paths:** `ProbeCoordinator.RunAsync` charge path at line 148, on-demand/speed paths, `ProbeByteBudget.Load:25`, source budgets.

A transport-returned Canceled result is now correctly treated as a deadline failure when the caller was not canceled, but the byte-charge branch still skips it. P04 consumes 100 reported bytes and observes less than 100 charged. A persisted fully spent budget dated one day ahead is reset to zero when loaded after a clock rollback. Saturating addition and backward-charge protection are genuine earlier fixes, but Load and all consumers must obey the same policy.

**Required fix:** Track cancellation origin separately from consumed bytes and charge every completed/partial attempt conservatively, including failed handshakes and deadlines. Reserve shared budgets atomically across routine/on-demand/speed work and retries; persist bounded state coherently. Treat a future-dated spent record as clock uncertainty, not free allowance. Use monotonic time for live limits and deliberate conservative UTC rollover for persisted day accounting.

**Close when:** Cancellation/deadline/exception paths cannot spend unaccounted traffic; competing workers cannot all oversubscribe the remaining allowance. Rollback, forward jumps, restart, corrupt files and overflow cannot refund a spent current allowance. Report application-payload versus actual wire accounting honestly.

### R5-20 [P1] Fix SQLite ownership and Windows file lifetime without losing data

**Evidence:** Existing Windows failures + fresh WPF reopen diagnostic; details in execution section.  
**Paths:** `Persistence/SqliteCatalogue.cs:44,660`, `Dispose`, `BackupTo`, `InspectExisting`, user/broker catalogue authority.

Copy-before-commit and stale-writer revision checking are useful and remain. Windows baseline still has nine file-lifetime/cleanup failures. The fresh UI harness separately examines close/dispose and same-process reopen, with any pool clear recorded as a diagnostic control rather than a production fix. A single-authority design and safe reads of a live database are necessary; pooled native handles, file sharing and integrity/quarantine must not conflict. This does not by itself prove data loss on an ordinary full process restart.

**Required fix:** Define catalogue ownership and immutable read/publication snapshots, transaction failure behavior and conflict recovery. Close/dispose all commands/readers/connections and explicitly manage pooling where files must be inspected, renamed, backed up or removed. Use safe sharing/locking consistent with live SQLite and WAL semantics; never quarantine a healthy live database because another authorized handle exists. Do not blindly sprinkle global ClearAllPools into production.

**Close when:** On Windows, open/close/reopen, concurrent allowed access, backups, upgrade/quarantine/uninstall and injected commit failures preserve data and release expected handles. Distinguish test-fixture teardown defects from actual library/application lifetime defects and prove the intended installed-account handoff.

### R5-21 [P2] Make single-row updates and retention scale to the promised catalogue

**Evidence:** EXECUTED fresh 1,000/5,000/10,000-node workload; retention integration STATIC/GAP.  
**Paths:** `SqliteCatalogue.TryUpdateInPlace:434`, `SameIdentity:509`, `MemoryCatalogue.Copy`, retention/eviction, catalogue presentation.

Secret re-protection was removed from a normal unchanged-identity assessment update, but the operation still copies/compares/serializes the catalogue and performs repeated linear searches. Fresh measurements are reported below rather than reusing round-4 numbers. A constant number of changed SQL rows does not make total CPU/allocation constant. Existing retention helpers also need complete scheduling and source-history enforcement.

**Required fix:** Use targeted mutations, indexed identity lookups, explicit changed-row sets, bounded immutable publication and batched assessments. Avoid traversing and serializing all credentials for one latency result. Wire retention/admission/expiry policies into real cycles, protect owner favorites and active sessions, and expose capacity limits rather than silently dropping useful data. Virtualize UI rows and keep blocking persistence off the dispatcher.

**Close when:** Repeat the provided workload with exact source/environment and report latency/allocation/SQL/protector calls, then run a sustained end-to-end catalogue test. One update must not allocate memory proportional to the entire catalogue. Restart/expiry/overflow tests preserve owner metadata and still-working old nodes.

### R5-22 [P1] Implement and prove DNS, IPv6 and endpoint-resolution boundaries

**Evidence:** STATIC + GAP; no production packet capture in this audit.  
**Paths:** `EndpointSafety`, `MihomoProfileGenerator` DNS/TUN/rules, fetch/probe dials, future Windows protection backend.

Literal-address screening and non-TUN loopback fixtures do not establish resolved-address/rebinding safety, internal user-DNS routing, IPv6 protection or candidate-path isolation under a live tunnel. The real network guard is absent. No actual packet leak is asserted from these source-only gaps.

**Required fix:** Separate minimal endpoint/bootstrap resolution from user-domain DNS, explicitly route internal user DNS through the intended outbound and avoid bootstrap recursion. Validate all resolved addresses under the correct destination policy and bind resolution to actual dialing. Support or block each advertised address family consistently. Keep LAN/bootstrap exceptions narrow, owned and visible. Never add a silent DIRECT fallback for protected user traffic.

**Close when:** An isolated authorized Windows environment demonstrates positive and negative packet controls for DNS cache miss/hit, IPv4/IPv6, encrypted DNS, UDP/TCP, uplink changes, core/broker death, reconnect and concurrent candidate probing. Packet captures must be private/redacted and not committed with live credentials.

### R5-23 [P1] Turn the improved journal into actual owned OS recovery

**Evidence:** STATIC + GAP; preserve the passing missing-journal/marker regressions.  
**Paths:** `Persistence/EffectJournal.cs`, `Recovery/Program.cs`, `INetworkGuard`, broker startup/disconnect/shutdown.

The missing-journal condition is now captured inside Open, and marker/duplicate-removal fixes deserve credit. The actual Windows effect enumeration, reconciliation and protection backend are still absent. A clean newly created journal is not universal proof that there are no owned OS objects, particularly when all metadata is missing. Recovery also has to remain independent of a healthy user catalogue and survive its own interruptions.

**Required fix:** Implement protected durable effect ownership, intent/commit records and OS reconciliation for routes, DNS, adapters and filters. Preserve unknown state until actual owned objects are reconciled, including missing/corrupt metadata, WAL and partial rollback. On authorized disconnect restore owned networking before releasing the final guard. Specify filter lifetime across broker death; in-memory flags cannot prove crash-surviving protection.

**Close when:** Partial installation/start/stop, UI/core/broker crashes, reboot, corrupt/missing journal, interrupted recovery and unrelated third-party network changes have observable safe outcomes. Never reset the entire firewall, DNS or routing table to make cleanup look successful.

### R5-24 [P1] Finish protected asset, child-process and diagnostic lifecycle

**Evidence:** STATIC; new native success tests are positive controls, not full fault/ACL proof.  
**Paths:** `Core/MihomoProcessController.cs`, `NonTunCoreProbeTransport`, `ProbeWorker`, `SecretRedactor`, core manifest/install paths.

The candidate worker now drains output continuously, and Windows loopback-port ownership is implemented and exercised by the positive native test. The separate configuration validator still uses unbounded ReadToEndAsync, conflates caller cancellation with timeout, kills without confirmed exit before deleting its credential directory, and does not implement the complete protected execution-path/ACL contract. Quoted-secret fixes do not prove all raw native diagnostics are safe.

**Required fix:** Use one reviewed owned-child lifecycle with scoped process/job handles, bounded retained output but continuous draining, safe kill-and-wait deadlines, protected temporary files and a controlled stale-file reaper. Verify exact assets at staging/install/launch boundaries and eliminate writable executable/DLL lookup. Redact structurally and with known secret canaries before retention/export. Preserve the original error when cleanup also fails and retain ownership until positively closed.

**Close when:** Hung/noisy/failing children, cancellation, partial process spawn, tampered assets, writable-path substitution and encoded/quoted/spaced secret canaries have tests on both platforms. No stale cleanup kills another session; no real credential or runtime YAML enters Git or diagnostic artifacts.

### R5-25 [P2] Finish the simple functional Russian desktop

**Evidence:** STATIC + fresh actual WPF render/settings experiment; no connected UI journey.  
**Paths:** `Desktop/MainWindow.xaml`, `MainWindow.xaml.cs`, `App.xaml`, `UiSession`, user-facing settings and tray.

The actual window and four navigation handlers render, but ServerList is still a TextBlock rather than a usable server selector. Manual selection, favorite/exclusion actions, full source controls, search/filter/sort, live traffic, speed actions, country constraints and complete themes/tray/accessibility behavior are not established. A visible button or fixed explanatory label must not imply implemented functionality. The original requirement was a simple attractive desktop, not merely a safe empty shell.

**Required fix:** Implement data-backed view models and virtualized interactive rows with measured states/reasons/timestamps. Keep latency, benchmark throughput and current transfer rate separate. Bind every retained control to an authoritative operation and provide clear pending/canceled/empty/error/protected states. Keep the four-view default simple; place advanced settings behind it. Finish startup/resync, explicit Exit, close-to-tray, keyboard/focus/accessibility, themes and meaningful DPI tests.

**Close when:** Exercise real user actions from a clean install through consent, refresh, server choice, favorite/exclusion, connection, failover, settings changes and safe Exit. Provide actual screenshots and behavior evidence for themes/scales, but do not count these two 96-DPI window sizes as a multi-DPI or connected acceptance pass.

### R5-26 [P0 delivery / P2 supply chain] Produce a retrievable installer bound to exact tested inputs

**Evidence:** GAP; packaging/verifier and notices inspected.  
**Paths:** `scripts/package.ps1`, `verify-release.ps1`, `test-windows-admin.ps1`, manifests, dependency records and release/handoff docs.

Packaging still publishes folders, explicitly not an installer, does not stage the core/driver or register a service, and the release verifier remains a NOT_RUN/exit-2 placeholder. The Mihomo license text correction remains valid. Nine-project NuGet results contain no vulnerable entries, but do not cover every native/Go/driver dependency, SBOM, signature, installer content or source-distribution obligation.

**Required fix:** Build the agreed Windows package with protected locations, reviewed core/driver assets, service identity/ACLs, user settings/data policy, safe upgrades and uninstall. Generate exact source/tree, dependency, binary/driver and artifact identities from a clean build. Reconcile component notices/source records and SBOM for the actual package. Keep signing limitations honest; hashes identify bytes but are not signatures. Implement the verifier, then publish retrievable artifacts.

**Close when:** Another authorized clean Windows machine downloads the same artifact, verifies it, installs unelevated UI plus the intended broker, completes the user journey, upgrades and uninstalls without harming unrelated networking or owner data. A local folder or an old archive is not the current release.

### R5-27 [P1] Make Windows/native tests permanent and interpret failures correctly

**Evidence:** EXECUTED baseline and independent suites; audit-harness limitations explicit.  
**Paths:** `.github/workflows/ci.yml`, `tests/AutoVpn.UnitTests`, native fixture prerequisites, packaging/admin tests.

The full Linux suite with the pinned core really passed 162/162. Windows still has 15 baseline failures: five pipe cases, nine file-lifetime/cleanup cases, and one fixture invoking `/usr/bin/pkill`. The no-core Linux run has one timing/cancellation failure in the old Q24 TLS case, which passes in the native run. These are not 16 newly exploited vulnerabilities. The new native matrix independently passes all 10 cases on both platforms.

**Required fix:** Retain Linux and Windows build/test jobs, permanent reviewed native positive/negative controls, exact artifact evidence and an isolated admin acceptance runner. Repair platform-specific fixture lifetime and timing instead of disabling all failing Windows tests. Keep standard native prerequisites explicit: the audit-only Native_Round5 tests are selected separately and require their R5_CORE_PATH/hash, so blindly merging them into an unconditional suite is wrong. Diagnose the intermittent Q24 case and preserve meaningful adverse HTTP assertions.

**Close when:** CI distinguishes unit/contract, real native non-TUN, actual WPF and installed Windows packet/installer evidence. Skipped or unavailable prerequisites are visible. New tests fail on the audited baseline and pass after correct production fixes; controls remain green without loosening the product's protection requirements.

### R5-28 [P1 acceptance] Close complete journeys rather than another numbered test list

**Evidence:** GAP; current status honestly says v1 is not accepted.  
**Paths:** `docs/IMPLEMENTATION_DIRECTIVE.md`, all audit/status files, implementation/handoff/architecture/user documentation.

All 33 imported round-4 cases pass in the native baseline, yet the product still cannot perform its promised installed Windows journey, and new cross-component controls fail. This is not evidence that previous work was fictitious. It is evidence that closing a list of narrow examples is not the same as satisfying the original contract. Missing production code is still engineering work even when its final Windows execution is externally blocked.

**Required fix:** Follow the ordered completion slices below and maintain one traceable requirement/finding/evidence ledger. Separate implemented, wired, contract-tested, native-tested, Windows-installed-tested, blocked and released. Finish all independently achievable production integration and packaging before stopping on an external gate. Do not rewrite the project or add unrelated features. Report a true blocker with a runnable checkpoint and precise missing prerequisite, never a green reinterpretation.

**Close when:** Original mandatory acceptance plus relevant regression controls have exact commit/artifact/environment evidence. The handoff contains a usable deliverable or an explicit unaccepted checkpoint with remaining work. A larger report or higher undifferentiated pass count cannot substitute for this result.

## 4. Previous findings remain binding

The links below identify remaining closure work, not a claim that every preceding assertion still fails unchanged. Read the original wording before changing behavior. A passing exact counterexample is credited only at its tested layer.

| Round-4 finding | Current round-5 linkage | Closure position |
|---|---|---|
| R4-01 | R5-01, R5-26 | OPEN: installed core/guard/SCM/installer still absent. |
| R4-02 | R5-02, R5-14, R5-20 | OPEN: Windows identity and catalogue/runtime handoff. |
| R4-03 | R5-03 | PARTIAL: branch resolution exists, but commit/tree consistency is unproven and S01 fails. |
| R4-04 | R5-06, R5-08 | PARTIAL: persistent cursor rotation improved; durable state and complete maintenance remain. |
| R4-05 | R5-04, R5-06 | PARTIAL: artifact 304 freshness improved; lost catalogue and discovery304 still fail. |
| R4-06 | R5-07 | PARTIAL: required non-null proof fixed; replay and completion ordering fail. |
| R4-07 | R5-08, R5-19 | PARTIAL: deadline classified separately; charging and failure taxonomy incomplete. |
| R4-08 | R5-09, R5-18 | PARTIAL: denied ordinary probes fixed; manual selection purpose and full policy lifecycle fail. |
| R4-09 | R5-08, R5-19 | OPEN/PARTIAL: retry-after exists; independent health scheduler and measured budgets missing. |
| R4-10 | R5-17 | PARTIAL: contradictory framing fixes pass; invalid field grammar still accepted. |
| R4-11 | R5-18 | TESTED FIX at prior corpus boundary; byte-level/property coverage still required. |
| R4-12 | R5-16, R5-18 | PARTIAL: case-conflicting key test fixed; full nested mapping contract incomplete. |
| R4-13 | R5-16, R5-24 | PARTIAL: 9 native profiles now pass; sibling-user semantics and certificate options fail. |
| R4-14 | R5-10, R5-11 | PARTIAL: exact previous cases pass; partial effects and cancellation state still fail. |
| R4-15 | R5-11 | PARTIAL: post-confirmation ownership improved; ownership before accepted Start still missing. |
| R4-16 | R5-10, R5-12 | PARTIAL: prior epoch/exclusion cases pass; cancellation/current-country/session policy fail. |
| R4-17 | R5-09 | PARTIAL: Start accepts manual override; stale admission/confirmation still reject it. |
| R4-18 | R5-13 | PARTIAL: replay Bloom filter introduces fresh-safety false positives. |
| R4-19 | R5-15 | PARTIAL: retired boot IDs improve ordering; protocol/correlation/invariant gaps remain. |
| R4-20 | R5-14 | OPEN: slow effect work and safety capacity. |
| R4-21 | R5-23 | TESTED FIX at missing-journal unit boundary; actual OS reconciliation not implemented. |
| R4-22 | R5-18, R5-20 | OPEN/PARTIAL: real same-process Windows reopen fails; full authority contract incomplete. |
| R4-23 | R5-21 | OPEN: single-row write still allocates approximately 37.68 MB at 10k nodes. |
| R4-24 | R5-08, R5-19, R5-21 | OPEN/PARTIAL: durable scheduling, retention and actual aggregate work/traffic caps remain incomplete. |
| R4-25 | R5-22 | OPEN: DNS/IPv6/resolution and real active-TUN isolation. |
| R4-26 | R5-24 | OPEN/PARTIAL: native owned workers run on both OSs; installed assets/process/diagnostic hardening unfinished. |
| R4-27 | R5-19 | PARTIAL: prior counter fixes pass; consumed-byte charging and persisted future-day handling still fail. |
| R4-28 | R5-09, R5-12, R5-18, R5-25 | PARTIAL: manual purpose, session policy, live country metadata and persisted-setting validation remain inconsistent. |
| R4-29 | R5-25 | OPEN: actual rendering is not manual-server/favorite/metrics/theme/tray completion. |
| R4-30 | R5-27 | PARTIAL: native Linux baseline passes; Windows and timing fixtures remain. |
| R4-31 | R5-26 | OPEN: installer, exact artifact provenance and complete component records. |
| R4-32 | R5-28 | OPEN: original complete v1 product and handoff still required. |

### Original F01-F34 traceability

| Original finding | Current closure work |
|---|---|
| F01 | R5-01, R5-26 |
| F02 | R5-03, R5-04, R5-05, R5-06, R5-08 |
| F03 | R5-02, R5-09, R5-15, R5-25 |
| F04 | R5-13, R5-14 |
| F05 | R5-02 |
| F06 | R5-07, R5-13, R5-14, R5-15 |
| F07 | R5-07, R5-10, R5-11, R5-12, R5-24 |
| F08 | R5-10, R5-12 |
| F09 | R5-10, R5-11, R5-23 |
| F10 | R5-01, R5-22, R5-23 |
| F11 | R5-20, R5-23 |
| F12 | R5-22 |
| F13 | R5-22 |
| F14 | R5-07, R5-08 |
| F15 | R5-07, R5-08, R5-17, R5-19 |
| F16 | R5-04, R5-05, R5-06, R5-18 |
| F17 | R5-16, R5-18 |
| F18 | R5-16, R5-24 |
| F19 | R5-09, R5-16, R5-18 |
| F20 | R5-16, R5-18, R5-24 |
| F21 | R5-16, R5-18, R5-20 |
| F22 | R5-03, R5-04, R5-05, R5-06, R5-19 |
| F23 | R5-18, R5-20 |
| F24 | R5-08, R5-21 |
| F25 | R5-03, R5-04, R5-05, R5-06 |
| F26 | R5-09, R5-12, R5-25 |
| F27 | R5-08, R5-12, R5-25 |
| F28 | R5-06, R5-08, R5-19 |
| F29 | R5-24 |
| F30 | R5-26 |
| F31 | R5-27 |
| F32 | R5-26 |
| F33 | R5-26, R5-27, R5-28 |
| F34 | R5-15, R5-25 |

The original 20 end-to-end acceptance scenarios are not replaced by these tables. Preserve source families and user data; test fresh install, offline cache, retained old healthy nodes, fresh-node admission, stable connected refresh, exclusions/countries, failover, network changes, cancellation, crash/recovery, upgrade and uninstall as complete journeys.

## 5. Executed regression pack and reproduction

The canonical new files are `tests/AutoVpn.UnitTests/IndependentRound5Tests.cs` and `IndependentRound5WireTests.cs` at the canonical regression commit. The following are the **34 actual executed cases on each OS**, not merely proposed tests. Internal contradictory inputs test publication/validation contracts; they are not automatically externally reachable exploits.

| Case | Outcome on Linux and Windows |
|---|---|
| `B01_ManualExclusionOverrideMustReachConfirmedConnection(ageSeconds: 0)` | FAILED |
| `B01_ManualExclusionOverrideMustReachConfirmedConnection(ageSeconds: 65)` | FAILED |
| `B02_CancelAfterSpawnMustSettleConnectingState` | FAILED |
| `B03_PartialSpawnIOExceptionMustKeepCleanupOwnership` | FAILED |
| `B04_RejectedStartWithFailedStopMustRemainRecoverablyOwned` | FAILED |
| `B05_CanceledFailoverMustNotCommitLateReplacement` | FAILED |
| `B06_StrictCountryMustRecheckUpdatedStandbyMetadata` | FAILED |
| `B07_ExplicitSessionLanPolicyMustSurviveFailover` | FAILED |
| `C01_ControlStableCommitDiscoveryWorks` | PASSED |
| `C02_ControlMatchedProofAndOwnedDisconnectWork` | PASSED |
| `D01_NullDisabledFamilyCollectionMustBeRejectedAsSettings` | FAILED |
| `D02_ClockRollbackMustNotRefundPersistedDailyBudget` | FAILED |
| `I01_ProbabilisticReplayFilterMustNotDenyFreshSafetyCommand` | FAILED |
| `I02_UnsupportedResponseProtocolCannotReplaceUiState` | FAILED |
| `I03_LiveCoreContradictionMustNotProveCleanDisconnect` | FAILED |
| `P01_ConsumedProofFromOldNetworkMustNotBeRestampedAsFresh` | FAILED |
| `P02_OlderConcurrentCompletionMustNotOverwriteNewerObservation(olderSuccess: False)` | FAILED |
| `P02_OlderConcurrentCompletionMustNotOverwriteNewerObservation(olderSuccess: True)` | FAILED |
| `P03_UnsupportedObservationCannotPassOnDemandAdmission` | FAILED |
| `P04_DeadlineResultMustChargeItsConsumedTraffic` | FAILED |
| `S01_MovingBranchMustNotMixCommitAndUnrelatedTree` | FAILED |
| `S02_BlobShapedMetadataIsNotACommitObject` | FAILED |
| `S03_LedgerWithoutCatalogueCannotTurn304IntoUsableSnapshot` | FAILED |
| `S04_InvalidPrimaryBodyMustNotSuppressValidReviewedMirror` | FAILED |
| `S05_CorruptPersistedCursorMustNotEscapeAsIndexException` | FAILED |
| `S06_Discovery304MustAdvanceItsSchedulingCheck` | FAILED |
| `X01_XraySecondUserMustNotInheritFirstUsersFlow` | FAILED |
| `X02_MissingSecondPasswordMustNotBorrowFirstServerCredential` | PASSED |
| `X03_XrayExplicitCertificatePolicyMustSurviveImport` | FAILED |
| `W01_InvalidFieldNamesCannotAuthenticateAsValidHttp(field: " Transfer-Encoding: chunked")` | FAILED |
| `W01_InvalidFieldNamesCannotAuthenticateAsValidHttp(field: "X Bad: value")` | FAILED |
| `W01_InvalidFieldNamesCannotAuthenticateAsValidHttp(field: "X\0Bad: value")` | FAILED |
| `W02_ControlValidAuthenticated204IsAccepted` | PASSED |
| `W03_ControlUntrustedCertificateIsRejected` | PASSED |

Native facts use names beginning `Native_Round5`. Nine theory cases validate profiles and one fact exercises the real Shadowsocks/HTTPS positive plus wrong-credential negative. They require `R5_CORE_PATH` and `R5_CORE_HASH`, set only after checking the official archive and executable against `config/core-manifest.json`. Do not derive trust by hashing an arbitrary downloaded executable and treating that same hash as an independent expected value.

For a local **disposable checkout**, the regression invocation is:

```text
dotnet build AutoVpn.slnx -c Release --nologo
dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~IndependentRound5&FullyQualifiedName!~Native_Round5" --logger "trx;LogFileName=independent.trx" --results-directory audit-results --blame-hang-timeout 90s
```

After separately checking and setting the native core environment, run:

```text
dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --nologo --filter "FullyQualifiedName~Native_Round5" --logger "trx;LogFileName=native.trx" --results-directory audit-results --blame-hang-timeout 120s
```

The exact workflows in the evidence bundle contain PowerShell/Linux hash verification and the selected platform commands. Baseline evidence was obtained before adding audit native facts; when porting tests, separate mandatory unit tests from explicitly provisioned native suites. Never let missing prerequisites become green no-op facts. Do not execute the optional WPF reopen harness against an owner's populated real profile: it changes two settings and uses a diagnostic pool clear. Its intended environment is a fresh disposable Windows runner.

## 6. Finite implementation slices and delivery gates

### Slice A: regressions and authority contracts

Port the new tests without weakening their meaning, correct independently demonstrated input/lifecycle/proof/cache failures, and retain the passing controls and previous rounds. Replace ad-hoc conditional patches with explicit typed contexts: source snapshot identity, health attempt identity, session policy, process cleanup ownership and broker transport/lease identity. Correct test-fixture portability/timing separately from product defects. Keep a narrow, tested commit for each coherent change.

This slice is not the final deliverable. A test-suite-only fix commit must not be reported as completion of the owner goal.

### Slice B: one connected unelevated catalogue workflow

Create a long-lived application coordinator with stable source identities, durable fair queue progress, validated current commit/tree/content binding, parse-aware mirror fallback, last-good snapshots and honest coverage. Run bounded health maintenance independently of source download intervals; prioritize on-demand selection under the correct purpose and current policy. Publish ordered context-bound measurements and preserve the connected runtime during refresh. Demonstrate fresh empty state reaching a real verified catalogue, including restart, invalid source, 304-with-lost-cache and settings changes, without seeded success.

### Slice C: real Windows control, TUN and owned recovery

Implement the supported Windows service identity and narrow authorized IPC/runtime-set boundary. Connect the real owned core and network backend, preserve cleanup handles across partial failures, move slow effects outside state authority locks, and retain bounded safety-command capacity. Keep the owner UI unelevated and the full subscription parser out of the service. Supervise actual process exit, verify the production path, stage bounded fresh standbys and apply immutable session policy at every switch.

Use an authorized disposable Windows environment with an independent recovery path. Never change the networking of the machine carrying your only control channel. Demonstrate TUN routing, DNS/IPv6 policy and fail-closed behavior using controlled endpoints and independent packet/OS observations. Reconcile only AutoVPN-owned effects. Preserve unrelated firewall/routes/DNS and record exact tested driver/core/OS identities.

### Slice D: usable Russian interface and retrievable package

Replace the remaining display-only server text with a usable measured list, manual selection, favorites/exclusions, source controls, settings and honest active/available/disabled/error states. Bind live state, timestamps and reason codes to authoritative data. Keep benchmark speed separate from current transfer rate and latency history. Finish keyboard/focus, scaling/themes, tray close/Exit and recovery UX without cluttering the four main pages.

Produce the real installer with protected assets, safe service registration, upgrade/uninstall/data-retention behavior, licenses, transitive component inventory and exact source-to-artifact provenance. Publish an independently retrievable installer with size and SHA-256. Missing signing credentials must be reported honestly; a missing installer is not a signing-only blocker.

### Slice E: independent acceptance, not a pass-count ceremony

Run a clean supported Windows 11 install-to-connect-to-refresh-to-failover-to-disconnect journey and every mandatory original acceptance gate, plus the applicable adverse cases here. Test unexpected core/UI/broker exits, partial installation, corrupted/missing journals, sleep/reboot/NIC changes, concurrent requests and stale work. Use true positive and broken-path negative controls. A mock can prove state transitions but not packet protection; a native profile validator can prove syntax but not protocol fidelity; a non-TUN success can prove its own path but not all-system routing.

Execute a bounded sustained catalogue workload with measurements and leakage/resource accounting. Tie every result to the exact source commit, dependency/core/driver identities and produced installer. Finish child tasks and preserve a recoverable checkpoint at each slice. No perpetual goal loop, unlimited traffic, silent broad network changes or fabricated results.

If a genuine external prerequisite blocks a remaining gate, finish independent implementation first, push the checkpoint, identify the precise required access/environment and list `NOT_RUN` gates. Do not claim release-ready, permanently refuse all Windows operations, or repeat a status-only handoff instead of progressing to the available next slice.

## 7. Closure reporting contract

Create `docs/AUDIT_ROUND5_STATUS.md`, with every R5-01 through R5-28 and its relevant R4/F links. Each entry must provide:

```text
Finding | Status | Fix commit | Production paths | Regression/acceptance IDs
Environment | Exact command/run | Evidence artifact | Remaining gate
```

Use `OPEN`, `IN_PROGRESS`, `IMPLEMENTED_NOT_VALIDATED`, `BLOCKED_EXTERNAL`, `VERIFIED`. Distinguish implemented, wired and executed. A passing helper closes only its helper contract; a Windows behavior requires actual Windows evidence. Do not make an implementation omission disappear under `BLOCKED_EXTERNAL`. Preserve this audit and raw adverse evidence rather than editing it into a retrospective success story.

Dispute a finding with the exact invariant, source and positive/negative execution evidence. A legitimate manual exclusion override is not a blanket bypass for forbidden security/source/country rules. A stricter lost certificate option is not an insecure TLS exploit. A synthetic proof contradiction is not automatically an authenticated external attack. Correct interpretations are welcome; omissions are not.

Final handoff in Russian: exact pushed commit, tested source tree, installer location/size/SHA-256, component/OS versions, what was actually executed, explicit failures/skips/NOT_RUN, installation and recovery instructions, and remaining limitations. Verify the remote commit. Do not force-push, leak live subscription credentials, or claim v1 readiness until the original required gates pass.

## 8. Primary evidence and references

- Audited source: https://github.com/alinescafs3mp-afk/vpn/tree/49e5bd54e41731b9b96b83789def31e86c701ee6
- Baseline run: https://github.com/alinescafs3mp-afk/vpn/actions/runs/37133249313
- Canonical new regressions and both native platforms: https://github.com/alinescafs3mp-afk/vpn/actions/runs/37133873472
- Final real-WPF and scale workload: https://github.com/alinescafs3mp-afk/vpn/actions/runs/37134634399
- Corrected production export: https://github.com/alinescafs3mp-afk/vpn/actions/runs/37133294396
- GitHub commit objects: https://docs.github.com/en/rest/commits/commits#get-a-commit
- GitHub tree objects: https://docs.github.com/en/rest/git/trees#get-a-tree
- HTTP semantics: https://www.rfc-editor.org/rfc/rfc9110.html
- HTTP/1.1 message syntax and field grammar: https://www.rfc-editor.org/rfc/rfc9112.html
- Pinned core source: https://github.com/MetaCubeX/mihomo/tree/88dcbf7f1614a67c3b36b848ee3592dfa92ada36

Live reference documentation can change. Validate against the exact pinned SDK/core and preserve proof in the fix report. References do not substitute for executing the app's acceptance gates.

**Start with the current repository/environment, confirm the comparison pin, then execute Slice A and continue through the complete product slices. Communicate meaningful milestones and true blockers to the owner in Russian.**
