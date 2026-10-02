# AutoVPN: hard audit, round 2

**Recipient:** Grok, implementation lead.  
**Communication with the owner:** Russian only. This directive and engineering identifiers are English.  
**Repository:** `alinescafs3mp-afk/vpn`  
**Review date:** 2026-10-03. Commit and CI timestamps below are UTC.  
**Audited main commit:** `560e5df0fb6c63b8a7daed55c97207b8b69be65a`  
**Audited tree:** `768c7badf331caaf7611d52626d32568b4b39d1c`  
**Latest implementation commit:** `b532631694f81b438a3902da27d0a4e97f998ae6`  
**Previous directive commit:** `2edd057c52fb47111e206b379704f4e6a6292977`  
**Verdict:** **V1 NOT ACCEPTED. Meaningful fixes exist, but the end-to-end Windows product is still absent and several newly wired paths are incorrect.**

This document supplements, rather than replaces, `for_fix/ASTRA_HARD_AUDIT_FIX_DIRECTIVE_2026-10-02.md`. The original `docs/IMPLEMENTATION_DIRECTIVE.md` remains the product contract. Preserve both documents. Do not turn this assignment into another documentation-only closure pass.

## 1. Evidence, scope and limitations

The comparison with the previous directive commit contains **six commits and 46 changed or added files**. Package A is `2b46431693fe58eb02c40a36a0b192ce000b6fed`; package B is `1ba3cf78dd242141d8605286ac2a45101ad737c0`; package C is `b532631694f81b438a3902da27d0a4e97f998ae6`. The audited head is its status/documentation amendment, committed at `2026-10-02T21:24:09Z`.

This is a repository-wide source and integration review, with particular attention to the changed production paths and the tests offered as evidence of closure. The review covers entrypoints, WPF command wiring, catalogue ownership, IPC, state transitions, import and identity, core configuration, candidate testing, source discovery/fetch/reconciliation, persistence/recovery, scheduling, CI, packaging and completion reports. Previously reviewed unchanged modules remain subject to the first audit; their unchanged status is not a new runtime test.

**Existing CI independently inspected:** workflow run `37066620339`, job `111035930327`, for the audited head. Its log records Ubuntu 24.04.5, SDK 10.0.112, a Release solution build with zero warnings and errors, and `dotnet test AutoVpn.slnx -c Release --nologo` with **63 passed, zero failed, one skipped, 64 total**. The optional Mihomo test is explicitly skipped because `AUTOVPN_MIHOMO_PATH` is empty. This corrects the earlier green no-op. It is not native-core or Windows validation. The release-list API returned no published releases during this review.

**Not executed by this auditor:** a new .NET build or test run, the reproductions proposed below, native Mihomo probes, Windows/UI/SCM/TUN/WFP/DPAPI execution, packet capture, installer execution, or an independent verification of the old local binary archive. This environment has no available `dotnet` executable, and a direct Git transport attempt could not resolve GitHub; repository source and existing CI were available through the GitHub connector. No exhaustive historical secret scan or current transitive vulnerability scan was completed.

All findings below are **source-confirmed defects or implementation gaps**, with explicit qualifications for platform-dependent consequences. The proposed `RT` scenarios must still be executed. A source review does not establish that a live exploit, packet leak or destructive failure occurred. Some issues are latent behind the currently refusing production adapters; the candidate-probe path introduces additional risks when a real core binary is configured.

Priority definitions: **P0** blocks the promised user journey or basic protection; **P1** is a serious safety/correctness/trust-boundary defect; **P2** is a required completeness, performance or evidence fix. These are engineering priorities, not CVSS scores. The 28 groups are not a count of independently exploited vulnerabilities.

## 2. What actually improved

Retain the useful changes instead of rewriting the project:

- Malformed VMess/nested JSON handling and rejection of unrecognized document shapes improved. `RefreshMerge` now distinguishes document validity from arithmetic balance.
- Expired and different-epoch healthy assessments can be selected for rechecking; an epoch change during the tested callback is rejected. The short pre-connect age is enforced, although the workflow that obtains it is missing.
- Protocol-specific SNI keys, VMess cipher emission, several transport fields and URI obfs option translation improved. Unknown URI security values are rejected in the tested normalized path.
- SQLite uses copy, durable save, then publication for single-instance mutations. The tested protector failure no longer publishes the failed mutation in that instance.
- The production subscription HTTP handler disables implicit redirects and ambient proxies and puts a deadline around the body read. The controlled HTTPS tests for these properties are useful evidence.
- Pipe frame/write/client deadlines, concurrent acceptance, explicit operation identifiers, stricter revision checks, unknown-state UI text and failure-to-start protection retention are useful partial steps.

None of these changes proves a working installed Windows tunnel. Some of their interactions are defective, as detailed below.

## 3. Reconciliation of every first-round finding

`VERIFIED` in the second column is Grok's recorded state, not this auditor's blanket acceptance. A narrow helper result can be accepted while its original end-to-end finding remains open.

| Original | Grok's state | Round-2 assessment and linkage |
|---|---|---|
| F01 | BLOCKED | Still no real production core/SCM composition. R2-06, R2-28. |
| F02 | IN_PROGRESS | Coordinator exists; discovery, HTTPS proof and runtime handoff remain broken. R2-01, R2-07, R2-09 through R2-14. |
| F03 | IN_PROGRESS | Consent persistence improved locally; authoritative session/startup/exit integration is incomplete. R2-07, R2-08, R2-12, R2-24, R2-26. |
| F04 | IN_PROGRESS | Single idle-client case improved; saturation, task retention and command lifecycle are not closed. R2-15 through R2-18. |
| F05 | BLOCKED | All unverified Windows peers are now rejected. This is safe refusal, not Windows authorization. R2-06, R2-16. |
| F06 | IN_PROGRESS | Revision/idempotency additions create a 256-response lifetime limit and do not bind retries to their content. R2-15, R2-17, R2-18. |
| F07 | IN_PROGRESS | Confirmation context improved; stale broker continuations and real process ownership remain unsafe/unimplemented. R2-02, R2-03, R2-17, R2-18. |
| F08 | VERIFIED | Narrow CoreExit-at-cap transition improved; process truth, late confirmation and guarded retry remain open. R2-18. |
| F09 | IN_PROGRESS | Failed Start returning false retains protection. Exception, cancellation and restoration ordering remain open. R2-17 through R2-19. |
| F10 | BLOCKED | No real protection implementation or packet evidence. R2-06, R2-19, R2-23. |
| F11 | OPEN | Durable recovery-unknown marker and OS reconciliation are still missing. R2-19. |
| F12 | OPEN | Internal DNS path and IPv6 protection still lack the required implementation/proof. R2-23. |
| F13 | OPEN | Resolved destination vetting and rebinding controls remain absent. R2-23. |
| F14 | VERIFIED | Accept the narrow expiry/epoch regressions. Reopen workflow closure: 60-second admission versus 30-minute recheck, clock and policy gaps. R2-04, R2-05. |
| F15 | IN_PROGRESS | New native probe sends plaintext instead of HTTPS; admission, isolation, cancellation and budgets remain incomplete. R2-01 through R2-05. |
| F16 | VERIFIED | Accept helper preservation on malformed documents. Reopen coordinator closure: premature ETags, false PUBLISHED, tree 304. R2-10, R2-11. |
| F17 | IMPLEMENTED_NOT_VALIDATED | Useful parser containment, but wrapper limits and nested schema validation remain incomplete. R2-20, R2-22. |
| F18 | IMPLEMENTED_NOT_VALIDATED | SNI/cipher string fixes are real; native protocol/transport semantics still need verification. R2-20. |
| F19 | VERIFIED | Accept normalized URI rejection of bogus security. The public emitter still has a mixed-case validation/emission disagreement. R2-20. |
| F20 | IMPLEMENTED_NOT_VALIDATED | Transport fields expanded, but H2, nested options and Xray coverage are not complete. R2-20, R2-22. |
| F21 | VERIFIED | Reopened: hash-version migration is not proof of equal effective configuration; UDP identity disagrees with emission. R2-21. |
| F22 | VERIFIED | Accept the tested production-handler timeout/redirect properties. Global scheduling, cumulative budgets and resolved destination policy are separate remaining obligations. R2-13, R2-14, R2-23. |
| F23 | VERIFIED | Accept tested single-instance save-before-publish. Cross-instance lost updates and mutable authority remain open. R2-07, R2-25. |
| F24 | OPEN | Full database rewrites and ineffective capacity/expiry enforcement remain. R2-14, R2-25. |
| F25 | IN_PROGRESS | Registry is loaded, but refresh is frozen to the initial commit and discovery cannot reuse 304. R2-09 through R2-11. |
| F26 | OPEN | Country codes, conflicts and strict/preferred behavior remain incomplete. R2-24, R2-26. |
| F27 | IN_PROGRESS | A generic stream-speed helper is not measured node throughput, bounded runtime standbys or ranking integration. R2-04, R2-24, R2-26. |
| F28 | IN_PROGRESS | Final jitter floor improved. No recurring product scheduler, complete clock/epoch lifecycle or comprehensive settings validation. R2-05, R2-12, R2-24. |
| F29 | OPEN | Previous cleanup/redaction gaps remain; new worker adds static control credentials and unmanaged output. R2-02, R2-03. |
| F30 | BLOCKED | No installer or retrievable updated release. R2-06, R2-28. |
| F31 | IN_PROGRESS | Honest native-test skip is fixed. Windows and production-path test coverage remain missing. R2-27. |
| F32 | OPEN | Incorrect Mihomo MIT entry still present. No current package/SBOM reconciliation. R2-28. |
| F33 | OPEN | Old artifact does not contain these changes; updated reproducible provenance is missing. R2-28. |
| F34 | OPEN | Actual Russian desktop usability, themes, DPI, tray and functional controls remain unvalidated/incomplete. R2-08, R2-26. |

The seven rows marked VERIFIED by Grok are therefore not seven fully closed product requirements. Record accepted sub-properties and remaining obligations explicitly, rather than either erasing progress or declaring the entire row complete.

## 4. Mandatory remediation findings

### R2-01 [P1] The new HTTPS candidate test never negotiates TLS

**Paths:** `src/AutoVpn.Infrastructure/Probe/NonTunCoreProbeTransport.cs`, especially `Socks5Client.GetStatusAsync` and `ProbeAsync`; `tests/AutoVpn.UnitTests/PackageBTests.cs`, including `ServeHttp204` and `SocksClientReadsALocalStatusAndSpeedStaysUnknownWithoutHealth`.

After SOCKS CONNECT, the client writes an ordinary HTTP GET directly to `NetworkStream`, including for an `https://` target on port 443. There is no target-side TLS handshake, certificate validation or authenticated target identity. It reads only the beginning of a status line and the caller accepts 200 or 204. A proxy can return that line without reaching the target; an ordinary TLS-only target will not receive the expected TLS client handshake. Encryption of the outer VPN connection does not authenticate the HTTPS test destination.

The new test uses a plaintext loopback HTTP responder, so its green result confirms the wrong protocol rather than detecting it.

**Fix:** implement actual TLS over the owned SOCKS connection, or an appropriately configured HTTPS client using that SOCKS endpoint. Authenticate the requested hostname, enforce the target certificate policy, reject redirects/unexpected responses as defined by the target registry, and validate bounded headers/body. Use the required independent targets. Never fix this by changing targets to HTTP, bypassing certificate validation or accepting any 200 response.

**Acceptance:** RT01. A valid controlled TLS target passes; plaintext 204, a fabricated proxy status, wrong-host certificate, untrusted certificate and wrong response semantics fail. The positive test must prove which target and candidate carried the exchange. This is independently implementable and testable without Windows TUN. Reference P1 below.

### R2-02 [P1] Probe success is not bound to an owned, authenticated worker

**Paths:** `NonTunCoreProbeTransport.BuildProbeYaml`, `FreePort`, `WaitForPortAsync`, `ProbeAsync`; `Core/MihomoProfileGenerator.cs`.

The controller secret is a fixed public literal. Controller and SOCKS ports are acquired by opening and closing temporary listeners, then reused later. The two allocations are not an atomic reservation, and readiness is merely successful TCP connection to the SOCKS port. There is no proof that this listener belongs to the intended live core with the intended configuration. Loopback is not authentication against other local processes/accounts. A port collision, stale listener or local controller access can invalidate measurement attribution.

**Fix:** use per-worker random control credentials, explicit owned instance identity, distinct collision-safe endpoint allocation and authenticated readiness/configuration verification. Restrict listeners to the intended local clients. Treat a port collision or exited child as a worker failure, never as a successful candidate. Disable unnecessary controllers rather than exposing an unauthenticated capability that the worker does not use.

**Acceptance:** RT02. Occupied/reused/swapped ports, an unrelated SOCKS responder, an exited child and unauthorized controller requests cannot produce candidate evidence. Every result names the actual worker/configuration context.

### R2-03 [P1] Worker lifecycle and path isolation are not complete

**Paths:** `NonTunCoreProbeTransport.cs`; unchanged `Core/MihomoProcessController.cs`; `Domain/TextPolicy.cs` redactor.

The new worker redirects stdout/stderr without draining either stream, kills without awaiting termination, disposes the process immediately and swallows a temporary-directory deletion failure. Profiles contain credentials. Errors around generation, socket I/O and cleanup have inconsistent containment. Full binary hashing on each attempt is expensive and is not an execution-path ownership guarantee. Setting `Tun=false` does not by itself keep the worker's outbound traffic from traversing another active TUN.

**Fix:** implement one bounded, scoped process supervisor reused by validation/probing where appropriate. Drain bounded output, redact before retention, observe exit, cancel, kill and await the owned instance, then clean restricted temporary data. Track failed cleanup for a safe reaper. Validate protected binary/DLL paths before privileged use; do not rely on a writable path plus an earlier hash. Bind candidate dialing to its intended physical path using the exact supported core/OS mechanism. Keep target authentication separate from path attribution.

**Acceptance:** RT03 and RT04. A chatty/stalled/exited child cannot hang the worker; cancellation leaves no unowned child or forgotten credential profile. With working production tunnel A and broken candidate B, B must fail rather than borrowing A's success; test the inverse and concurrent workers. Do not claim this isolation without the corresponding controlled network evidence.

### R2-04 [P1] Admission, cancellation and measurement accounting still disagree with production behavior

**Paths:** `Probe/ProbeCoordinator.cs`; `NonTunCoreProbeTransport.cs`; `Refresh/CatalogueCoordinator.cs` and `SpeedMeasurement`; `Domain/BoundedTransfer.cs`.

The production transport converts cancellation into a failed observation with `CANCELED`; the coordinator only treats thrown cancellation specially. A cancelled attempt can therefore mark a node failed, unlike the injected transport in the cancellation regression. Socket/I/O/configuration failures may escape and abort the refresh instead of becoming an appropriate bounded outcome. One Boolean success and one sample still publish Healthy. The desktop uses only the first approved target.

The native transport reports zero payload bytes. The supposed daily allowance is a local counter reset on every `RunAsync` call, not a durable daily budget. The reported latency includes worker startup/readiness, then is stored in a median-latency field. A speed helper accepts an arbitrary Stream and checks only a stored health enum; it does not establish node/path/freshness provenance.

**Fix:** typed outcomes must distinguish cancellation, environment/target failure, unsupported configuration, core failure and candidate failure. Cancellation and infrastructure faults must not pollute candidate reliability. Require complete, context-bound admission evidence; preserve multiple samples and honest metric names. Enforce global/persistent budgets at the transport boundary, including failed attempts, and attach throughput to an eligible node and measurement path. Do not disable useful testing by permanently returning unknown; implement the positive proof.

**Acceptance:** RT05 and RT06. Run cancellation and failure injection against the actual worker, not only a callback. Multiple cycles/restarts share the defined byte budget. Fake, stale and direct-stream samples cannot become node speed or health evidence.

### R2-05 [P1] The freshness fix creates a 60-second/30-minute admission dead zone

**Paths:** `ProbeCoordinator.NeedsProbe`; `BrokerEngine.Select` and `IsCurrentlyEligible`; `Desktop/MainWindow.xaml.cs` `ConnectClick`; `Domain/Clock.cs` and `Eligibility.cs`.

A node with a successful assessment 65 seconds ago is too old to connect, but too fresh for `NeedsProbe`, which waits 30 minutes. Connect merely rejects it; ordinary refresh still skips it. Thus a healthy entry can remain visible as working while connection cannot obtain the required fresh proof. The standalone tests prove the rejection and the 30-minute recheck separately, not the combined usable behavior.

The freshness predicate also subtracts wall-clock timestamps directly; a future timestamp after clock rollback can remain unselected for recheck while eligibility correctly rejects it. Result timestamps use a supplied cycle timestamp rather than actual completion time. Policy revision/exclusions are not captured alongside epoch and digest for the whole attempt.

**Fix:** distinguish catalogue monitoring from on-demand pre-connect validation. Revalidate the selected candidate to the required age without discarding a useful cache or restarting a healthy session. Use conservative wall-clock persistence and monotonic durations, record completion times and bind results to the current configuration, epoch and policy. Implement actual NIC/sleep epoch observation, excluding self-owned changes.

**Acceptance:** RT07. At 65 seconds, Connect initiates fresh validation and proceeds or tries an eligible alternative. It does not wait 30 minutes or require restart. Clock rollback, sleep/resume, policy change and delayed old-epoch completion remain conservative and recoverable.

### R2-06 [P0] Windows still has no usable production composition

**Paths:** `Service/Program.cs`; `Broker/PipePeer.cs`, `ICoreController.cs`, `UnavailableNetworkGuard.cs`; service project and recovery entrypoint.

The service still uses the refusing core and unavailable guard. It remains a console-style lifetime, not a completed installed SCM service. Windows peers are now rejected because peer inspection only verifies Linux. This is preferable to accepting a fabricated identity, but makes Windows IPC unavailable by construction. The application does not become a VPN when moved to a Windows machine.

**Fix:** finish the real core supervisor, Windows service lifetime, peer SID/session validation, restrictive pipe/server identity policy, protected effect ownership and real protection implementation. Keep fail-closed adapters only for unsupported/unready environments. Do not remove the refusal and replace it with a success flag, run the entire UI as administrator, or widen access to all local users.

**Acceptance:** RT08 and the original Windows journeys. The installed unelevated UI controls a real pinned core through the authenticated broker. Real traffic traverses TUN, protection and owned-state recovery are observed, and two-user/remote/spoofed-server cases are rejected. Lack of Windows execution blocks verification, not all source implementation or unrelated Linux-testable fixes.

### R2-07 [P1] Independent catalogue owners can lose data or cannot exchange usable state

**Paths:** `Desktop/MainWindow.xaml.cs` `OpenCatalogue`; `Service/Program.cs`; `Persistence/SqliteCatalogue.cs` `Load`, `Commit`, `Save`; `BrokerEngine.Select`.

Desktop and broker each load an independent in-memory catalogue. Under the same Windows account/default path they can open the same SQLite file, yet each save deletes and rebuilds all rows from its own snapshot. An old instance can erase another instance's nodes/settings even though each individual transaction is atomic. Multiple desktop instances have the same problem. Under different service identities, paths/DPAPI scopes instead diverge and the broker has no matching node/consent. The desktop sends a NodeWire in Connect, but the broker selects only from its own catalogue and ignores that supplied node definition.

**Fix:** make the unelevated catalogue have one authoritative writer and a defined instance lifecycle. The broker receives a bounded typed runtime-set transaction with explicit context and independent validation, not authority over the user-writable full catalogue. Use optimistic durable revisions or another sound transaction design where multiple writers are genuinely required. Do not solve this by granting LocalSystem arbitrary parsing/fetching or trusting the entire writable SQLite file.

**Acceptance:** RT09. Open stores A and B on one test database; let A commit a node/consent and then let stale B update active state. A's data must survive or B must receive a conflict, not silently replace it. Also test real process startup order, two UI launches and the actual service identity.

### R2-08 [P1] UI startup, disconnect and response ordering are not an authoritative session workflow

**Paths:** `Desktop/MainWindow.xaml.cs` `OnLoaded`, `ConnectClick`, `SendAsync`, `ExitApplication`; `Application/UiSession.cs`; IPC contracts.

On load the UI checks refresh scheduling, not the broker's current snapshot. There is no complete heartbeat/event/resynchronization path. A restarted UI can remain Unknown while the broker owns a session; with no fresh local eligible node it may reject Connect before querying that session. The primary button remains disabled throughout a pending Connect RPC, so the newly concurrent IPC implementation does not make cancellation accessible from that button.

`FromSnapshot` accepts replies without boot/sequence/revision ordering checks. An old response can overwrite a newer state. Exit may close an initial Unknown session without obtaining broker state. Conversely repeated control errors have no explicit independent recovery/quit decision. A missing snapshot is collapsed into generic unreachability, hiding useful protocol errors such as PEER or REPLAY_WINDOW.

**Fix:** synchronize on startup/reconnect, correlate request and response identities, reject older state within a broker boot and handle new boots explicitly. Keep safety commands available while operations are pending. Implement explicit disconnect/recovery/quit semantics for unknown state without claiming verified restoration; do not trap the UI indefinitely either. Distinguish window-hide, user exit and UI crash.

**Acceptance:** RT10. Restart the UI during a real or controlled broker session, deliver replies out of order, lose IPC and request Exit/Disconnect during a pending Start. State labels and safety actions remain correct without consulting test-only engine references.

### R2-09 [P0] Subscription refresh is frozen to the initial upstream commit

**Paths:** `config/source-manifest.json`; `Fetch/ReviewedRegistry.cs` `ContentUrls`; `CatalogueCoordinator.DiscoverAsync`.

The tree URL and every content URL use `20c38289c29e4dba6b8f01ddd3273ec9ec169b46`. No product step advances that snapshot to the current source head. Repeated refreshes therefore cannot collect newly published configurations. Content URL generation uses `registry.PinnedCommit`, not a newly resolved discovery snapshot. The fixed family list also excludes newly discovered families. Mirror fallback only helps after discovery succeeds; unavailable GitHub discovery has no complete cached/mirror continuation.

**Fix:** separate a reviewed source/origin registry from a particular downloaded data snapshot. Resolve the current approved branch/head, then pin all artifacts of one cycle to that immutable snapshot. Cache discovery and preserve last-good data when head resolution fails. Discover relevant new families safely rather than treating the eight seed names as the eternal complete list. Keep origin/path restrictions; do not replace them with arbitrary remote URLs.

**Acceptance:** RT11. Two successive upstream commits with different nodes produce different successful snapshots; a mid-cycle head change cannot mix snapshots. New/removed families are correctly accounted for. A transient discovery outage can still use cached approved sources without destructive pruning.

### R2-10 [P1] A tree 304 stops discovery instead of reusing a valid tree

**Paths:** `CatalogueCoordinator.DiscoverAsync`; `Refresh/SourceLedger.cs`; UI `RefreshClick`.

Discovery remembers the tree ETag but does not persist/reuse its parsed tree. A 304 returns `Complete=false` and no work items; the UI exits before refreshing or validating candidates. Combined with the immutable source pin, this is a likely repeated-refresh failure, not merely an optimization gap.

**Fix:** persist versioned validated discovery state alongside its validator. On 304, reuse exactly that state and continue due work. If the state is unavailable/corrupt/incompatible, perform one bounded unconditional refetch. Do not label a genuine unchanged valid snapshot incomplete, and do not treat a 304 without local state as a successful empty tree.

**Acceptance:** RT12. First discovery returns 200+ETag, second returns 304, and source/candidate processing still occurs as scheduled. Repeat after restart and after deleting/corrupting just the cached tree.

### R2-11 [P1] ETags and success messages can advance without a committed valid snapshot

**Paths:** `CatalogueCoordinator.RefreshAsync`, `DownloadAsync`; `RefreshMerge.Ingest`; `SourceLedger.Remember` and `Save`.

The coordinator remembers new validators/hash/success time before parsing and committing the artifact. If the body is invalid, the old membership is preserved by the fixed helper, but the new ETag can still be retained. A later 304 is treated as reusable because some old node has that artifact id. This equates old membership with a cache of the new representation. It also reports PUBLISHED whenever the fetch was not a 304, regardless of the ingestion rejection. The helper's balanced/publication outcome is not reflected in overall source failure.

**Fix:** distinguish fetched, parsed, staged, committed and checked states. Advance last-success/validators only with a durable valid representation or a separately tracked rejected version that is never confused with reusable good state. Store explicit snapshot metadata, including recognized empty snapshots, not node-presence heuristics. Ledger and catalogue publication need a consistent recoverable transaction boundary. Preserve invalid-body diagnostics without storing live credentials in the repository.

**Acceptance:** RT13. Seed good version A, fetch malformed version B with a new ETag, restart, then receive 304. The result cannot be PUBLISHED/unchanged-good-B. Verify parse rejection, missing cache, valid empty snapshots and a persistence failure between staging and publication.

### R2-12 [P0] Automatic refresh is a one-time startup check, not a recurring feature

**Paths:** `MainWindow.xaml.cs` `OnLoaded`, `RefreshClick`; `RefreshScheduleGate`; source ledger.

The UI evaluates whether refresh is due once during Loaded. There is no recurring scheduler attached to the tray/application lifetime. OnLoaded also starts downloading/probing before disclosure is accepted. A maximum success timestamp across ledger entries, including discovery/partial successes, is not an accurate schedule for every failed or stale source.

**Fix:** implement the required recurring scheduler in the unelevated application lifetime, with coalesced wakeups, explicit consent policy, per-source due/backoff state, manual override and sleep/resume handling. Decide and explain which metadata requests may precede consent; do not silently start third-party candidate probes before the required disclosure. Keep cached operation possible when offline.

**Acceptance:** RT14. With controlled time, leave one application instance open across multiple intervals and observe distinct actual cycles without relaunch or button clicks. Test consent refusal/acceptance, tray hiding, sleep and partial-source failure.

### R2-13 [P1] Repeated refreshes can overlap and publish stale work after cancellation

**Paths:** UI `RefreshClick`, `OnClosing`; `CatalogueCoordinator.RefreshAsync`; `SourceLedger`.

Refresh cancels and disposes the previous CancellationTokenSource but does not join its work before launching a new coordinator. Downloads already completed can still be ingested because publication is not fenced to the current refresh generation. Each invocation creates its own concurrency limiter. The old run can overwrite ledger/status or database state after the new run, and Exit can dispose the catalogue while refresh still uses it. Cancellation outcomes are not consistently reflected in the final UI message. Retry-After information is not a complete persistent scheduling policy.

**Fix:** retain task ownership, cancel and join/coalesce or safely supersede the old generation, and reject obsolete publications and UI updates. Use a shared bounded scheduler, not a new independent resource allowance per click. Terminate/join work before disposing stores. Apply bounded retry and persistent per-origin/source backoff to manual as well as scheduled work, with an explicit documented override where appropriate.

**Acceptance:** RT15. Arrange refresh A with delayed completion, start and finish B, then release A. B remains authoritative. Repeated clicks do not multiply concurrency. Exit/cancel during download, parse, commit and probe has deterministic cleanup and status.

### R2-14 [P1] Per-file bounds do not enforce aggregate download, candidate or retention limits

**Paths:** `CatalogueCoordinator.RefreshAsync`, `DownloadAsync`; `ProductLimits`; `RefreshMerge`; catalogue retention paths.

Task.WhenAll retains all downloaded bodies before ingestion. Three simultaneous downloads do not cap total retained payload, and the 64 MiB cycle bound is not enforced. Candidate limits are applied within individual imports rather than one whole cycle. Capacity and retention helpers are not connected into a complete admission/expiry policy. Repeated cycles and representations can accumulate work/data despite the constants.

**Fix:** a bounded producer/consumer pipeline must account for aggregate encoded/decoded bytes, queued bodies, parse records, candidate attempts and retained state. Process useful verified data incrementally without restarting the active tunnel. Apply explicit admission/eviction/backoff policies while preserving active nodes, favorites and last-good source state. Do not delete owner data just to satisfy a numeric limit; expose an intentional cap outcome.

**Acceptance:** RT16. Many individually valid near-limit artifacts stop at the cycle budget without an unbounded in-memory batch. Test overlapping representations, failed attempts, repeated cycles and full retention capacity. Report observed resource use rather than assuming constants enforce it.

### R2-15 [P1] The idempotency fix permanently exhausts control after 256 completed requests

**Paths:** `Application/IpcDispatcher.cs`; `Domain/ProductLimits.cs`.

Every completed request, including GetSnapshot, is retained in `_results`. Once 256 entries exist, any new id is rejected with REPLAY_WINDOW, including Disconnect and RecoverOwned. No normal lease/rotation path frees capacity. This changes bounded memory into a hard service lifetime limit.

Reusing an id with different operation/payload returns the previous result without checking a request fingerprint. In-flight requests are not counted in the cap and duplicate waits block synchronously. Exceptions remove the record, allowing an operation with partial effects to be retried as new. ResetOwner lacks fencing against old in-flight completion repopulating a new owner's cache.

**Fix:** separate read requests from mutation idempotency; use bounded lease/window/tombstone semantics that preserve safety without permanently blocking control. Bind the id to immutable operation content and context. Reserve availability for safety commands. Use asynchronous bounded in-flight handling and record terminal/uncertain mutation outcomes. Old leases and old completions cannot reenter a new authority epoch.

**Acceptance:** RT17. More than 1,000 snapshots followed by legitimate mutations and Disconnect still work. Same-id/different-content requests fail explicitly. Test partial-effect exceptions, in-flight saturation and owner reset. Evicting everything and allowing old Connect replay is not an acceptable shortcut.

### R2-16 [P1] Pipe saturation can stop the listener and completed session tasks accumulate

**Paths:** `Broker/LocalIpcServer.cs`; `Service/Program.cs`; `ProductLimits`.

The configured pipe instance limit is four. The accept loop treats repeated pipe-construction IOException as a fatal fault after three attempts separated by 50 ms. Busy instance capacity can therefore be treated like permanent inability to create the endpoint, before the two-second client read deadline frees a slot. The platform-specific instance behavior needs Windows execution, but the erroneous failure/backpressure conflation is visible in the loop.

`_sessions` retains every completed task. Dispose captures streams/tasks before waiting for acceptance to stop, so work started around that boundary is not reliably included. The service waits indefinitely on its own stop token and does not supervise `server.Completion`, allowing a dead listener to coexist with a live-looking service process. Individual handlers still use synchronous waits around async engine work.

**Fix:** distinguish capacity backpressure from unrecoverable creation failure; enforce a bounded active-session set and prune completed work. Stop acceptance before joining all owned sessions, bound shutdown, and surface terminal listener failure to the host. Keep authentication and resource limits active for malformed clients. Do not obtain responsiveness by permitting uncontrolled concurrent state mutations.

**Acceptance:** RT18. Occupy all four slots with partial clients, release them and verify recovery without service restart; test equivalent injected capacity pressure off Windows. Run sustained valid/malformed traffic and show bounded tasks/handles. Shutdown and listener failure leave no unobserved operations.

### R2-17 [P1] A late abandoned Start still corrupts a newer broker session

**Paths:** `BrokerEngine.ConnectAsync`, `DisconnectAsync`, `Recover`; `ICoreController`; `PackageCTests`.

Operation ids now scope the controller's Stop call, but the broker continuation itself is not fully scoped. An old abandoned Connect unconditionally clears `_coreRunning` and calls `SetActiveNode(null)`. If session B starts after A was disconnected but before A's late Start result returns, A can damage B's state even when the controller correctly ignores a stop for A.

Disconnect drops the operation id before cleanup finishes; retry after cleanup failure may target the incremented generation rather than the still-owned old effects/process. Cleanup exceptions and cancelled tokens do not have a complete transition/retry contract. Recover checks that no active session exists, then leaves the lock before running recovery, allowing a concurrent Connect to invalidate that decision.

**Fix:** use a serialized authority/state actor with immutable owned operation/process/effect handles. Every completion and cleanup step checks its own boot/lease/attempt/configuration context before mutating shared state. Retain cleanup ownership until confirmed completion. Persist uncertain effects and make critical bounded cleanup independent of an already-cancelled UI request. Represent recovery as an exclusive lifecycle transition, not a check followed by unguarded effects.

**Acceptance:** RT19 and RT20. Delay A's Start result, fully disconnect A, connect and confirm B, then release A. B's state, active flag, guard and process survive. Inject failed Stop/Disarm and retry; the same old owned resources are reconciled. Concurrent Recover/Connect cannot remove a newly armed guard.

### R2-18 [P1] Failover still has false process state, stale proof and no complete guarded retry

**Paths:** `BrokerEngine.ReportHealthAsync`, `ConfirmProduction`; `Domain/Failover.cs`; `TunnelReducer`.

CoreExit at the switch cap now moves to Reconnecting, but `_coreRunning` is not cleared and the old operation context remains valid. A delayed positive confirmation for that context can move Reconnecting back to Connected without a new live core. When the cooldown expires, the switch branch still requires Connected, so a held Reconnecting/Blocked session has no complete recovery path. Confirmed health failure at the cap can still be treated as an unchanged connected session.

Starting failover sets a new operation id while the old phase remains Connected. Another health message can enter the busy path and block/abandon that switch. HealthPayload is not bound to the measured node/attempt/core instance; assigning CommandGeneration from the current state does not validate a stale health event. The old and staged core instances also need distinct ownership during replacement.

**Fix:** make physical process exit terminal for that instance's proof, preserve protection and report process state accurately. Keep guarded retries reachable from outage states, including pinned/cooldown/no-server cases, under the user's policy. Bind health events to their producer/context, serialize transition decisions and commit replacement only after the appropriate proof. Do not accept arbitrary client failure counters as authoritative network observations.

**Acceptance:** RT21. Core death at/after the cap, a late positive callback, cooldown expiry, duplicate health events during staging and pinned recovery all behave correctly. No verified-dead instance remains or becomes Connected. A target outage alone still does not unnecessarily flap a genuinely healthy session.

### R2-19 [P0] Protection and durable recovery remain unfinished; prior journal defects persist

**Paths:** `UnavailableNetworkGuard`; `BrokerEngine.DisconnectAsync`; `Persistence/EffectJournal.cs`; `Recovery/Program.cs`; recovery/admin scripts.

The prior audit's real WFP/route/DNS ownership requirements remain open. Disconnect still disarms before journal recovery. Corrupt-journal uncertainty is not persisted across a new process opening the replacement empty journal; missing journal metadata is not proof that no owned OS effects exist. Sidecar/handle-safe quarantine and OS reconciliation are still required. These are not resolved by a source-level `ProtectionArmed` Boolean.

**Fix:** implement protected durable effect records and recovery-unknown state, reconcile actual owned OS objects and preserve unrelated changes. Restore owned network effects before releasing protection. Decide and test broker-crash/filter-lifetime behavior rather than assuming dynamic filters survive. An explicit unprotected choice is a separate owner policy, not an implicit error fallback.

**Acceptance:** RT22 plus original crash/restore journeys. Corrupt or remove a journal while owned effects exist, run recovery, restart and run it again. It must not become clean merely because an empty replacement database now exists. Verify packet behavior and only-owned-effect cleanup on Windows.

### R2-20 [P1] Emitter validation and protocol mapping still disagree

**Paths:** `Core/MihomoProfileGenerator.cs` `AcceptedSecurity`, `AppendProxy`, `AppendTransport`; `Import/ClashProxyParser.cs`; unchanged `XrayOutboundParser.cs`; wire factory/contracts.

There are at least three remaining classes of mismatch:

1. `AcceptedSecurity` trims/lowercases the value, but TLS emission later compares the original string to lowercase literals. A direct typed NodeWire with `Security="TLS"` can pass that check without emitting `tls: true`. Normalized URI import fixes one path, not the generator's own contract. This boundary defect is not a claim that the current normalized UI path emits uppercase values.
2. H2 is routed through `AppendHttp` and emits `http-opts`, whereas the pinned core has separate `h2-opts`. A websocket alias can be accepted by transport handling without canonicalizing the serialized network name. Unknown transport values without option fields can fall through. Header/grpc/host combinations need explicit support or rejection, not silent omission.
3. Nested Clash option fields and case-equivalent conflicting keys are not fully checked. Xray still selects the first vnext/user, misses normal server-array structures and drops significant TLS/WS/gRPC fields. A correct URI test does not establish coverage of client-specific exports.

**Fix:** validate and serialize one canonical, protocol-specific typed model at every boundary. Preserve supported semantics exactly and explicitly classify unsupported combinations. Add native config and controlled handshake tests, not just substring assertions. Security normalization must be identical for validation and emission. Do not globally loosen security to boost import counts.

**Acceptance:** RT23 and RT24. Direct mixed-case/whitespace security values either normalize securely or fail. H2 with a nondefault path reaches the intended controlled peer. Nested unknown/conflicting options are rejected or preserved correctly. Multi-record Xray exports are accounted for without silent first-record truncation. References P3 and P4.

### R2-21 [P1] Identity still merges different emitted UDP behavior, and migration can retag unproven health

**Paths:** `Domain/NodeSemantics.cs` `CanonicalJson`, `NormalizeLegacy`; `Catalogue.ReconcileStoredDigest`; `SqliteCatalogue.Load`; generator; `AuditRegressionTests.At22OpaqueBytesSurviveAndMigrationDoesNotInventHealth`.

Canonical JSON omits both null and true UDP, but the emitter writes `udp: true` only for explicit true. These identities can therefore merge while their generated profiles differ. The core's documented default for the relevant TCP-based proxies is false. Since an existing catalogue node retains its first semantics, import order can change effective UDP behavior under the same digest. Reference P2; the pinned VMess option structure is also listed in P4.

Migration uses equality to a recomputed old-version hash as `sameBytes` and can carry Healthy to the new digest even where old and new normalization change an opaque path/password. The new test deliberately seeds a current spaced-path record, rewrites its digest to the old algorithm and expects health to survive. That does not prove historical and current effective connections are identical. Ordinary old records already normalized by the old writer may not trigger this exact fixture; do not claim all existing users necessarily lose or inherit bad data.

**Fix:** identity must describe effective emitted semantics, including explicit defaults. A canonicalizer upgrade and a renderer/core behavior change need compatible evidence-versioning. Retain health only after proving equivalent effective configuration and measurement context; otherwise preserve user metadata but require a new check. Use actual legacy serialized fixtures and account for collisions, not only new fixtures with edited hashes.

**Acceptance:** RT25. Null/true UDP either generate identical intended behavior or have distinct identities. Import order cannot change it. Opaque values and historical-version fixtures migrate without assigning old proof to changed semantics; favorites/provenance survive appropriately.

### R2-22 [P1] Subscription wrapper limits and input schemas still reject valid feeds or lose semantics

**Paths:** `SubscriptionImporter.ImportLines`, `Base64Text.TryDecode`; `ClashProxyParser`; `GithubTreeParser`; source ledger/registry loaders.

The line-size check happens before recognition of a whole Base64 subscription. A valid wrapper larger than 64 KiB, containing individually small valid records and remaining below the artifact limit, is classified as an oversized line. Decoded CRLF handling is inconsistent: the Base64 helper disallows carriage return and the decoded inner document is not passed through the same initial normalization. JSON `proxies` containers still use the Xray parser rather than a format-appropriate schema.

Input validation must also consistently check nested value types, case-equivalent conflicts, registry schema versions, malformed Unicode and loaded ledger entries. For example, a syntactically valid ledger array containing null is not handled by catching only JsonException before dereferencing entries.

**Fix:** apply separate encoded-wrapper, decoded-document and per-record limits in the correct order. Normalize supported line endings without changing credentials. Distinguish URI/Base64/Clash/Xray schemas explicitly and retain safe record-level containment. Use versioned bounded loaders and recoverable diagnostics for corrupted local state. Do not hide unsupported fields as successful empty data.

**Acceptance:** RT26. Large valid Base64 with small records, Base64 of CRLF lists, malformed encodings, null ledger entries, nested type errors and format-specific JSON are tested. Limits remain effective against expansion/depth/record abuse.

### R2-23 [P1] DNS/IPv6 and resolved destination safety still need implementation and path proof

**Paths:** `Domain/NodeSemantics.cs` `EndpointSafety`; generated DNS block; subscription fetch and probe dial paths; broker protection backend.

Literal address checks do not validate every resolved A/AAAA result and bind the approved decision to the actual connection. The newly usable non-TUN worker makes this omission more relevant, not less. Internal core DNS resolver traffic is not proven to follow the selected protected outbound simply because application port-53 rules exist. Bootstrap resolution, user DNS and candidate measurements have different trust/path requirements.

**Fix:** complete the first audit's resolution/rebinding policy, separate bootstrap and user DNS, and implement the supported IPv6 route/block policy with narrow explicit exceptions. Ensure the physical path used by candidate probes cannot silently use another VPN. Do not turn private-address fixture exceptions into a production arbitrary-endpoint allowance.

**Acceptance:** RT04 and RT27. Controlled mixed-address/rebinding fixtures are rejected before unintended dialing. Windows packet captures with positive/negative controls show the intended DNS/IPv6/bootstrap paths during connect, failover, crash and recovery. No packet-level success is asserted by this source audit.

### R2-24 [P1] Owner policy is not applied consistently from UI through probe and failover

**Paths:** UI settings/connect code; `NonTunCoreProbeTransport.BuildProbeYaml`; `ProbeCoordinator.NeedsProbe`; `BrokerEngine`; `ProductSettings`; eligibility and country helpers.

The probe builder hardcodes `AllowInsecureCertificates=false`, even when an explicit owner setting permits such a candidate, leading to an exception rather than a consistent policy outcome. Stored policy reasons and healthy caches are not reevaluated for every setting change. Excluded/disabled-family nodes can still be queued for probing. The guard is always requested with protection required true regardless of the displayed toggle. Connect and failover do not consistently latch the same LAN/security/source policy. The UI's first eligible node is not necessarily the configured ranking/country choice.

**Fix:** create one versioned effective policy, validate settings comprehensively and apply it at scheduling, admission, selection, staging and actual use. Security changes invalidate or reclassify affected proof. Support the specified explicit unprotected/insecure modes only with accurate warnings and semantics; never silently ignore a toggle or weaken target HTTPS validation. Keep disabled-source and exclusion semantics clear. Use canonical country codes and preserve conflicts separately from measured geography.

**Acceptance:** RT28. Change each setting while a probe/connect/failover is pending. Stale work cannot cross policy boundaries, strict country/source choices remain enforced and owner-approved insecure proxy TLS never disables authentication of the HTTPS test target.

### R2-25 [P2] Single-instance transactional safety did not fix quadratic persistence and rendering

**Paths:** `SqliteCatalogue.Save`; `Catalogue.Copy`/mutable nodes; `Application/UiSession.cs` `CataloguePresentation.Servers`, `Match`.

Every assessment still clones and rewrites the entire catalogue and re-protects every secret. The working-list predicate invokes a complete `Eligible` pass for each candidate, repeatedly computing eligibility/digests. Mutable node collections are still returned to callers. The result is an unmeasured scaling risk and an unclear authority boundary, not evidence that a particular machine has already hit a measured performance limit.

**Fix:** targeted/batched transactional updates, immutable published snapshots, appropriate indices and one eligibility calculation per presentation revision. Finish retention/admission policies and virtualize large views. Keep secrets protected without re-encrypting all rows for one latency update. Preserve atomicity when optimizing.

**Acceptance:** RT29. At the specified catalogue size, measure SQL operations, protector calls, memory and render responsiveness. One assessment must not rewrite every credential, and one working-list render must not perform a full catalogue scan for every row. Cross-instance correctness from RT09 remains mandatory.

### R2-26 [P2] The desktop still lacks required usable controls and honest contextual metrics

**Paths:** `Desktop/MainWindow.xaml`, `MainWindow.xaml.cs`, `App.xaml`; `CataloguePresentation`; ranking/country/standby functions.

Static text has partly become catalogue text, but manual server selection, functional favorites/exclusions, complete subscription statuses, native throughput testing, live traffic display, bounded/diverse standbys and ranking-policy integration are not complete. A formatted Healthy enum in all/favorite views ignores expiry/epoch/policy and has no adequate proof timestamp. Theme/DPI/keyboard/tray behaviors remain unexecuted. Several async event paths do not contain realistic parsing/SQLite/configuration errors, and closing can race with unfinished refresh work.

**Fix:** finish the original simple four-view design backed by real commands and revisioned data. Show active session separately from eligible/healthy/stale/disabled states, with last-check time and failure reason. Latency, benchmark throughput and current traffic are separate measurements. Add appropriate UI-boundary error handling without swallowing data loss or inventing success. Keep cancellation and recovery accessible; do not add more decorative controls before existing ones work.

**Acceptance:** RT30. Exercise a real click/keyboard matrix, themes and representative DPI levels, including fresh install, no core, no working nodes, rejected source, cancelled update, active session, protected outage and broker loss. Every enabled control must perform its advertised operation.

### R2-27 [P1] The new tests often validate only one helper or encode an incorrect protocol

**Paths:** `tests/AutoVpn.UnitTests/AuditRegressionTests.cs`, `PackageBTests.cs`, `PackageCTests.cs`, `BehaviorTests.cs`; `.github/workflows/ci.yml`; test scripts.

The actual HTTPS fetcher tests are useful. In contrast, the SOCKS test's plaintext responder cannot prove HTTPS, the healthy-admission test runs with no core, the cancellation fake throws while the production transport returns a failure object, and the old-attempt test calls a defensive test-double Stop rather than releasing a stale broker continuation after session B. The pre-connect and expiry tests never exercise obtaining a fresh proof. Registry tests assert the fixed initial commit in every URL. Migration tests treat an edited old digest as sufficient proof.

Windows peer rejection also means the new real-pipe positive tests cannot simply be assumed to pass on Windows. CI remains Ubuntu-only. A skipped native check is now honest, but there is still no positive production admission/core/installer journey.

**Fix:** write failing end-to-end and adversarial regressions for these interactions, then fix production code. Do not preserve a false assertion just because it is already green. Separate portable unit, real native non-TUN, OS-specific pipe/UI and Windows-admin suites with explicit prerequisites and results. Add nondestructive Windows CI where available; run destructive network acceptance only in an authorized isolated lab. Report the exact tested layer.

**Acceptance:** RT31. The new regression pack fails meaningfully against the audited snapshot and passes after the fixes. Include at least one actual controlled TLS-through-pinned-core positive and the matching broken-candidate negative. Correct skips and platform-specific expectations instead of disabling coverage.

### R2-28 [P0 release / P2 metadata] Installer, provenance, component licensing and closure reports are still incomplete

**Paths:** `scripts/package.ps1`, `verify-release.ps1`, `test-windows-admin.ps1`; `config/core-manifest.json`; `THIRD_PARTY_NOTICES.md`; implementation/status/handoff/architecture and evidence documents.

There is no installer or published release for the revised code. The old local archive predates packages A through C. Placeholder release/admin scripts remain placeholders. The pinned Mihomo component is still labeled MIT even though the previously fetched LICENSE at that exact commit is the GNU GPL v3 text. No full transitive SBOM/asset/provenance reconciliation has been supplied. Some architecture and status prose describes an older wiring state, and the F32 closure text paraphrases the first audit's licensing statement inaccurately.

**Fix:** implement packaging/service registration/upgrade/uninstall and a real verifier, build from explicit clean source inputs and publish retrievable artifacts with checksums. Correct notices against actual pinned components; derive precise SPDX qualifications from upstream notices, not guesswork from generic license text. Generate and review the transitive SBOM and dependency/security scan results. Signing unavailability must be disclosed, not fabricated or bypassed. Reconcile documentation with actual production paths and evidence layers.

**Acceptance:** RT32. Another clean supported Windows machine can retrieve, verify, install, use, upgrade and uninstall the exact artifact safely. Its manifest identifies the source commit/tree, dependencies/core/driver and executed acceptance results. No old binary is presented as containing these fixes. Metadata corrections and reproducible scripts must not wait for Windows availability.

## 5. Required regression suite

The following are specifications for tests to implement and execute, not results claimed by this auditor. Use synthetic credentials, deterministic clocks, controlled resolvers/peers and bounded task lifetimes. A public IP printed in a parser fixture is not permission to probe it. The original AT01 through AT33 remain binding where applicable.

| Test | Required evidence | Findings |
|---|---|---|
| RT01 | Real TLS over candidate SOCKS; reject plaintext 204, fake status, invalid/wrong-host certificates and unexpected body/redirect. | R2-01 |
| RT02 | Ports/listeners/controller belong to the intended worker; collision, unrelated listener and unauthorized access fail. | R2-02 |
| RT03 | Chatty/hung/exited child, cancellation, output limits, awaited kill and credential-temp cleanup; no stale reaper kills a new instance. | R2-03 |
| RT04 | Working production A cannot make broken candidate B pass; inverse/concurrent cases and intended physical path verified. | R2-03, R2-23 |
| RT05 | Actual transport cancellation/IO/configuration outcomes do not wrongly mark candidates failed or abort unrelated work. | R2-04 |
| RT06 | Two-target admission, sample provenance and byte budgets persist across cycles/restarts; arbitrary Stream is not node throughput. | R2-04 |
| RT07 | A 65-second-old healthy candidate gets on-demand admission; clock/epoch/policy changes reject obsolete evidence. | R2-05 |
| RT08 | Installed unelevated UI, actual Windows broker identity, real core and protected TUN journey; second-user/remote denial. | R2-06 |
| RT09 | Two stale catalogue handles/processes cannot erase each other's committed nodes/settings; correct bounded broker handoff. | R2-07 |
| RT10 | UI startup/resync, pending Connect cancellation, old replies, broker restart and explicit Exit use authoritative state. | R2-08 |
| RT11 | Source head advances between cycles; each cycle remains internally pinned; new/removed families and discovery fallback work. | R2-09 |
| RT12 | Tree 200 then 304, restart and missing/corrupt tree cache still produce correct due work or one unconditional refetch. | R2-10 |
| RT13 | Valid A then malformed B+ETag then 304 does not invent successful B publication; atomic ledger/snapshot failure handling. | R2-11 |
| RT14 | One long-lived tray instance executes multiple scheduled cycles under consent/per-source due policy. | R2-12 |
| RT15 | Cancel/supersede A, finish B, release A; no stale publication, multiplied resource budget or use-after-dispose. | R2-13 |
| RT16 | Whole-cycle byte/candidate/queue/capacity limits with many valid artifacts and repeated updates; owner data preserved. | R2-14 |
| RT17 | More than 1,000 reads, mutation retries, conflicting id reuse, partial effects and lease reset never permanently disable Disconnect. | R2-15 |
| RT18 | Four saturated pipe instances recover; session tasks/handles stay bounded; listener failure and shutdown are supervised. | R2-16 |
| RT19 | Late A Start completion after B is connected cannot clear B's running/active/guard state. | R2-17 |
| RT20 | Failed cleanup retries retain correct old handles; concurrent Recover/Connect cannot remove new effects. | R2-17, R2-19 |
| RT21 | Core exit at switch cap, late proof, cooldown expiry, pinned retry and duplicate health events never produce false Connected. | R2-18 |
| RT22 | Corrupt/missing journal plus restart stays recovery-unknown until actual owned OS effects are reconciled. | R2-19 |
| RT23 | Direct uppercase/whitespace typed security is secure or rejected; H2 uses actual H2 options and controlled peer path. | R2-20 |
| RT24 | Per-protocol native validation and relevant handshakes; nested conflicts and Xray multiple records are handled explicitly. | R2-20 |
| RT25 | UDP defaults agree with identity/emission; real legacy fixtures migrate metadata without unproven health reuse. | R2-21 |
| RT26 | Large Base64 wrapper, CRLF inner list, malformed encoding/nesting/ledger nulls and proper schema dispatch. | R2-22 |
| RT27 | Resolved-address/rebinding and DNS/IPv6/bootstrap positive/negative path controls, including Windows packet evidence. | R2-23 |
| RT28 | Policy changes during pending work honor source/country/security/LAN/protection choices without stale result acceptance. | R2-24 |
| RT29 | Catalogue-scale measurements: bounded writes/protector calls, one eligibility pass per revision, safe retention. | R2-25 |
| RT30 | Real functional Russian UI, themes/DPI/keyboard/tray and loading/empty/error/protected states. | R2-26 |
| RT31 | Portable/native/Windows suites separated; each claimed closure is linked to the actual production-boundary regression. | R2-27 |
| RT32 | Current retrievable installer/artifact, clean-source manifest, component notices/SBOM, install/upgrade/recovery/uninstall. | R2-28 |

### 5.1 High-value reproduction recipes

These recipes are deliberately short enough to become deterministic regressions. Do not report them as executed until they actually run.

**Request-cap failure:** instantiate the real dispatcher, send 256 distinct valid GetSnapshot requests through a same-owner handler, then a new Disconnect at the current revision. The audited source rejects the latter with REPLAY_WINDOW. The fixed behavior must preserve safe mutation replay semantics while allowing continued operation.

**Stale-writer failure:** open catalogue A and B on the same disposable SQLite path with the test protector before either imports nodes. Import and persist a synthetic node through A. Invoke `B.SetActiveNode(null)`, then reopen C. The audited full rewrite can erase A's row. Test both a stale empty writer and one holding an older nonempty snapshot.

**Frozen refresh and tree 304:** serve valid tree/content at snapshot A, then expose newer snapshot B. Assert the production coordinator resolves B, not merely reuses the initial config commit. Separately return 200+ETag then 304 for discovery. The second invocation must still produce work from a validated cached tree.

**ETag-before-commit failure:** commit source A, then return invalid source B with ETag B, followed by 304 when B is requested. Assert the ledger never describes old A as a usable representation of B, and the UI does not say PUBLISHED. Inject a database exception before committing the new representation as a separate case.

**Admission dead zone:** set successful health to now minus 65 seconds. Both the working-list and the Connect command must be exercised. The correct resolution is a bounded fresh candidate check, not changing the 60-second policy to 30 minutes or deleting the good cached node.

**Late Start failure:** a controllable core delays A's Start return; disconnect A, connect and confirm B, then release A's result. Assert B's physical instance, `CoreRunning`, active catalogue flag, operation identity and protection remain intact. Checking only `CountingCore.StopAsync` directly does not cover the broker continuation.

**Wrong TLS proof:** the SOCKS peer returns a plaintext HTTP status without contacting the target. The current helper accepts the status; the corrected HTTPS test must reject it. Add a real TLS target with a trusted test certificate and a separate invalid/wrong-host target. Do not put an unconditional certificate callback in production.

**Identity/emitter equality:** construct two otherwise identical supported TCP-proxy nodes with UDP null and true. Compare both digests and generated profiles/effective native options. The invariant is that equal identity and evidence context mean equal effective connection behavior, not simply equal display strings.

## 6. Execution order and completion contract

### Slice A: repair deterministic safety/correctness first

Add failing regressions for RT01, RT07, RT09, RT12, RT13, RT17, RT19, RT21, RT23 and RT25, then fix their production paths. Include the native worker cancellation/output lifecycle tests as that path is repaired. These items are not blocked by lacking Windows TUN. Correct wrong protocol/test assumptions rather than appending more happy-path tests.

### Slice B: deliver one actual unelevated catalogue journey

One fresh application-side catalogue must progress through consent, current-head discovery, bounded fetch, valid snapshot commit, actual authenticated candidate test and honest working-list publication. Run it with a pinned core against controlled endpoints, with a matching broken-candidate negative control. Complete recurring scheduling, source 304 reuse, cancellation/supersession and restart persistence. Do not move this broad untrusted ingestion workflow into the privileged broker.

### Slice C: connect the real broker boundary and lifecycle

Complete typed bounded runtime-set acceptance, service/owner identity, ordered state authority, owned process/effect handles and responsive safety commands. Neither a writable shared catalogue nor unrestricted parallel handlers is an authorization design. Demonstrate UI startup/resync and the actual IPC cancellation path without reaching into `engine.Snapshot()` from the UI test driver to conceal missing commands.

### Slice D: finish and verify Windows behavior

Implement the real Windows core/protection/service/installer paths, keeping unsupported-host refusal. On an authorized disposable Windows environment, execute TUN/DNS/IPv6/path-isolation, crash, recovery, two-user, sleep/NIC and UI acceptance with an independent recovery path. Do not change the network carrying your only control connection. A missing Windows environment permits `IMPLEMENTED_NOT_VALIDATED` or a precise external blocker, not claims that a production feature is complete because its adapter refuses.

### Slice E: package the current result and reconcile all evidence

Build and publish from explicit clean source inputs. Supply a retrievable Windows artifact, exact hashes/versions, actual notices/SBOM and observed acceptance results. Update architecture, user/recovery instructions, source/protocol coverage and handoff. The existing old local archive cannot stand in for this output.

### Required tracking

Create `docs/AUDIT_ROUND2_STATUS.md` with one entry for every R2-01 through R2-28, plus the carried original finding ids. Each entry includes:

```text
Finding | Original finding ids | Status | Fix commit
Production paths | RT/AT tests | Executed command and environment
Evidence/run/artifact | Remaining limitation or precise external blocker
```

Allowed states: `OPEN`, `IN_PROGRESS`, `IMPLEMENTED_NOT_VALIDATED`, `BLOCKED`, `VERIFIED`. A broad finding is VERIFIED only when all its stated closure criteria pass at the required layer. Label accepted helper sub-properties separately. Do not replace unavailable native/Windows evidence with a source assertion, mocked guard, direct reducer call or larger unit-test count.

For disagreements, preserve the finding and attach a concrete counterexample/test showing why it does not apply to the current source. Do not quietly edit this audit away. Keep all owner communication in Russian. Make coherent tested commits, push to the intended branch and verify the remote. Preserve owner changes; never force-push or commit live subscription credentials, runtime YAML, databases or private captures.

Work in finite, recoverable slices and close child tasks/processes. Do not end an otherwise feasible implementation pass after filling out a status table. If truly blocked, finish independent work, push a recoverable checkpoint and identify the exact missing capability. Do not restart the entire architecture, expand the product scope or weaken security/freshness/acceptance requirements to make tests green.

## 7. Primary references

Repository-relative paths and symbols in this report refer to the audited commit, not a moving branch. These references support the specific external API/core-schema statements; they do not replace product runtime evidence.

- **P0, audited repository:** https://github.com/alinescafs3mp-afk/vpn/tree/560e5df0fb6c63b8a7daed55c97207b8b69be65a
- **P0, existing CI run:** https://github.com/alinescafs3mp-afk/vpn/actions/runs/37066620339
- **P1, target-side TLS authentication and hostname requirements:** https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream.authenticateasclientasync?view=net-10.0
- **P2, Mihomo common proxy fields, UDP default and interface binding:** https://wiki.metacubex.one/en/config/proxies/
- **P3, distinct HTTP/H2/WS/gRPC transport options:** https://wiki.metacubex.one/en/config/proxies/transport/
- **P4, exact pinned core VMess schema:** https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/adapter/outbound/vmess.go
- **P5, named-pipe instance construction and limits:** https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstream.-ctor?view=net-10.0
- **P6, pinned Mihomo LICENSE reviewed in round 1:** https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/LICENSE

Live documentation may change. Recheck the exact pinned engine/SDK when implementing and preserve the applicable evidence. No absence of a finding here is a security certification.

**End of round-2 directive.**
