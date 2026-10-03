# AutoVPN: independent hard audit and completion directive, round 4

**Recipient:** Grok, implementation lead.  
**Owner communication:** Russian only. Engineering identifiers and this directive are English.  
**Repository:** `alinescafs3mp-afk/vpn`  
**Audit date:** 2026-10-03. Execution timestamps are UTC.  
**Audited production commit:** `12c4b920a478a6f66945d7d7512d9ada319a559c`  
**Audited production tree:** `a7f6482ede43f7c77a5173ca3b9752eb0b844b61`  
**Implementation parent:** `c76764f69e9be4637abb670dd39a17cded7fd81f`  
**Independent audit branch:** `audit/round4-12c4b92-independent`  
**Canonical regression commit:** `a5f110b3c2f326e529f7d866ad9415062e6b64f5`  
**Additional workload/UI/native-profile commit:** `8361f6340adda5e31c27482e92b0fee4fe5cf4b0`  
**Verdict:** **V1 NOT ACCEPTED. Some round-3 defects are genuinely fixed, but important integration defects remain and the Windows VPN product is still incomplete.**

This is an implementation and remediation assignment, not a request for another status-only audit. Read this document, `ROUND4_EVIDENCE_2026-10-03.json`, all three previous audit directives, and `docs/IMPLEMENTATION_DIRECTIVE.md`. The original specification remains the acceptance contract. The new counterexamples are a regression floor, not a reduced product scope.

Do not rewrite the stack, weaken certificate verification, fabricate measurements, remove adverse assertions, or move broad untrusted subscription parsing into a privileged service. Keep the existing C#/.NET/WPF/Mihomo design and finish its real user journeys. Do not edit this audit baseline to make findings disappear. Document any justified correction with executable evidence.

## 1. Evidence and scope

### 1.1 Exact source and historical screen

The complete production snapshot contains **123 tracked files, 1,279,891 bytes, 25,546 text lines, and 63 C# files with 18,904 lines**. Every exported file's Git blob hash was recomputed. The root Git tree was independently reconstructed from names, modes and bytes and matched the tree above. Source inventory and verification records are included in the evidence bundle.

The review covers entrypoints, UI commands, IPC, broker transitions, source registries/discovery/fetching, importers, canonical identity, persistence, recovery, probes, core lifecycle, settings, selection/failover, tests, build/packaging and engineering reports. Full source availability does not prove that every possible execution or every dependency is defect-free.

All **266 distinct blobs reachable from the 22 production-history commits**, totaling 2,853,901 bytes, were screened for five narrow signature categories: classic GitHub tokens, GitHub fine-grained tokens, AWS access-key IDs, PEM private-key headers and OpenAI-key-like strings. No matches were found. This is not entropy-based/general secret detection, not a guarantee that credentials are absent, and not inspection of unreachable remote objects.

The audit branch adds **eight files only**: two workflows, two regression-test files, and two small harness projects. Existing production files were not changed. Do not blindly merge the branch into main. Port the useful tests and harnesses deliberately, preserve their acceptance meaning, and integrate permanent CI separately.

### 1.2 Independently executed suites

Canonical run: `37127984578`, commit `a5f110b3c2f326e529f7d866ad9415062e6b64f5`. Release builds passed on Linux and Windows with SDK **10.0.112** and runtime **10.0.12**.

| Executed suite | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| New round-4 regressions, Ubuntu | 4 | 29 | 0 | 33 |
| New round-4 regressions, Windows Server 2025 | 4 | 29 | 0 | 33 |
| Existing full suite, Linux without optional native core | 124 | 1 | 3 | 128 |
| Existing full suite, Windows without optional native core | 110 | 15 | 3 | 128 |
| Existing full suite, Linux with verified pinned Mihomo | 127 | 1 | 0 | 128 |

The 29 new failing cases are repeated across platforms: they are **not 58 separate defects** and are not a count of exploited vulnerabilities. There are 27 named Q-tests, with theories expanding them to 33 cases. The four positive/negative controls Q22, Q23, Q25 and Q26 pass on both platforms.

The existing Linux/native failure is the old worker-output assertion in `Rt03WorkerCancelCleansCredentialsAndDoesNotKillTheNextProcess`: it still limits the total drained output to 1 MiB after the production code was correctly changed to keep draining. Do not restore the output deadlock to satisfy this obsolete assertion. The existing Windows failures also include nonportable test fixtures and file-cleanup expectations, not only production defects. See R4-30.

Initial run `37126680884` also exposed a timing-sensitive `BoundedTransfer` assertion; it did not fail in the canonical run. Preserve that evidence rather than describing every run as identical. The project's reported 128/128 result was not independently reproduced here, but these results do not prove that the reported earlier run was fabricated.

The native job fetched the exact official Mihomo **v1.19.32** Linux archive and verified both archive and executable SHA-256 before execution. The three optional native checks actually ran. In-process state/guard doubles are not packet-level protection tests.

### 1.3 Additional execution, not just source analysis

Additional run `37127812801`, commit `8361f6340adda5e31c27482e92b0fee4fe5cf4b0`, performed four separate workloads:

- **Real SQLite measurements at 1,000, 5,000 and 10,000 nodes on Linux and Windows.** At 10,000 nodes, each of three single-assessment updates took about 365-367 ms on Linux and 393-407 ms on Windows, allocating approximately **38.8 MB** of managed memory per update. No other node's secret was re-protected. These are observations on those runners with a test-only counting passthrough protector, not a DPAPI benchmark or a portable latency guarantee.
- **Nine actual native configuration validations.** Eight profiles passed `mihomo -t`: VLESS, Trojan, Shadowsocks, Hysteria2, TUIC, and VLESS WS/gRPC/H2. The VMess fixture with omitted `AlterId` was rejected with `has unset fields: alterId`. This is not a claim that all VMess configurations fail, nor that `-t` proves every field or handshake works.
- **Actual WPF App/MainWindow execution.** Four navigation handlers were exercised at 880x640 and 1120x760, producing eight renders. Protection and LAN checkboxes were toggled and restored. The profile remained unconsented; no public subscription download, public-node probe, TUN operation or service installation occurred. These are two window sizes at 96 DPI, not a multi-DPI/theme/keyboard/tray acceptance matrix.
- **One live upstream metadata request.** At `2026-10-03T13:55:07.037176Z`, the configured source commit was `20c38289c29e4dba6b8f01ddd3273ec9ec169b46`, while upstream HEAD was `8f28255d2348e9f628b1c60d2e010817f3b656d6`, committed at `2026-10-03T13:49:11Z`. No public subscription bodies or proxy credentials were retained by this check.

The Windows runner is **Windows Server 2025 Datacenter, 10.0.26100**, not a Windows 11 acceptance VM. WPF renders were downloaded and visually inspected. The transitive NuGet advisory query returned zero vulnerable package entries across nine projects; it does not cover all native/Go/Wintun dependencies or establish an SBOM/signature/compliance verdict.

### 1.4 Auditor corrections and unexecuted gates

Intermediate audit fixtures had incorrect registry initialization, an xUnit2031 analyzer failure, and a Windows TLS certificate/private-key portability problem. These were auditor test-harness defects, corrected before the canonical run. They are excluded from product findings. The final TLS fixture uses its own temporary certificate and custom trust collection, never a global trust-store bypass. Both valid-certificate and invalid-certificate controls pass.

The mutation corpus completed **528 bounded direct-string parser mutations per platform; 53 escaped as `ArgumentException`**. This demonstrates an API containment defect. It does not establish that an arbitrary raw HTTP body can create the same invalid UTF-16 state or that a remote crash exploit was demonstrated.

**Not executed:** installed Windows TUN/WFP/SCM positive acceptance; production DNS/IPv6 packet capture; two-user Windows service authorization; Windows 11 installer/upgrade/uninstall/reboot/sleep tests; all-protocol controlled handshakes; simultaneous active-TUN/candidate-path packet isolation; sustained full-catalogue probe/soak testing; comprehensive native dependency/SBOM/signature verification. A 10,000-node micro-workload is not a long-duration soak.

Downloaded artifacts were CRC-checked and SHA-256-checked against GitHub metadata. The JSON records exact runs, jobs and artifact identities. GitHub artifact retention is finite; preserve the evidence bundle.

## 2. Changes that must be preserved

Credit the actual improvements instead of mechanically repeating the previous audit. TLS is real; malformed status lines and plain 200 responses are now rejected by the strengthened response policy. Mismatched non-null candidate proofs and cancellation at ordinary publication are handled better. Failed on-demand admission invalidates old success. Unsupported candidates no longer necessarily terminate the queue. Daily counters saturate and do not move backward to a previous day. Speed checks include more context. Child output continues draining. Quoted-secret redaction improved.

Windows loopback-port ownership now has a platform implementation; this is distinct from still-missing Windows pipe authorization. Journal header handles are closed before quarantine, unknown-marker ordering improved, and removed-effect IDs are deduplicated. Precanceled connect and explicit unprotected state-model behavior improved. Ordinary mutation traffic is no longer limited to 256 successful requests, although replay retirement is incomplete. Several source and policy subcases also improved.

Keep these controls. A fixed helper can still be wired into an incorrect product lifecycle, and a stronger guard can introduce a cleanup ownership regression. That is the focus of this round.

## 3. Findings and required remediation

Evidence labels: **EXECUTED** means a specified test or workload ran; **STATIC** means the relevant source/call graph demonstrates the issue but not its full deployed consequence; **GAP** means required implementation or acceptance evidence is missing. Priorities are engineering priorities, not CVSS scores. P0 blocks delivery; P1 is serious correctness/safety; P2 is required robustness, performance or product completeness.

All paths below are relative to the audited production commit. Q-tests are in the audit branch. A state-double failure is not a claim of an observed real network leak.

### R4-01 [P0] Finish the actual Windows VPN composition

**GAP. Paths:** `src/AutoVpn.Service/Program.cs`, `Broker/ICoreController.cs`, `UnavailableNetworkGuard.cs`, service project, packaging scripts.

The shipping composition still uses refusing core/protection adapters rather than an installed, supervised Windows TUN service. This is missing production behavior, not merely an unexecuted test. Do not treat perpetual refusal as a completed safe VPN.

**Fix:** implement SCM-compatible service lifetime, protected official asset discovery, owned core supervision, readiness/production verification, network guard and startup reconciliation. Keep ordinary UI use unelevated. Preserve explicit refusal for actually unsupported environments or failed validation, not as the only production path.

**Close when:** clean Windows installation reaches an actual TUN-routed controlled HTTPS exchange, accurately disconnects and restores owned state. Missing/tampered assets and failed readiness never produce `Connected`. Windows packet evidence is required.

### R4-02 [P1] Establish Windows identity and a real catalogue-to-broker handoff

**STATIC + existing Windows test failures. Paths:** `Broker/PipePeer.cs`, `LocalIpcServer.cs`, desktop/service entrypoints, `BrokerEngine.Select`, IPC contracts.

Windows peers remain unverified/rejected. The UI's catalogue and consent are not a working authority exchange with a service under its intended identity. Supplying a `ConnectPayload.Node` is not equivalent to admitting it into the broker's trusted runtime set; selection still depends on the broker catalogue. Separate LocalApplicationData/DPAPI identities further matter after installation.

**Fix:** explicit restrictive ACLs, OS-derived SID/session identity, authorized owner lease, authenticated server discovery, and bounded typed runtime/settings handoff with digest/epoch/policy validation. Keep full source fetching, parsing and user catalogue unelevated. Do not share a user-writable database as privileged authority or permit arbitrary profile/executable paths.

**Close when:** real owner UI, actual service identity, second user/session, stale lease and spoofed server are tested. First-run consent and a measured candidate reach the broker without test seeding.

### R4-03 [P0] Source refresh still uses a frozen revision and has an invalid branch fallback

**EXECUTED Q07 + live metadata + STATIC. Paths:** `config/source-manifest.json`, `Fetch/ReviewedRegistry.cs`, `GithubTreeParser.cs`, `CatalogueCoordinator.DiscoverAsync/BuildDiscovery`.

The reviewed configuration still pins the initial source commit. Extracting that hash into `CommitRef` does not discover current HEAD. Switching the tree URL to a branch also fails: the tree response's SHA can become the raw-content commit reference. Q07 exercises that branch-shaped response. The live metadata comparison proves the configured revision was already behind upstream during this audit.

**Fix:** resolve a permitted branch to a commit object, then fetch its tree and content bound to that commit. Keep commit/tree/blob/content hashes as different typed identities. Make the initial pin a bootstrap/last-good fallback, not the permanent refresh target. Validate returned identity, tree completeness, blob mode and safe paths; incomplete discovery must not prune good state.

**Close when:** two different controlled upstream commits and a live metadata smoke produce the correct content URLs, honest coverage and retained fallback nodes. A tree SHA is never silently treated as a commit SHA.

### R4-04 [P1] Source fairness disappears when the UI recreates the coordinator

**EXECUTED Q05. Paths:** `CatalogueCoordinator.RefreshAsync`, `_refreshCursor`, `Desktop/MainWindow.xaml.cs: RunRefreshAsync`.

The cursor advances only within one coordinator instance. The actual UI constructs a new coordinator for each refresh. Three cycles with that lifecycle still never fetch the ninth of nine tiny subscriptions. A passing test that reuses one helper does not close this product path.

**Fix:** put queue ownership/progress in the real long-lived application coordinator or persist it by stable artifact identity. Reconcile progress when discovery changes. Enforce actual aggregate byte/time budgets across attempts and mirrors without permanently reserving the worst-case size for every tiny file. Keep concurrency bounded and cancellation safe.

**Close when:** every eligible artifact eventually receives work across new coordinators, application restarts, changed ordering, failures and manual refreshes. Test the UI-created lifecycle, not only the same-instance helper.

### R4-05 [P1] Successful 304 responses and historical URLs break scheduling freshness

**EXECUTED Q06 + STATIC. Paths:** `CatalogueCoordinator.DownloadAsync/RefreshAsync`, `SourceLedger`, `RefreshScheduler`, desktop timer.

A valid 304 preserves content but does not advance the timestamp used by the scheduler. The source remains due on each one-minute pulse. Entries keyed by immutable revision URLs can accumulate old timestamps. Discovery success and artifact success must not conceal never-successful families or conflate unchanged content with failed refresh.

**Fix:** separate last attempt, last validated check, last content change and retry-after. Schedule by stable source/artifact identity. Record successful 304 freshness only with a usable committed local snapshot, otherwise refetch unconditionally. Commit accepted snapshot identity and associated validators coherently; retain prior state on rejected/partial work. Bound ledger history and serialize writers.

**Close when:** repeated 304s do not create a refresh storm; one unavailable source does not suppress another; restart/fault injection never advances ETags without corresponding durable content.

### R4-06 [P1] Missing proof fields still mean successful admission

**EXECUTED Q01, two cases. Paths:** `ProbeCoordinator`, proof acceptance and assessment publication.

A success with absent candidate/target proof is accepted. Correct digest/target but absent worker identity is also accepted. Checking only mismatched non-null values is not a complete contract.

**Fix:** require an immutable observation context containing target contract, candidate/configuration digest, network epoch, policy revision, owned worker and unique attempt identity. Bind it to what was requested and reject missing fields at the production publication boundary. Synthetic test transports should construct explicit synthetic contexts rather than exploiting a permissive fallback.

**Close when:** missing, mismatched, replayed and previous-worker proofs cannot publish health, while Q23's matching-proof control remains functional. Complete path isolation must be separately tested under a real active tunnel.

### R4-07 [P1] Request timeouts can masquerade as user cancellation

**EXECUTED Q03 + STATIC. Paths:** `ProbeCoordinator.RunAsync`, `NonTunCoreProbeTransport.ProbeAsync`, `ProbeWorker` readiness and failure classification.

The transport can catch cancellation caused by the coordinator's request deadline and return `Canceled`; the coordinator then treats it as user cancellation even though the caller was never canceled. No failure/backoff is recorded and the cycle may stop. A candidate-specific invalid native profile can also be classified as a global core-start failure, stopping unrelated work.

**Fix:** preserve cancellation origin and distinguish owner cancellation, per-attempt deadline, invalid candidate/configuration, missing/tampered core, target failure and uplink failure. On deadline, record a bounded inconclusive/failure outcome under the diagnosis policy, not fabricated user cancellation. Keep global environment failures separate from node capability rejection.

**Close when:** repeated slow/bad candidates cannot terminate or monopolize unrelated work; genuine user cancellation publishes no late success; a missing core is reported without condemning every server.

### R4-08 [P1] On-demand checks contact denied nodes and policy-blocked records remain stuck

**EXECUTED Q02, three cases, and Q15. Paths:** `ProbeCoordinator.AdmitIfStaleAsync`, `Scheduled/PolicyHeld/NeedsProbe`, `RefreshMerge`, import policy classification.

On-demand admission checks some policy only after transport I/O: excluded, disabled-family and strict-country-denied nodes are contacted first. Conversely, a node classified at import as certificate-policy-blocked is never reconsidered when the owner explicitly enables the corresponding option.

**Fix:** apply current authorization before every dial and revalidate it before publication. Separate immutable unsupported semantics from mutable owner-policy decisions. Recompute policy-derived eligibility when settings change, without refetching identical data or blindly trusting old assessments. Carry explicit selection purpose where the original manual exclusion override is allowed.

**Close when:** denied background/on-demand candidates cause zero network calls; explicit opt-in permits only its intended proxy-certificate relaxation, never invalid target HTTPS certificates. Revocation takes effect before new work and at completion.

### R4-09 [P1] Retry pacing and the complete admission scheduler remain unfinished

**EXECUTED Q04 + STATIC/GAP. Paths:** `ProbeCoordinator`, `Domain/Scheduling.cs`, `ProductLimits`, desktop refresh/probe invocation.

Future `RetryAfterUtc` is ignored. The production flow still lacks the complete fair, resumable, independently paced validation scheduler and the required multi-target admission policy. One elapsed measurement including core startup is not a latency median. A short cycle budget and a per-candidate budget are different constraints.

**Fix:** implement durable/resumable queues, bounded endpoint concurrency, backoff, health freshness and on-demand priority. Run maintenance independently of a two-hour source refresh so a 30-minute assessment policy does not empty the working set. Validate the approved independent targets and actual responses, maintain bounded sample history, and classify UDP/capabilities separately.

**Close when:** retry-after is honored, healthy records are rechecked on time, bad candidates cannot starve good ones, target outages do not condemn the catalogue, and candidate B cannot pass through unrelated production tunnel A.

### R4-10 [P1] Authenticated 204 responses with contradictory framing are accepted

**EXECUTED Q24, three real TLS cases. Paths:** `NonTunCoreProbeTransport.cs: Socks5Client`, response parser and byte accounting.

The authenticated peer sends 204 with `Content-Length: 3` and `ABC`, 204 with chunked transfer/body, or a malformed `Content-Length :` field and body. All three return no failure. Valid empty 204 and invalid-certificate controls Q25/Q26 pass, so the negatives reached the intended HTTP layer.

**Fix:** enforce the approved target's status/body contract with a bounded HTTP parser. Reject forbidden/contradictory framing, malformed field syntax, duplicate/conflicting length fields, unsupported transfer coding and unexpected content. Do not wait indefinitely for a body forbidden by a legitimate 204 response. Preserve counts from all consumed bytes rather than overwriting them with the initial header read. RFC 9110/9112 references are below.

**Close when:** fragmented headers, coalesced data, delayed unexpected bytes under the defined contract, malformed framing and oversized responses are handled deterministically. Do not reinstate plain HTTP or disable TLS to simplify tests.

### R4-11 [P1] Malformed Unicode escapes parser containment

**EXECUTED Q27. Paths:** `Import/SubscriptionImporter.cs`, JSON entrypoints, text/label validation.

The completed bounded corpus executes 528 mutations and observes 53 escaping `ArgumentException` failures. These are direct-string invalid-UTF-16 cases, not a demonstrated HTTP-delivered exploit. The public parser still needs a precise input/exception contract.

**Fix:** validate decoding/Unicode at each entry boundary; contain expected input failures per record or document and preserve last-good snapshots. Do not silently change credential bytes. Keep internal programming/storage failures distinguishable from malformed input. Expand fuzz/property testing for byte decoding, duplicate keys, nesting, aliases, truncation, oversized fields and mixed valid/invalid records.

**Close when:** all bounded corpus inputs finish with a typed deterministic result, no unhandled input exception and no invalid authoritative empty snapshot. Add tests that traverse the actual HTTP decoding path before making claims about remote reachability.

### R4-12 [P1] Case-conflicting security keys and nested mappings are ambiguous

**EXECUTED Q14 + STATIC. Paths:** `ClashProxyParser`, subscription YAML/JSON mapping and option schemas.

The parser accepts both `skip-cert-verify: false` and `Skip-Cert-Verify: true` in one mapping. Collection and lookup comparers disagree, so a conflicting security instruction is accepted according to incidental lookup behavior. This is not proof that a deployed TLS bypass occurred, but ambiguity must not cross the canonical boundary.

**Fix:** choose a defined field-name policy and reject duplicates under that policy, including nested aliases and JSON/YAML conversion. Whitelist supported nested transport/plugin semantics and reject unsupported meaningful fields rather than dropping them. Validate types before coercion; preserve opaque values exactly.

**Close when:** conflicting case variants, duplicate keys, unknown nested fields and malformed option types cannot become a different apparently valid node. Positive format-equivalence cases retain identical effective semantics.

### R4-13 [P1] Xray round trips and VMess native defaults remain inconsistent

**EXECUTED Q12/Q13 and native profile validation + STATIC. Paths:** `XrayOutboundParser.ParseAll/Parse`, `ClashProxyParser`, `MihomoProfileGenerator`.

One Xray Shadowsocks `settings.servers` record does not follow the normalization used for multiple records. VLESS WebSocket input is accepted while its `wsSettings.path` is lost. A canonical VMess profile with omitted `AlterId` reaches generation but the pinned core rejects it for missing `alterId`; relevant import paths can leave this value null.

**Fix:** normalize zero/one/many wrapper records consistently, fully map admitted TLS/REALITY/WS/gRPC/H2/plugin semantics, and define protocol-specific defaults shared by import, identity, runtime wire and generation. For VMess, emit a justified validated default or reject the incomplete record before admission. Do not assert that all VMess is broken or default away explicit nondefault values.

**Close when:** import-to-native tests cover single/multiple endpoints, path/host/service/SNI/cipher preservation and omitted/explicit VMess alterId. Add controlled handshakes for every claimed combination; native `-t` alone cannot prove semantic fidelity.

### R4-14 [P1] Cancellation and Start exceptions leave incorrect lifecycle state

**EXECUTED Q16/Q21. Paths:** `BrokerEngine.ConnectAsync/HandleAsync`, core lifecycle interface.

Cancellation after Start begins can still commit a late successful result. An `IOException` from Start can leave the broker in `Connecting`. The precanceled check added in round 3 does not cover cancellation between awaits.

**Fix:** allocate an owned attempt before effects, check cancellation/context again inside the commit decision, and make late-success cleanup independent of the canceled request token. Translate expected start/preflight/guard failures into explicit states and owned cleanup, preserving protection until authorized release. Use bounded waits and do not discard the original failure if cleanup also fails.

**Close when:** cancellation before arm, during spawn/readiness, before commit and after validation never resurrects an abandoned session. Inject Start exceptions and stalled cleanup while a second client issues a safety command.

### R4-15 [P1] Rejecting production confirmation loses the core cleanup identity

**EXECUTED Q17 with a strictly operation-owned core double. Paths:** `BrokerEngine.ConfirmProduction`, `DisconnectAsync`, operation fields.

After Start succeeds, a policy change makes confirmation reject the session and clear its operation identity without stopping the owned core. Later Disconnect sends an empty/different identity; the strict owned core remains active. A permissive mock that globally stops everything hides this defect.

**Fix:** separate active proof identity from a durable cleanup handle. Revoking confirmation invalidates usability but must not erase ownership of existing effects/processes. Schedule an owned stop/reconcile transaction, retain retryable cleanup context until positively complete, and report unknown process/protection state honestly.

**Close when:** policy rejection, certificate-permission revocation and failed production verification followed by Disconnect stop exactly the original owned process. A stale cleanup must never stop a newer core. This test does not itself prove real packet leakage.

### R4-16 [P1] Failover commits after the candidate or network becomes invalid

**EXECUTED Q18, epoch and exclusion cases. Paths:** `BrokerEngine.ReportHealthAsync`, switch commit, selection context.

A switch starts, then the network epoch changes or the standby is excluded; the delayed successful Start can still commit that candidate. The settings stamp does not cover all mutable eligibility and attempt facts. Health reports also need an unambiguous relationship to the process/session they describe.

**Fix:** bind every switch and health completion to boot, lease, attempt, core generation, node digest, epoch and policy/selection purpose. Recheck current exclusion/source/country/freshness after I/O inside serialized authority. Preserve truthful non-connected state while replacing a confirmed-dead process, and retain cleanup handles on rejection.

**Close when:** old health/core-exit events, excluded/expired/changed candidates, network changes and concurrent disconnect cannot commit or confirm the wrong session. Keep the fixed immediate CoreExit truth transition.

### R4-17 [P1] The post-Start guard contradicts the specified manual exclusion override

**EXECUTED Q19. Paths:** eligibility purpose handling and `BrokerEngine` post-Start selection check; original manual-selection specification.

A manually selected node excluded from automatic selection passes the manual eligibility rule, then fails the unconditional post-Start exclusion check with `POLICY_CHANGED`. This makes two layers implement different product policies.

**Fix:** carry selection purpose and explicit owner intent through admission, Start and confirmation. Distinguish exclusion from automatic selection from disabled source, forbidden security, invalid configuration or other hard restrictions. Do not let an arbitrary manual flag bypass those hard restrictions.

**Close when:** deliberate manual selection follows the original contract, automatic selection never chooses excluded nodes, and later cancellation/policy changes are still honored. Fix the inconsistency rather than deleting either safety or manual-selection tests.

### R4-18 [P1] Evicted replay tombstones permit old commands to execute again

**EXECUTED Q09 at the dispatcher boundary. Paths:** `Application/IpcDispatcher.cs`, request contracts, owner lease lifecycle.

After the response cache and bounded retired-ID set are exhausted, an old request ID becomes new again. The test observes a second handler execution. Broker state-revision checks mitigate some deployed cases, so this is not a claim that every old Connect can immediately replay successfully.

**Fix:** define authenticated boot/lease-scoped monotonic request sequence or an equivalent bounded replay contract. Reject requests outside the accepted window even after individual tombstones are evicted. Support legitimate idempotent retries with original outcome/uncertain-effect semantics and explicit session renewal. Do not restore a lifetime command cap or keep unbounded history.

**Close when:** long-running sessions remain usable, old/duplicate/reordered commands cannot repeat effects across cache eviction or restart, and Disconnect/recovery remain responsive without reopening stale mutations.

### R4-19 [P1] A delayed previous-boot reply can replace the new broker state

**EXECUTED Q10. Paths:** `Application/UiSession.cs: SessionMailbox`, desktop send/resync paths.

Boot A sequence 100, then boot B sequence 1, then delayed boot A sequence 101 makes the mailbox return to A. Treating any different boot ID as a legitimate fresh boot is insufficient.

**Fix:** bind replies to the current authenticated transport/resync generation and originating request. Adopt a new broker boot only through an explicit current handshake; reject superseded-session replies. Validate request ID, protocol and snapshot ordering consistently, preserve last-known protection on transport loss, and do not accept an unsolicited snapshot as proof of a clean disconnect.

**Close when:** reorder replies across broker restarts, UI reconnects, cancel and explicit Exit. An old boot never resurrects Connected or overwrites newer safety state.

### R4-20 [P1] Slow effect I/O must not monopolize safety control

**STATIC/GAP plus existing Windows IPC failures. Paths:** `BrokerEngine` guard/cleanup calls, `LocalIpcServer`, `IpcDispatcher` in-flight wait.

Bounded frame/session handling does not itself bound guard Arm, core Stop or synchronous recovery. Critical I/O still occurs under state locks or is awaited with no operation-level deadline. CancellationToken.None is appropriate for some cleanup obligations but is not a timeout policy. Synchronous waiting on duplicate in-flight work also needs a bounded contract.

**Fix:** serialize decisions in a state actor, execute owned slow work outside the authority critical section, and post context-bound completions. Use operation tickets, deadlines and retryable cleanup states. Reserve capacity for safety commands and make server/child shutdown bounded without letting abandoned work mutate newer state.

**Close when:** actual Windows pipe clients can cancel/disconnect during stuck arm/start/stop/recovery; malformed/held clients cannot exhaust safety capacity; all tasks and handles terminate or remain explicitly owned for recovery.

### R4-21 [P1] Service-style reopening erases missing-journal uncertainty

**EXECUTED Q08. Paths:** `Persistence/EffectJournal.Open`, `RequiresReconciliation`, `.journal-seen` marker, service/recovery entrypoints.

After an existing journal is deleted, `RequiresReconciliation` correctly detects the condition. But calling `Open` first recreates an empty database; recovery can then report success. The CLI precheck does not protect every caller, especially service startup.

**Fix:** capture/persist unknown recovery state before creating replacement storage, inside the common journal-opening boundary. Reconcile actual owned OS objects independently of user catalogue health. Preserve uncertainty until real ownership-aware reconciliation succeeds, including missing marker/database, corrupt WAL and interrupted quarantine. Never infer absent OS effects merely from an empty replacement database.

**Close when:** delete a previously used journal, reopen through service and CLI paths, restart again, and verify neither reports clean before reconciliation. Retain the fixed close-before-quarantine and deduplicated removal logic. Never reset unrelated firewall/routes/DNS.

### R4-22 [P1] Catalogue authority, Windows file lifetime and persisted validation need completion

**STATIC + existing platform failures. Paths:** `SqliteCatalogue.Open/Load/Dispose/BackupTo`, mutable catalogue exposure, settings loader.

Copy-before-commit and stale-writer conflict detection are useful, but independent authorities still need a defined synchronization/handoff model. Public mutable node objects are not immutable revisioned snapshots. Pooled SQLite connections and test-owned open handles account for several Windows deletion failures; not every failure is proof of a production leak. Loaded settings also need the same semantic validation as new settings.

**Fix:** one authoritative writer or explicit reconciled revisions, immutable reader snapshots, schema/value validation, conservative active-session restoration, and a documented connection/pool lifetime. In tests, dispose owned handles before deletion and use scoped pool management or nonpooling fixtures. Do not hide file-operation failures or globally clear unrelated production pools.

**Close when:** two processes, failed commits, restart/migration/backup, newer schema, invalid settings and Windows file moves have consistent outcomes. Failed writes never become visible authority and owner metadata remains preserved.

### R4-23 [P2] Single-assessment updates still process the whole catalogue

**MEASURED + STATIC. Paths:** `SqliteCatalogue.TryUpdateInPlace/SameIdentity`, `MemoryCatalogue.Copy`, node lookups.

At 10,000 nodes a single assessment costs roughly 0.37-0.41 seconds and 38.8 MB allocated on the tested runners despite zero protector calls. Full copies/serialization and repeated linear searches remain. The result is not a memory-leak diagnosis or a complete end-to-end throughput benchmark.

**Fix:** targeted transactional mutations with indexed node identity and immutable revision publication, bounded change sets and batched assessment writes. Avoid copying/serializing all credentials to update one latency. Keep backup and migration transactional; do not sacrifice correctness for benchmark numbers.

**Close when:** rerun the supplied 1k/5k/10k workload, record CPU/allocation/SQL/protector counts and compare before/after on the same environment. Then test sustained real scheduling plus UI responsiveness. A fast text formatter is not proof that a WPF catalogue remains responsive.

### R4-24 [P2] Retention and aggregate limits must be enforced by production workflows

**STATIC/GAP. Paths:** catalogue merge/eviction, `Domain/Scheduling.cs`, `ProductLimits`, source discovery and queue construction.

Policy helpers and constants do not establish bounded catalogue size, candidate backlog, assessment history, retained source revisions or credential lifetime. Worst-case per-file reservations are not accounting of real bytes across retries and mirrors. Protecting favorites/current sessions requires an explicit overflow policy rather than silent owner-data loss.

**Fix:** enforce admission, expiry and retention at transaction/scheduler boundaries; retain healthy historical nodes according to the original policy, with provenance and revalidation. Bound low-priority pending work and histories, preserve favorites/current sessions, and report intentional overflow or incomplete coverage honestly.

**Close when:** large overlapping/new/removed source families, missing sources, long offline periods and repeated refreshes cannot grow state indefinitely or delete protected owner data. Execute the full-capacity workflow, not only a one-batch seed.

### R4-25 [P1] DNS, IPv6 and resolved-destination safety remain unproven

**STATIC/GAP. Paths:** `EndpointSafety`, generated DNS/TUN rules, fetch/probe dialers and future OS guard.

Literal special-use checks improved, including the benchmark range, but hostname resolution/rebinding and actual dial binding remain incomplete. Internal user-domain DNS routing, endpoint bootstrap exceptions, IPv6 and active-TUN candidate isolation still lack the required packet-level evidence.

**Fix:** validate all resolved addresses under separate origin/proxy/target policies and bind approval to the actual connection. Keep controlled loopback fixtures explicitly test-only. Route user DNS through the intended outbound with narrow documented bootstrap exceptions; protect or block every advertised address family. Do not equate a `strict-route` setting with a crash-surviving kill switch.

**Close when:** controlled resolver rebinding/mixed-address tests and Windows packet captures cover DNS/IPv6, core/broker crash, uplink change and active A versus broken B with positive and negative controls.

### R4-26 [P1] Finish owned process, asset and diagnostic boundaries

**STATIC/GAP with corrected subcases. Paths:** `ProbeWorker`, `MihomoProcessController`, profile staging, `SecretRedactor` and diagnostic export.

Keeping drains active is fixed; the old total-output assertion must change. That does not close protected executable/DLL lookup, hash-to-execution ownership, all cancellation/exit paths, native validation output bounds, crash-orphan recovery or structural diagnostic redaction. Raw retained error tails still require a defined sanitization boundary.

**Fix:** protected verified asset staging, owned process/job handles, scoped kill-and-wait, restricted temporary credential files, bounded retained output while continuing to drain, and structured redaction using the actual secret set before logging/export. Separate timeout from cancellation. Retain cleanup obligations when deletion/termination cannot be confirmed.

**Close when:** tampered/replaced assets, occupied/swapped ports, chatty/hung children, late old cleanup and encoded/quoted secret canaries are tested on supported platforms. Never log full generated profiles or commit live subscriptions.

### R4-27 [P1] Daily budgets and throughput still lack complete path accounting

**STATIC/GAP. Paths:** `ProbeByteBudget`, `ProbeCoordinator`, on-demand admission, desktop budget save, `SpeedMeasurement`.

Counter saturation/day ordering are improved, but reported response payload bytes are not a complete traffic budget. Failed/canceled work, TLS/protocol overhead, concurrent reservations, crash-before-save and on-demand checks need explicit treatment. A matching digest on an arbitrary supplied Stream does not authenticate its network path.

**Fix:** define exactly what is metered, reserve conservatively before work, reconcile completion/failure/cancel, persist safely and serialize concurrent charges. Apply owner-visible caps to on-demand and benchmarks too, with deliberate safety exceptions where specified. Obtain throughput streams from an owned verified candidate path; check context before and after measurement and keep benchmark speed separate from live transfer rate and health.

**Close when:** failures/restarts/cancellation cannot silently refund substantial consumed allowance; late prior-day results do not roll back today; unrelated streams cannot publish candidate throughput. Unknown speed remains unknown.

### R4-28 [P1/P2] Settings, country and runtime-set policies are inconsistent

**EXECUTED Q11/Q20 + STATIC. Paths:** `ProductSettings.Validate`, `BrokerEngine.ApplyRuntimeSet`, ranking/country/source helpers and UI selection.

Undefined enum values are accepted. A hundred duplicate standby records are retained instead of a bounded deduplicated runtime set. Country labels/codes, preferred versus strict selection, disabled-family state and selected ranking mode still need consistent production behavior and measured inputs.

**Fix:** validate all enums/ranges/schema values on load, IPC and mutation. Normalize geography to codes with localized display/provenance/conflicts. Enforce hard policy at first connect and failover. Bound/deduplicate standby records, exclude active duplicates and select endpoint diversity. Use actual sample counts/history; do not invent speed or stability to fill ranking fields.

**Close when:** malformed settings cannot silently relax policy, strict-country never escapes, preferred mode behaves as documented, and post-staging exclusions/expiry are honored. Runtime sets stay within the specified cap without losing the healthy active session.

### R4-29 [P2] Complete the functional desktop, not only its rendered shell

**ACTUAL WPF SMOKE + STATIC/GAP. Paths:** `MainWindow.xaml/.cs`, `App.xaml`, `CataloguePresentation`, session binding.

The actual window renders and navigation/settings handlers execute, which is useful evidence. It still does not supply the promised interactive measured-node catalogue, manual selection/favorites/exclusions, complete source controls, useful speed/live metrics, themes and tray/recovery experience. Text output in a large block is not an interactive server list.

**Fix:** finish the original simple four-view design with real bound commands, incremental/virtualized rows, honest timestamps/reasons and unambiguous active/healthy/disabled state. Add system/light/dark theme behavior, accessible keyboard/focus/labels, sensible loading/empty/error states and a complete explicit Exit/cancel/resync contract. Keep advanced settings out of the default flow.

**Close when:** a Windows click/keyboard matrix covers fresh install, refresh, manual selection, favorite/exclusion, settings persistence, broker restart, protected outage, tray exit and recovery at representative DPI/theme settings. Never use mock measurements in release screenshots as proof of real functionality.

### R4-30 [P1] Correct the tests and make Windows/native evidence permanent

**EXECUTED baseline suites. Paths:** `Round2SliceATests`, `PackageBTests`, SQLite/pipe tests, `.github/workflows/ci.yml`, test scripts.

The canonical existing suite fails once on Linux/native and fifteen times on Windows. Linux/native's output counter failure is an obsolete assertion after a correct drain fix. Windows includes five pipe failures, nine file-lifetime/cleanup failures, and a Linux `/usr/bin/pkill` fixture. In particular, the current Rt22 failure is deletion while its test still owns an open journal, not the previously fixed header-quarantine handle bug. Initial runs also expose timing-sensitive truncation reporting in `BoundedTransfer`.

**Fix:** retain bounded log retention but allow total drained bytes to exceed the cap. Make owned-process fixtures platform-specific or portable; do not skip entire Windows classes to hide real defects. Dispose/clear only owned test resources. Track whether a transfer timed out explicitly rather than inferring it solely from a subsequent elapsed-time comparison. Run Windows build/unit/IPC smoke and pinned native checks permanently, with structured truthful skips.

**Close when:** original plus new tests pass for the correct reasons, positive controls still work, repeated runs do not rely on lucky timing, and actual unavailable admin gates remain explicitly NOT_RUN instead of green no-ops.

### R4-31 [P0 delivery / P2 supply chain] Produce a current installable artifact

**GAP. Paths:** `scripts/package.ps1`, `verify-release.ps1`, `test-windows-admin.ps1`, core manifest, notices and handoff/evidence documents.

Published folders and reports are not the original install/connect/update/recover/uninstall deliverable. Production service registration, protected directories, reviewed core/driver assets, installer/upgrade/uninstall and exact artifact provenance still require implementation and execution. The earlier Mihomo license correction should be preserved.

**Fix:** build a retrievable Windows package from a clean explicit commit/tree, include actual dependency/core/driver identities and notices, generate a transitive SBOM and review native dependencies/provenance. Implement verification rather than changing a placeholder's exit code. Handle unavailable signing credentials honestly; never invent Authenticode or bypass OS security controls. Keep existing owner data and unrelated networking untouched.

**Close when:** another supported clean Windows machine can retrieve/hash-verify/install the artifact, launch unelevated, connect, upgrade, recover and uninstall. The artifact, manifest, licenses and acceptance evidence agree on exact build inputs.

### R4-32 [P1 acceptance] Close complete journeys instead of optimizing for this test list

**GAP. Paths:** original specification, all audit status reports, production composition and acceptance scripts.

Newly green unit subcases cannot close missing Windows networking, packaging, full source coverage or UI features. Conversely, fixing the 29 new failing cases must not trigger another stop while the original deliverable remains a refusal-only shell.

**Fix:** maintain a requirement-to-production-path-to-test matrix for every mandatory original requirement and F01-F34/R2/R3/R4 finding. Separate missing code from missing external execution. Finish all independently achievable work; the available Windows CI runner already disproves a blanket claim that no Windows build/non-destructive testing is possible. Use a specifically authorized isolated Windows 11 environment for destructive/admin packet acceptance.

**Close when:** all mandatory journeys below have exact source/artifact evidence, or a narrowly described real external blocker remains with a recoverable checkpoint. BLOCKED is not DONE. Do not run an unbounded autonomous loop or leave child processes/tasks alive after a slice.

## 4. Executed regression index

These cases already exist on the canonical audit branch. They are not merely suggested future tests. Preserve their acceptance intent while adapting them to legitimate architecture changes.

| Tests | Cases | Canonical outcome on each OS | Required behavior |
|---|---:|---|---|
| Q01 | 2 | Failed | Missing proof cannot publish health |
| Q02 | 3 | Failed | Denied on-demand policy causes no dial |
| Q03-Q04 | 2 | Failed | Correct timeout origin and retry pacing |
| Q05-Q07 | 3 | Failed | UI-lifetime source fairness, 304 freshness, correct commit reference |
| Q08-Q10 | 3 | Failed | Missing-journal uncertainty, replay retirement, old-boot rejection |
| Q11-Q15 | 5 | Failed | Enum validation, Xray round trips, conflicting YAML keys, policy reconsideration |
| Q16-Q17 | 2 | Failed | Canceled late Start and cleanup ownership |
| Q18 | 2 | Failed | Failover epoch/exclusion changes win |
| Q19-Q21 | 3 | Failed | Manual purpose, bounded standbys, Start-exception state |
| Q22-Q23 | 2 | Passed | Healthy owned connect/disconnect and matching proof still work |
| Q24 | 3 | Failed | Authenticated contradictory 204 framing is rejected |
| Q25-Q26 | 2 | Passed | Valid empty TLS 204 accepted; untrusted certificate rejected |
| Q27 | 1 | Failed | All 528 parser mutations are contained; currently 53 escape |

## 5. Round-3 crosswalk

A credited subcase is not closure of the whole corresponding product requirement. The earlier directive remains binding for details not repeated here.

| Round-3 finding | Current independent assessment | Round-4 follow-through |
|---|---|---|
| R3-01 | Production composition still missing | R4-01, R4-31 |
| R3-02 | Windows process-port helper improved; pipe identity still incomplete | R4-02, R4-26 |
| R3-03 | Status/TLS checks improved; framing negatives still accepted | R4-10 |
| R3-04 | Non-null mismatch checks improved; absent proof still accepted | R4-06 |
| R3-05 | Unsupported continuation improved; classification/fairness incomplete | R4-07, R4-09 |
| R3-06 | Failed on-demand invalidation improved; policy lifecycle incomplete | R4-08 |
| R3-07 | Ordinary publication cancellation improved; late Start still commits | R4-14, R4-20 |
| R3-08 | Saturation/day-order subcases improved; complete metering missing | R4-27 |
| R3-09 | Epoch checks improved; verified stream path/integration missing | R4-27 |
| R3-10 | Continuous drain fixed; obsolete test must change | R4-26, R4-30 |
| R3-11 | Quoted redaction improved; full diagnostic boundary remains | R4-26 |
| R3-12 | Frozen source remains; branch fallback still confuses tree identity | R4-03 |
| R3-13 | Same-instance rotation improved; actual UI lifecycle still starves | R4-04 |
| R3-14 | Timer exists; 304, stable source identity and transactions incomplete | R4-05 |
| R3-15 | Lifetime cap removed; evicted retirement permits replay | R4-18 |
| R3-16 | Bounded transport does not finish slow-operation control | R4-20 |
| R3-17 | Some policy checks improved; cleanup and late eligibility regressions | R4-14 through R4-17 |
| R3-18 | Immediate core-exit truth improved; late switch context incomplete | R4-16 |
| R3-19 | Precanceled/unprotected subcases improved; later cancellation incomplete | R4-14 |
| R3-20 | UI local cancellation improved; boot and complete exit lifecycle incomplete | R4-19, R4-29 |
| R3-21 | Header handle and marker ordering fixed; missing-Open bypass remains | R4-21 |
| R3-22 | Conflict checks improved; identity/handoff and Windows lifetime remain | R4-02, R4-22 |
| R3-23 | No redundant re-protection; measured allocation/CPU and retention remain | R4-23, R4-24 |
| R3-24 | New parser/native counterexamples remain | R4-11 through R4-13 |
| R3-25 | Literal range checks improved; real dial/packet boundary remains | R4-25 |
| R3-26 | Enum, country, ranking and standby policies incomplete | R4-28 |
| R3-27 | Actual window exercised more broadly; functional UI still incomplete | R4-29 |
| R3-28 | Target-OS suite independently fails; permanent CI incomplete | R4-30 |
| R3-29 | No accepted current installable release journey | R4-31 |
| R3-30 | Full original acceptance still required | R4-32, section 7 |

## 6. Original F01-F34 coverage ledger

No original group is silently dropped. Map every inherited R2 subcase through the previous directives when updating the closure report.

| Original group | Current scope still requiring closure | Round-4 groups |
|---|---|---|
| F01 | Real service/core composition | 01, 31 |
| F02 | Running discover/refresh/verify workflow | 03-09 |
| F03 | Consent/settings/UI authoritative session | 02, 19, 29 |
| F04 | Responsive real control under load | 18, 20, 30 |
| F05 | Windows peer/owner authorization | 02 |
| F06 | Revision/replay/idempotency/lease | 18-20 |
| F07 | Owned attempt confirmation/cleanup | 06, 14-16, 26 |
| F08 | Truthful failover and guarded retry | 16, 20 |
| F09 | Protected failure and authorized release | 14-16, 20-21, 25 |
| F10 | Real OS guard and owned recovery | 01, 21, 25 |
| F11 | Durable corrupt/missing-journal semantics | 21 |
| F12 | Production DNS/IPv6 routing proof | 25 |
| F13 | Resolved endpoint/origin/target safety | 25 |
| F14 | Freshness/epoch/on-demand context | 06-09, 16 |
| F15 | Real admission, fairness, isolation and budgets | 06-10, 27 |
| F16 | Last-good snapshot and validator transactions | 05, 11 |
| F17 | Untrusted parser/schema containment | 11-13 |
| F18 | Protocol-specific defaults/emission | 13 |
| F19 | Closed security policy at all boundaries | 08, 12-13, 28 |
| F20 | Semantic mapping and compatibility | 12-13 |
| F21 | Opaque identity and conservative migration | 12-13, 22 |
| F22 | Bounded approved HTTP fetch and retries | 03-05, 24-25 |
| F23 | Durable authoritative catalogue mutations | 02, 22-23 |
| F24 | Practical scaling and retention | 23-24 |
| F25 | Complete current source discovery/provenance | 03-05, 24 |
| F26 | Country semantics and constraints | 08, 28 |
| F27 | Measured ranking/bounded runtime standbys | 09, 28 |
| F28 | Timers/monotonic clocks/network epoch | 05, 07, 09, 16, 28 |
| F29 | Verified processes/temp secrets/diagnostics | 26 |
| F30 | Installer and retrievable release | 31 |
| F31 | Truthful layered tests and target platform | 30, 32 |
| F32 | Actual component licenses and supply chain | 31 |
| F33 | Exact source/artifact/evidence provenance | 31-32 |
| F34 | Complete usable polished desktop | 19, 29 |

## 7. Required implementation sequence and acceptance matrix

### Slice A: regressions and coherent pure logic

Acquire the canonical Q-tests and harnesses without merging unrelated audit workflow history. Fix source identity/lifecycle, proof validation, input semantics, policy purpose, replay/boot handling, Start cancellation/cleanup, journal uncertainty and test portability. Add positive controls for every tightened guard. Address R4-13's native default with an import-to-native test, not only a hand-built profile string.

Run the original and new suites on both Linux and Windows. Preserve failed/initial-run evidence. Correct obsolete expectations explicitly rather than weakening production behavior. Keep changes in coherent tested commits and record production paths, not just test edits.

### Slice B: finish the actual unelevated product workflow

Build the real current-source discovery, durable fair queues, source/assessment scheduling, transactional catalogue, measured admission, bounded budgets, useful settings and functional catalogue UI. Exercise clean state with no seeded consent/health/database. Demonstrate preservation across failed updates and useful pending/unsupported/error reporting. Connect bounded runtime/settings handoff to the installed-identity design.

Measure sustained workload and UI responsiveness after optimizing single-assessment writes. Keep unknown geography/speed/capability visibly unknown. Do not add unrelated product features.

### Slice C: implement the Windows control/core boundary

Complete SID/session/ACL/server identity, SCM lifetime, process ownership, protected assets, responsive command actor and cleanup/recovery supervision. Use existing Windows runners for build, file/DPAPI/IPC and non-destructive controlled smoke where appropriate. Missing an admin acceptance VM does not justify leaving independently implementable Windows code or CI absent.

Do not modify the networking of a machine carrying the only control session. Use explicit per-test ownership and a separate recovery path for effects.

### Slice D: execute real protected networking acceptance

On an authorized isolated supported Windows 11 environment, complete these minimum journeys with positive and negative packet/OS observations:

| Journey | Required evidence |
|---|---|
| Clean install and first consent | Unelevated UI, actual service identity, fresh settings/catalogue, no prior test seeding |
| Discover and update all relevant source families | Two revisions, mirror failure, partial/304/empty/rejected snapshots, honest coverage |
| Admit and choose a server | Real target TLS/status/body checks, failed-candidate negative, fresh-context selection |
| TUN connect and production confirmation | Actual intended route/interface/core, DNS and IPv4/IPv6 traffic observations |
| Candidate isolation during production tunnel A | Broken B fails independently; good B succeeds; concurrent workers remain isolated |
| Active health and failover | Core exit, target outage, uplink loss, cooldown, country/pin/exclusion constraints, guarded retry |
| Cancellation and state races | Cancel at every stage; old replies/completions/cleanup cannot resurrect or stop a newer session |
| Crash protection | Core, broker and UI crashes; verify the precise protection lifetime and no unsupported promise |
| Recovery and competing state | Corrupt/missing journal, unrelated routes/DNS/firewall/VPN, interrupted cleanup and restart |
| Sleep/reboot/network change | Epoch invalidation, no self-TUN change storm, bounded schedule coalescing, safe resumption |
| UI/tray/Exit | Manual selection, favorites, exclusions, timestamps, settings persistence, DPI/theme/keyboard, explicit disconnect-before-exit contract |
| Upgrade/uninstall/restore | Exact artifact identity, preserved owner data policy, removal of owned effects only |

Global availability of public proxy nodes cannot be guaranteed by one environment's live result. Report network/time/context; keep reproducible controlled suites separate and never publish their real credentials.

### Slice E: release and final independent closure

Build from a clean explicit commit/tree; preserve dependency/core/driver hashes, SBOM/notices, commands/results and artifact SHA-256/size. Publish a retrievable installer and practical Russian operating/recovery instructions. Verify the same artifact on another clean supported host. Signing unavailability is a disclosed limitation, not a fabricated signature.

Work in finite slices. Close child tasks/processes after each result. If a genuine external gate remains, complete independent work and leave a reproducible checkpoint naming the missing resource and exact next command. Do not report the full assignment as done while mandatory code or acceptance remains missing.

## 8. Closure report and commands

Create `docs/AUDIT_ROUND4_STATUS.md`, with one row for each R4-01 through R4-32 and explicit original F/R2/R3 links. Each row must identify: status; fix commit; production paths changed; regression/acceptance IDs; exact executed environment; evidence/run/artifact; remaining limitation.

Allowed states: `OPEN`, `IN_PROGRESS`, `IMPLEMENTED_NOT_VALIDATED`, `BLOCKED_EXTERNAL`, `VERIFIED`. A source-only change cannot verify a Windows packet behavior. Whole-product verification is different from a verified subcase. Record any rejected finding with a counterexample and rationale, never silent omission.

Representative regression commands after deliberate integration:

```sh
dotnet build AutoVpn.slnx -c Release --nologo
dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --no-build --filter 'FullyQualifiedName~IndependentRound4' --logger 'trx;LogFileName=round4.trx' --results-directory audit-results
dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --no-build --filter 'FullyQualifiedName!~IndependentRound4' --logger 'trx;LogFileName=baseline.trx' --results-directory audit-results
```

Set `AUTOVPN_MIHOMO_PATH` only to an independently verified appropriate-platform asset for native checks. Do not set it to a downloaded binary without verifying its identity. Run the measurement harness with `measure <output-directory>` and the native profile harness with `native <output-directory> <verified-linux-binary>`. The WPF harness is Windows-only and uses a fresh unconsented profile.

The final handoff must include the pushed fix commit/tree, exact current installer location/size/hash, actual test results with skips, before/after workload observations, packet acceptance evidence, recovery instructions and remaining risks. Audit workflows deliberately retain red outcomes when acceptance fails; do not count a `continue-on-error` step's displayed success as a passing test. Read the TRX and terminal workflow result.

## 9. Primary references and evidence locations

These references support protocol/API statements; the product findings derive from the pinned source and supplied test results.

- Production snapshot: https://github.com/alinescafs3mp-afk/vpn/tree/12c4b920a478a6f66945d7d7512d9ada319a559c
- Canonical independent run: https://github.com/alinescafs3mp-afk/vpn/actions/runs/37127984578
- Additional workload/UI/native-profile/metadata run: https://github.com/alinescafs3mp-afk/vpn/actions/runs/37127812801
- Initial baseline showing the additional timing-sensitive failure: https://github.com/alinescafs3mp-afk/vpn/actions/runs/37126680884
- Canonical tests: https://github.com/alinescafs3mp-afk/vpn/tree/a5f110b3c2f326e529f7d866ad9415062e6b64f5
- GitHub Git trees API, including tree identity and branch/tree inputs: https://docs.github.com/en/rest/git/trees#get-a-tree
- HTTP 204 semantics and Content-Length restrictions: https://www.rfc-editor.org/rfc/rfc9110.html#section-15.3.5 and https://www.rfc-editor.org/rfc/rfc9110.html#section-8.6
- HTTP/1.1 framing: https://www.rfc-editor.org/rfc/rfc9112.html#section-6.3
- Microsoft.Data.Sqlite connection/pooling configuration: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings
- Windows TCP ownership API: https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable
- Pinned core source: https://github.com/MetaCubeX/mihomo/tree/88dcbf7f1614a67c3b36b848ee3592dfa92ada36

Exact downloaded artifact identities are in `ROUND4_EVIDENCE_2026-10-03.json`. The accompanying ZIP preserves TRX, canonical test/harness source, source verification/history-screen records, workload measurements, native-profile results, upstream metadata and WPF renders. References and a green configuration parser are not substitutes for complete product acceptance.

**End of round-4 directive.**
