# AutoVPN: independent hard audit and completion directive, round 6

**Recipient:** Grok, implementation lead.  
**Owner communication:** Russian only. This directive and engineering identifiers are English.  
**Repository:** `alinescafs3mp-afk/vpn`  
**Date:** 2026-10-03; execution timestamps are UTC.  
**Audited production commit:** `bee26022245fb7fd1ede1d5edbe845db97e1205b`  
**Audited production tree:** `dd8a85742f54b18296fc82176093303949676097`  
**Independent branch:** `audit/round6-bee2602-independent`  
**Canonical regression commit:** `a2aa6f518ae21c0f5fc419e1b7c5badb0b5db31b`  
**Native-handshake commit:** `26abeab2c3e8bd2ceec56af60e91951af1128b68`  
**Verdict:** **V1 NOT ACCEPTED. Important fixes and real native handshakes are verified at their tested boundaries, but the installed Windows VPN is still unimplemented and cross-component correctness defects remain.**

Read this document, `ROUND6_EVIDENCE_2026-10-03.json`, the five previous audit directives, and `docs/IMPLEMENTATION_DIRECTIVE.md`. The original product specification remains binding. This is an implementation and completion assignment, not permission to stop after another counterexample-only or status-only commit. Preserve the existing stack, owner data, truthful reporting, and working security controls.

## 1. Evidence, scope and corrections

### 1.1 Exact source and execution

The complete pinned snapshot has **134 tracked files, 1,597,157 bytes, 30,391 text lines, and 68 C# files containing 21,057 lines**. Every exported file's Git blob hash was recomputed, and the root tree was reconstructed from names, modes and bytes. Review covered changes and critical unchanged paths in composition, UI, IPC, broker, discovery, refresh, probing, parsers, persistence, recovery, native processes, settings, packaging and tests. Inventory and byte identity are not exhaustive execution coverage or a proof of absence of defects.

All **325 distinct blobs reachable from 31 production-history commits**, totaling 4,011,230 bytes, were screened for five narrow signature categories: classic/fine-grained GitHub tokens, AWS access-key IDs, PEM private-key headers, and OpenAI-key-like strings. No matches were found. This is not general credential/entropy detection, not inspection of unreachable objects, and not a guarantee that every possible secret is absent.

The final audit branch adds **nine files**: three workflows, two test files, and two small harness projects. All 134 pre-existing production files were byte-compared and remained unchanged. Do not merge the audit branch wholesale. Port the useful tests and harnesses deliberately, with explicit native prerequisites and a reviewed CI configuration.

Independent runs initiated for this audit:

| Run | Commit | Purpose |
|---|---|---|
| `37139717970` | `aca29c4144b5e6e5ff2acf1ae4ac7e31cba5a128` | Exact production checkout, builds, full baseline, official core provisioning, advisory query and source/history export |
| `37140722817` | `a2aa6f518ae21c0f5fc419e1b7c5badb0b5db31b` | Canonical cross-platform adverse tests and controls |
| `37140597619` | `26abeab2c3e8bd2ceec56af60e91951af1128b68` | Six actual protocol handshakes and wrong-credential controls per OS |
| `37140515716` | `84ea01e4e11377f56a4f15f57e0c7601da6f0040` | First scale workload and actual WPF reopen; its initial broker fixture is superseded below |

Release builds succeeded. SDK **10.0.112**, runtime **10.0.12**. Linux was an Ubuntu 24.04 x64 hosted runner. Windows was **Windows Server 2025 Datacenter 10.0.26100**, not a Windows 11 acceptance machine. These are actual CI executions, not merely Grok's reported pass count or a local Linux cross-build.

| Executed suite | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| Existing Linux suite without provisioned native facts | 198 | 0 | 3 | 201 |
| Existing Windows suite without provisioned native facts | 187 | 11 | 3 | 201 |
| Full existing Linux suite with official native core | 211 | 0 | 0 | 211 |
| Full existing Windows suite with official native core | 196 | 15 | 0 | 211 |
| New round-6 cases, Linux | 4 | 17 | 0 | 21 |
| New round-6 cases, Windows | 4 | 17 | 0 | 21 |
| Additional actual handshakes, Linux | 6 | 0 | 0 | 6 |
| Additional actual handshakes, Windows | 6 | 0 | 0 | 6 |

The 21-case audit suite includes **one exploratory policy assertion, S603**, which is not an established specification violation. There are **16 failing binding-contract cases per OS plus that one exploratory failure**, not 34 distinct defects or a vulnerability count. All four canonical controls C601-C604 pass. The baseline intentionally excludes ten explicitly provisioned `Native_Round5` facts in no-core jobs; three other optional native facts report skipped. Do not confuse the two mechanisms or count absent native execution as a pass.

All **34 imported round-5 adverse/control cases and all 33 imported round-4 cases passed in all four baseline runs**. The ten `Native_Round5` profile/connection facts also passed on both platforms when provisioned. These are genuine improvements to preserve.

### 1.2 What the new native tests prove

Both platform jobs downloaded official **Mihomo v1.19.32**, checked the archive and executable SHA-256 against the committed manifest, and ran the real executable. Upstream source pin: `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`. Exact hashes are in the JSON and raw artifacts.

The new matrix is **VLESS/TLS, VMess/TLS, Trojan/TLS, VLESS/WebSocket/TLS, VMess/WebSocket/TLS and VLESS/gRPC/TLS**. Each case uses the production `NonTunCoreProbeTransport`, an owned official core as a controlled loopback peer, and an authenticated local HTTPS target. Correct credentials reach the target; incorrect UUID/password fails and does not increase the target's authenticated accept count. Both sides are real processes, not a successful configuration string or mock connection.

The proxy certificate is synthetic and self-signed, with an **explicit proxy-only insecure-certificate opt-in**. Target HTTPS still authenticates against fixture-local trust anchors. No system CA is installed. Therefore these cases prove protocol/transport and wrong-credential behavior under that declared fixture policy; they do **not** prove strict proxy-certificate validation, REALITY, Hysteria2, TUIC, H2, arbitrary upstream configurations, public-node availability, or active-TUN isolation. All listeners and dial maps are loopback; no TUN/WFP/route changes were made.

### 1.3 WPF and scale improvements

The actual WPF StartupUri created the initial window. Four navigation handlers ran at **880x640 and 1120x760, 96 DPI**, producing eight renders that were downloaded and visually inspected. Protection/LAN settings were toggled, the window/catalogue was closed and reopened within the same process, and the settings were read back and restored. **Normal reopen now succeeds without the diagnostic pool-clear workaround.** The profile remained unconsented. This is not a real tray Exit, whole-process restart, installation, multi-DPI/theme/keyboard or connected-user-journey test.

The repeated SQLite workload used 1,000, 5,000 and 10,000 synthetic nodes, three single-assessment writes at each size. At 10,000 nodes, the first recorded run measured **1.8131 / 0.7909 / 0.8117 ms on Linux** and **36.5226 / 12.0726 / 11.1158 ms on Windows**, with **4,688 managed bytes allocated per write** and no unrelated secret re-protection. The prior round's roughly 37.68 MB allocation per assessment is no longer reproduced by this workload. This is substantial progress consistent with the new targeted SQL path, not a portable speedup guarantee or a sustained full-catalogue/DPAPI/UI benchmark. Other mutations still use copying and broader comparisons.

The NuGet transitive advisory query returned zero vulnerable package entries across nine projects. It does not validate native/Go/Wintun dependencies, an installer, signatures, an SBOM, or distribution compliance.

### 1.4 Auditor corrections and interpretation

The first audit-owned core double incremented its start counter inside a null-conditional callback. Without a callback, the increment did not run and ordinary starts could incorrectly throw. This corrupted several initial broker/control outcomes in run `37140515716`; those outcomes are **not product findings**. The double was corrected before the canonical run. B605 was also refined to omit a per-connect LAN override, so it tests a global policy change during Arm rather than contradicting an intentional session override. The workload/WPF results from the earlier run were independent of this double.

**S603 correction:** original specification sections on disabled families explicitly constrain node eligibility, but do not unambiguously forbid background metadata downloads. S603's expectation of zero HTTP calls is stronger. Preserve its observed behavior as a product-policy question; **do not count it as a proven safety/specification defect or require it to pass without defining the intended control.** A disabled source for selection and a paused source for downloads can be separate concepts. Do not weaken the already-required exclusion from automatic selection.

Windows baseline failures require classification, not wholesale suppression. The no-core run has five IPC cases, five file-lifetime/test-cleanup cases and one `/usr/bin/pkill` fixture. The full native run adds three platform-specific native fixtures and one elapsed-time assertion. `PinnedLinuxCoreValidatesSyntheticNonTunProfileWhenProvided` compares a Linux hash with the verified Windows binary: this is not evidence that the binary is tampered with. The correctly platform-provisioned native suites pass. Inspect each other failure's actual assertion/cleanup stack before calling it a production defect. Preserve timing-sensitive evidence without claiming a semantic regression solely from a runner threshold.

**Not executed:** installed Windows TUN/WFP/SCM positive acceptance; packet-observed production DNS/IPv6/crash protection; Windows 11 install/upgrade/uninstall/reboot/sleep/two-account authorization; every protocol handshake; active-TUN/candidate isolation; live public subscription/node availability or speed; long-duration soak; full native supply-chain/SBOM/signature closure. Missing service/guard/installer code is an implementation gap, not merely an external testing blocker.

Downloaded evidence archives were CRC-checked and their SHA-256 values matched GitHub artifact metadata. Preserve the supplied bundle because GitHub artifact retention is finite.

## 2. Mandatory remediation

Labels: **EXECUTED** identifies the tested contract; **STATIC** means source/call-graph evidence without its full deployed consequence; **GAP** means absent implementation or acceptance. P0/P1/P2 are engineering priorities, not CVSS. Paths and line anchors refer to the audited commit. In-process core/guard doubles do not prove live packet leakage.

### R6-01 [P0] Finish the real Windows product composition

**GAP.** `src/AutoVpn.Service/Program.cs`, `Broker/ICoreController.cs`, `UnavailableNetworkGuard.cs`, service project and packaging scripts.

The executable still constructs `RefusingCoreController` and `UnavailableNetworkGuard`, runs a console lifetime and explicitly installs neither TUN nor filters. Native non-TUN successes do not close this. Implement the SCM-compatible service, protected asset resolution, owned core lifecycle, actual TUN readiness/production-path validation, guard and startup reconciliation. Keep explicit refusal on genuinely unsupported or invalid configurations, not as the only supported-Windows path. Do not restart the stack or expand into a general networking platform.

**Close:** a clean supported Windows machine installs, launches an unelevated UI, connects through actual TUN, reports verified state, disconnects and restores only owned changes. Missing/tampered assets cannot yield Connected. A full installer and packet-level protection remain required.

### R6-02 [P1] Complete authorization and the catalogue-to-broker boundary

**STATIC/GAP.** `Broker/PipePeer.cs`, `LocalIpcServer.cs`, service/desktop entrypoints and IPC contracts.

Windows callers are still rejected rather than authenticated using their actual SID/session. Owner consent and a measured desktop catalogue do not yet reach an appropriately isolated service as an authorized bounded runtime set. Implement restrictive ACLs, remote rejection, authenticated server discovery, owner lease, exact boot/request/operation identity, DPAPI/store ownership and the narrow typed handoff. Do not make a writable full catalogue privileged authority, accept arbitrary YAML/paths/commands, or move broad subscription parsing/fetching into LocalSystem.

**Close:** real service account, owner account, second user/session, stale lease, spoofed server and malformed runtime set are tested. Fresh first-run consent and a real measured candidate work without seeded broker data.

### R6-03 [P1] One ordered publication authority must serve routine and on-demand probes

**EXECUTED R601 and R602, both success/failure orderings.** `ProbeCoordinator.cs:47,304,385`; `Catalogue.cs:233`; `SqliteCatalogue.cs` copy/commit paths.

`AdmitIfStaleAsync` bypasses the new publication counter entirely. An older on-demand result overwrites a newer opposite result. Routine probing also loses ordering when a real SQLite favorite update copies the catalogue: `CloneNode` omits `ProbePublication`, the next attempt reuses a counter value, and a delayed old result passes the equality check. Merely copying the field is not the complete cross-path/atomicity contract.

**Fix:** reserve a per-node attempt in the authoritative catalogue/coordinator, not a mutable DTO field. Use a shared commit operation for every probe path that atomically verifies boot/attempt sequence, node/digest, epoch, policy and current ownership. Ordinary metadata changes must preserve ordering; newer observations must supersede older ones in both directions. Keep the efficient single-row assessment write.

**Close:** all four controls pass with real SQLite, interleaved favorites/settings/source updates, routine versus on-demand work, cancellation and restart. Do not restore whole-database rewrites or permit unsynchronized in-memory publication to avoid the race.

### R6-04 [P1] A proof is a consumed attempt, not a string remembered only on the current row

**EXECUTED R603/R604.** `ProbeCoordinator.cs:439,450`, `AssessmentSnapshot.ProofToken`.

An old epoch-1 proof is rejected once in epoch 2, but that refusal writes an assessment without the previous token; the next replay is accepted as fresh epoch-2 evidence. Reusing the same proof after 31 minutes in the same epoch also renews availability. These are internal transport-contract tests, not demonstrations that a public proxy can inject arbitrary proof records.

**Fix:** bind observation to a uniquely reserved live attempt/worker and immutable requested context. A rejected result must not erase replay protection. Expiry, clock changes, storage copying and retry cannot turn a consumed result into new evidence. Use exact bounded attempt/boot sequencing rather than unbounded token history or a blacklist of specific test strings.

**Close:** repeat an old proof zero, one and many times before/after failure, epoch change, cancellation and restart; none refreshes health. A genuinely new successful attempt remains accepted.

### R6-05 [P1] Implement continuously maintained admission, not a short tail of source refresh

**STATIC/GAP.** `ProbeCoordinator`, `CatalogueCoordinator.ProbeAsync`, `Scheduling.cs`, desktop timers and measurement storage.

The pipeline still cannot keep a large verified catalogue fresh independently of source updates. The default path uses one configured target and a short sequential pass; per-pass pacing is not a resumable, fair maintenance scheduler. A single elapsed sample including core startup is not a median ping. On-demand checks also need their own deadline, ordered publication and budget treatment.

**Fix:** one long-lived unelevated work owner with resumable queues, freshness deadlines, active/standby priority, bounded per-endpoint concurrency, cancellation ownership and retry/backoff. Initial general-web admission must satisfy the original two-target contract. Separate candidate/configuration failure from missing core, target outage, uplink failure and owner cancellation. Use completion time and monotonic durations; preserve bounded measurement history.

**Close:** sustained old-healthy plus new-candidate work progresses between source updates, pending nodes stay out of the usable list, and target/uplink incidents do not condemn unrelated nodes. Prove candidate-local behavior while another actual tunnel is active, not only loopback without TUN.

### R6-06 [P1] Bind fallback discovery as rigorously as the successful path

**EXECUTED S601; stable C602 passes.** `CatalogueCoordinator.cs:231,355`, `GithubTreeParser`, reviewed registry and cached discovery.

The successful branch-to-commit-to-immutable-tree path is improved. When commit resolution fails, the code returns the bootstrap pin with no tree identity and fetches the mutable branch tree. S601 pairs an unresolved old pin with current branch paths and reports complete discovery.

**Fix:** fallback must fetch the pin's immutable tree or reuse a known coherent cached commit/tree/snapshot pair. Otherwise return explicitly incomplete/degraded coverage. Validate returned identities and object types on every path, including 304 and cache reuse. Cancellation or a superseded refresh cannot publish older discovery success/failure into newer state.

**Close:** stable/moving branch, failed commit lookup, failed tree lookup, bootstrap pin, cached 304, malformed object, missing blob and superseded request all produce coherent identities or honest incomplete results. Do not undo successful current-HEAD resolution.

### R6-07 [P1] Represent valid empty and missing snapshots independently of node membership

**EXECUTED S602.** `CatalogueCoordinator.DownloadAsync`, `RefreshMerge.Ingest`, `SourceLedger`, catalogue schema.

The previous lost-catalogue/surviving-ledger bug was improved by checking node membership. That cannot represent an accepted empty subscription: its valid 304 causes an unnecessary unconditional third fetch. Conversely, metadata without its canonical snapshot still must not masquerade as recoverable content.

**Fix:** persist a versioned artifact-snapshot record including a valid-empty marker, accepted canonical content identity, provenance/membership and validators. Atomically publish it with the catalogue state, or provide a tested crash-consistent journal/recovery protocol. Do not store live raw credentials unprotected just to cache HTTP bytes.

**Close:** valid-empty 304 needs no repeated full download; missing/corrupt canonical content triggers bounded unconditional refetch; failed transactions cannot advance validators; malformed/partial source data never becomes an authoritative empty snapshot.

### R6-08 [P1] Schedule live artifact identities, not historical raw URLs

**EXECUTED S604.** `SourceLedger.cs:161,177`; `RefreshScheduler`; desktop one-minute pulse.

Old immutable raw-content URLs remain in `LiveSuccessStamps`, even after the current revision and discovery have succeeded. Their old timestamps can keep refresh due on every pulse. Filtering only old `/git/trees/` URLs does not retire the old raw revisions.

**Fix:** give each logical artifact a stable scheduling identity with one current accepted revision and bounded history. Reconcile renamed/removed artifacts and queue position by identity, not incidental list index. Separate last attempt, successful check, content change and retry-after; a failed or never-successful source must not be hidden by another success or retried in a storm. Serialize ledger writers and handle crash/restart consistently.

**Close:** repeated upstream revisions, unchanged 304s, partial failures, empty families, restored catalogues and reordered discovery preserve fair bounded scheduling without minute-by-minute full refresh loops.

**Policy clarification, not a proven defect:** S603 observes an HTTP call for a disabled family. Decide and document whether the existing switch disables selection only, or also pauses downloads. Preserve the original mandatory eligibility restriction. A separate explicit pause-download control is acceptable within the simple UI. Do not blindly port S603's stronger assertion into a release gate.

### R6-09 [P2] Complete source coverage, meaningful mirror fallback and aggregate limits

**STATIC/GAP.** Reviewed source/family registry, `CatalogueCoordinator.DownloadAsync/DocumentEligible` and `RefreshMerge`.

Fair rotation and parsing before mirror acceptance improved, but the workflow still reserves a maximum-sized artifact for each attempt rather than enforcing one actual aggregate byte/time budget across mirrors and retries. `DocumentEligible` and final publication use different acceptance predicates; align document validity/completeness/limits so a partially parseable but nonpublishable primary cannot prematurely suppress a good reviewed mirror. Account for newly discovered or unmatched relevant families rather than silently equating the seed registry with all sources.

**Fix/close:** use one typed artifact outcome through download, parse, fallback and commit. Exercise tiny/large/over-limit files, malformed 200/HTML, mirror disagreement, all-representation overlap, new families and cancellation. Retain last-good nodes on incomplete work, expose coverage/progress, and ensure every eligible artifact eventually advances under a real application restart lifecycle.

### R6-10 [P1] Failover must own partially spawned replacement resources

**EXECUTED B601 with strictly scoped core ownership.** `BrokerEngine.ReportHealthAsync:559`, `_ownedOperationId`, cleanup state.

Initial Connect now records cleanup ownership before Start, which fixes earlier cases. Failover does not apply the same contract: replacement Start acquires its resource then throws IOException, and later Disconnect targets the old identity. The test retains the replacement resource after a success-shaped Disconnect. This is not an observed real TUN leak.

**Fix:** reserve cleanup identity before every initial/retry/failover effect and retain it even when Start throws or Stop fails. Track all outstanding owned resources, not just the most recently usable session. Separate proof/usability, selected node and cleanup handles. Normalize expected failures into truthful retryable cleanup states; never kill arbitrary processes by name.

**Close:** partial spawn/readiness exceptions and failed stops in every path remain recoverable by the exact original handle. Old cleanup cannot kill a newer instance, and Disconnect cannot report complete while owned resources remain.

### R6-11 [P1] Use completion-time freshness when committing a replacement

**EXECUTED B602.** `BrokerEngine.ReportHealthAsync`, `IsCurrentlyEligible:1044`, injected clock.

The standby is fresh at selection; the test advances time by 65 seconds during Start. The switch still commits it because a pre-await timestamp is reused in the post-await eligibility decision.

**Fix:** re-evaluate current freshness and all mutable eligibility using completion-time context within the serialized commit. Revalidate through the required candidate path where necessary; do not extend the 60-second requirement just to make a delayed Start pass. Preserve the selected session only when it actually remains valid and alive.

**Close:** delayed starts, clock changes, network epochs and expired assessments cannot confirm stale standby evidence. Correct fresh replacement and an actual production-path verification remain successful.

### R6-12 [P1] Automatic failover cannot inherit an earlier manual exclusion override

**EXECUTED B603; deliberate manual C604 passes.** `BrokerEngine._selectionPurpose`, switching and `ConfirmProduction:412`.

A manually selected initial node leaves `_selectionPurpose=Manual`. Automatic failover does not replace that purpose. Excluding the automatically chosen standby before confirmation is therefore treated as an allowed manual override and it becomes Connected.

**Fix:** bind purpose and explicit owner intent to each attempt, not a mutable session-global remnant. Automatic replacements use automatic policy regardless of how the first node was chosen. Keep the original permitted manual exclusion override, without bypassing hard security/source/country restrictions.

**Close:** fresh/stale deliberate manual selection works, automatic selection and failover never acquire manual bypass rights, and policy changes during admission/Start/confirmation still apply.

### R6-13 [P1] Validate the full current policy before Arm and at confirmation

**EXECUTED B604/B605.** Profile construction, policy capture after Arm, `SelectionHeld:1031`, `ConfirmProduction`.

B604 changes current country metadata from DE to FI after Start under a strict-DE policy; confirmation still accepts it. B605 changes the global LAN policy to disabled during Arm, with no per-connect override: YAML built before Arm still permits LAN DIRECT routes, while the policy stamp captured afterwards matches the new settings and confirmation succeeds.

**Fix:** capture the immutable effective session/attempt policy before profile generation and effects. Validate its revision and current node/source/country/digest/epoch/capability restrictions at every commit, not only selected flags. An authorized explicit session override and a later global revocation need clear, consistent precedence. Reject/rebuild safely on change; retain cleanup ownership.

**Close:** changes at preflight, Arm, Start, production validation and switch cannot confirm a profile generated under a contradictory policy. Advertised country remains source metadata, not verified exit geography; unknown/conflicting values cannot silently satisfy strict mode.

### R6-14 [P1] Bound effect I/O and make safety commands responsive through the real pipe

**STATIC/GAP.** `LocalIpcServer`, `IpcDispatcher`, guard Arm under broker locks, Stop/recovery waits.

Frame limits alone do not make long core/guard work cancelable or responsive. Synchronous waiting for the engine or a duplicate request and effect I/O inside authority locks can monopolize safety control. Transport timeouts must not be interpreted as proof that no effects occurred. Existing Windows IPC remains unimplemented, not validated by Linux peer checks.

**Fix/close:** serialize authority decisions, perform owned slow work outside the critical section, return operation tickets and post context-bound completions. Give arm/start/stop/kill-and-wait/recovery their own bounded deadlines and retryable uncertain states. Reserve safety capacity. Test actual Windows clients issuing query/Disconnect during blocked work, saturated partial clients, duplicate requests, service shutdown and cancellation. Keep protection until an authorized safe release.

### R6-15 [P1] Replace replay exceptions with an exact bounded sequence contract

**EXECUTED I601.** `IpcDispatcher`, safety cache, retired IDs and lifetime Bloom filter.

The previous Bloom false-positive denial of a fresh safety request was fixed by bypassing that filter for safety commands. After the exact safety cache/tombstones age out, the same old Disconnect ID executes again. I601 sends 9,000 side-effect-free synthetic safety requests, then observes a second handler execution for the original ID. Broker revision checks mitigate some real cases; this is not a demonstrated old request disconnecting a new installed session.

**Fix:** authenticated boot/lease-scoped exact monotonic sequence windows, idempotent outcomes, uncertain-effect semantics and explicit renewal. A probabilistic filter cannot be the final authorization for either accepting a retired request or denying a fresh safety request. Do not restore a lifetime command cap or unbounded history.

**Close:** long sessions accept fresh safety commands and reject retired/reordered/duplicate effects across cache eviction, reset and restart, through the real authorized transport. Tie Disconnect to intended generation/owner state.

### R6-16 [P1] Contradictory process state must block a success-shaped Exit

**EXECUTED I602.** `UiSessionReducer.FromSnapshot:48`, `PlanExit:74`, mailbox and desktop Exit flow.

A snapshot says Disconnected while `CoreRunning=true` and protection=false. Verified-disconnect is correctly false, but `SafetyDisconnectAvailable` and `PlanExit` ignore the remaining process flag and permit closing. This is an adverse snapshot/state contract, not proof that the current refusing service naturally emits that combination.

**Fix:** carry process/effect/recovery truth into the session model. If owned work remains or the snapshot is contradictory, keep an authorized safety action and require a bounded disconnect/recovery acknowledgment before claiming clean Exit. Preserve last-known protection across transport loss and correlate replies with the current request/transport generation. Do not equate window visibility or a nominal phase with process termination.

**Close:** contradictory/unknown snapshots, late replies, reconnects, core/broker/UI crashes and explicit Exit cannot hide unresolved resources. Window-close-to-tray remains distinct from explicit Exit and application crash.

### R6-17 [P0] Finish ownership-aware Windows protection and recovery

**GAP with partial journal fixes preserved.** Guard/recovery interfaces, `EffectJournal`, `Recovery/Program.cs` and admin test script.

The markers, quarantine handling and deduplicated removal accounting have improved, but no real Windows filter/route/DNS reconciliation exists. Preserve uncertainty for missing/corrupt journals until actual owned OS state has been inspected. A journal row, an armed Boolean and `strict-route` alone are not evidence of protection across crashes.

**Fix/close:** narrow owned provider/filter/route/adapter identities, durable write-ahead effect intent, rollback and restart reconciliation. Check core/broker/UI death, partial Arm, failed restore, reboot and competing changes. Release protection last on explicit disconnect. The standalone recovery utility must work without a healthy user catalogue and never globally reset unrelated firewall/routes/DNS. Perform destructive testing only on an authorized isolated Windows environment with independent recovery access.

### R6-18 [P1] Prove DNS, IPv6, destination safety and active-tunnel probe isolation

**STATIC/GAP.** Native profile DNS/TUN sections, endpoint safety, physical-network epoch detection and candidate workers.

Loopback non-TUN handshakes do not establish the core's internal resolver routing, user DNS containment, IPv6 protection, hostname rebinding control or isolation from an already active TUN. Literal-address checks alone do not bind approved A/AAAA results to actual dials.

**Fix/close:** separate narrow endpoint/bootstrap resolution from user DNS through the chosen outbound; prevent recursion and unauthorized private/metadata destinations; protect or explicitly block advertised address families. Avoid treating self-created TUN changes as new physical-network epochs. Use controlled resolvers/targets, direct-path negative controls and packet captures before/during/after connect, failover, crash and recovery. Prove broken candidate B cannot pass through working tunnel A and the inverse. Never weaken TLS or add a broad DIRECT fallback to make probes work.

### R6-19 [P2] Complete semantic compatibility beyond the now-passing native matrix

**STATIC/GAP; new six-case native successes credited.** URI/Clash/Xray importers, canonicalizer, `NodeWireFactory`, profile generator, compatibility report.

Keep successful protocol mappings and the repaired malformed-input cases. The matrix still does not prove every nested option, source wrapper, REALITY/QUIC/H2 path, UDP capability or strict proxy-certificate configuration. An accepted native profile is weaker evidence than an actual authenticated handshake; a canonicalizer change cannot silently inherit old health.

**Fix/close:** explicit typed compatibility matrix from each supported representation through canonical identity, runtime wire and emitter. Preserve opaque credential/path/transport bytes and protocol defaults; reject unknown meaningful nested options and duplicate/conflicting keys consistently. Add controlled positive/negative handshakes for each advertised combination, plus bounded byte-decoding/parser mutation and migration tests. Distinguish unsupported from failed/network-down without silently changing requested security semantics.

### R6-20 [P2] Preserve targeted SQLite writes while completing the persistence contract

**EXECUTED performance/reopen controls; STATIC remaining paths.** `SqliteCatalogue.ApplyAssessment:122`, copy-based metadata paths, `InspectExisting:600`, source/assessment restore.

The single-row assessment transaction and pooling/header changes are real improvements. Do not keep the previous claim that every assessment rewrites/re-encrypts the full database. Other mutations still copy/compare a large catalogue and may share mutable semantic objects. Stale-writer rejection is not a complete owner synchronization/handoff strategy, and catalogue/ledger consistency remains incomplete.

**Fix/close:** immutable published snapshots or one serialized authority, atomic compare-and-commit assessment context, consistent settings/digest validation on load, conflict refresh/reconciliation and bounded retention that protects favorites/current sessions. Inject disk/commit failures, backup/WAL corruption, upgrade/downgrade and concurrent readers/writers. Test all connection and quarantine handles under Windows. Diagnose the five remaining file-lifetime/test-cleanup failures individually; the now-passing ordinary WPF/SQLite reopen must not regress.

### R6-21 [P1] Close remaining native-process, temporary-secret and diagnostic boundaries

**STATIC.** `MihomoProcessController.ValidateAsync`, `ProbeWorker`, temporary profiles and redaction.

The validator still uses unbounded `ReadToEndAsync`, hashes then executes a path without an installed protected-path contract, and on cancellation kills without a completed owned kill-and-wait before deleting the directory. Caller cancellation and timeout are conflated in its result. The improved worker drain must not be reverted, but that does not automatically fix every other process path.

**Fix/close:** protected verified executable/DLL locations, narrow ACLs, scoped handles and temp directories, continuous bounded-retention drains, child termination confirmation and an owned retry/reaper path. Separate timeout/cancellation/error reasons and preserve original plus cleanup failures. Redact structurally before retaining/exporting output; test raw/quoted/encoded/multiline canaries. Never commit live profiles, credentials, databases or packet captures. Synthetic test keys stay ephemeral and do not justify trusting arbitrary certificate input.

### R6-22 [P2] Measure real traffic and expose honest latency, speed and capability values

**STATIC/GAP.** Probe byte budgets, `BoundedTransfer`, ranking, live traffic and settings.

Small payload counters or worst-case source reservations do not account for all traffic across setup/TLS/retries/mirrors/on-demand work. A helper with a byte cap is not a connected UI throughput test. Unknown bandwidth, exit location and UDP capability must remain unknown.

**Fix/close:** one bounded accounting policy with reserved essential session-health capacity, metered-network behavior, manual throughput confirmation, actual payload/time measurements and durable conservative daily rollover. Separate proxy latency, benchmark Mbps and live transfer rate. Enforce monotonic deadlines including retries; add sustained resource/queue/traffic measurements rather than inferring long-run behavior from the SQLite micro-workload.

### R6-23 [P2] Finish the simple Russian desktop instead of static placeholder views

**EXECUTED narrow WPF pass; STATIC/GAP remaining UI.** Desktop XAML, view/session models, settings, tray and actual data flow.

The real four-page window and normal settings reopen work in the tested harness. Manual server selection, usable favorites/source controls, full metrics, live resync, polished system/light/dark themes, keyboard/accessibility, scaling and tray/session lifecycle are not closed by eight screenshots. The current text views are not the promised interactive measured catalogue.

**Fix/close:** complete the original four-view design with real commands and authoritative state, clear loading/empty/error/protected-outage states, timestamps and actionable reasons. Keep advanced controls out of the default flow. No consent means no automatic external work. Exercise actual clicks/keyboard, first run, import/probe progress, cancellation, chosen-node connect, failover, settings restart, source scope and explicit Exit on supported Windows. Use screenshots as visual evidence, not a substitute for functional assertions.

### R6-24 [P0] Deliver a retrievable installer and a real acceptance gate

**GAP/STATIC.** `scripts/package.ps1`, `test-windows-admin.ps1`, `verify-release.ps1`, CI and handoff documents.

Packaging still publishes folders explicitly labeled not an installer, without core/driver installation or service registration. Admin/release verification scripts still exit with NOT_RUN. This missing implementation is not closed by source commits or by calling an unsigned folder a release.

**Fix/close:** clean reproducible Windows packaging with protected installation paths, reviewed core/driver assets, service identity/ACLs, uninstall/upgrade rollback and the owner's data-preservation policy. Publish retrievable artifact size/hash and exact source/dependency inputs. Separate unsigned authorized test builds from signed public releases honestly; no signing bypass or fabricated signature. Pass install/connect/update/failover/disconnect/recover/upgrade/uninstall on Windows 11 with packet-level safety evidence before release acceptance.

### R6-25 [P2] Make permanent CI, supply-chain records and closure reports match reality

**EXECUTED baseline evidence plus STATIC/GAP.** `.github/workflows/ci.yml`, platform fixtures, manifests, notices and evidence reports.

Permanent native Linux CI is a genuine addition. Windows coverage and portable native prerequisites remain incomplete. Keep unit/native/controlled-integration/admin/UI suites separate. Replace Linux-hardcoded hash/path expectations in platform-general tests; do not skip whole Windows groups or loosen verification. Correct test cleanup ownership and use deterministic clocks/bounds where possible. Preserve real flaky/timing outcomes with diagnoses, not repeated runs selected only for green.

Generate a transitive SBOM and accurate notices/provenance for what is actually shipped, including native core/Go/Wintun components. A locally recorded hash is identity, not an upstream signature. Bind each result to clean source/tree/artifact identities and distinguish IMPLEMENTED, WIRED, TESTED, NOT_RUN and READY. Preserve reviewed action/dependency pins and minimal workflow permissions. The NuGet query and narrow history screen are scoped observations, not blanket clearance.

**Close:** reproducible platform suites, correct prerequisite reporting, published evidence and artifact identities agree with the actual code/package. A reviewer can rerun the original golden journeys and adverse controls without guessing environment variables or trusting status prose.

## 3. Required regression interpretation

| Cases | Required interpretation / closure |
|---|---|
| R601, two orderings | On-demand late result cannot overwrite newer evidence. |
| R602, two orderings | Real SQLite metadata mutation cannot reset or reuse live attempt identity. |
| R603 / R604 | Rejected cross-epoch or consumed same-epoch proof never refreshes availability. |
| S601 | Unresolved fallback pin cannot label an unrelated branch tree complete. |
| S602 | Persisted valid-empty artifact can legitimately reuse 304. |
| S604 | Historical raw URLs do not participate in current refresh scheduling. |
| B601 | Partial failover spawn retains exact cleanup ownership after an exception. |
| B602 | Post-Start freshness uses current time. |
| B603 | Automatic switch does not inherit previous manual-selection privilege. |
| B604 / B605 | Current country and pre-effect effective policy are checked before confirmation. |
| I601 | Retired safety request does not execute again after bounded-cache eviction. |
| I602 | Remaining core resource/contradictory state prevents a clean Exit claim. |
| C601-C604 | Positive controls for SQLite reopen, stable discovery, scoped connect/disconnect and deliberate manual selection remain passing. |
| S603, exploratory only | Resolve disabled-selection versus paused-download semantics; not a proven violation of the original specification. |
| Six new native cases | Preserve real protocol/transport and wrong-credential controls under their explicitly declared proxy-certificate fixture policy. |

Use the canonical source, not the initial faulty broker double. Do not reverse assertions, special-case fixture IDs/hosts or use a success-returning adapter to close a gate. Expand with production transport and OS tests where the corresponding functionality exists. No one test count replaces the original acceptance matrix.

## 4. Execution order: do not stop after another Slice A

### Slice A: fix bounded, independently reproducible contract defects

Port the binding-contract regressions, retain positive controls, and fix shared probe publication/proof lifetime, coherent fallback/empty snapshots/scheduling, failover ownership/freshness/purpose/policy, replay sequencing and Exit truth. Do not import S603 as an unconditional requirement without the explicit product-policy decision. Preserve targeted SQL writes and the now-working native/WPF paths. Commit coherent tested increments and report this slice as a slice, not v1 completion.

### Slice B: complete the unelevated end-to-end catalogue and usable UI

Deliver clean first-run consent -> all relevant source discovery -> bounded download/mirror/parse -> durable snapshot -> fresh local multi-target verification -> actual usable list -> explicit selected-node request. Add independent health maintenance and settings/source policies with real UI commands. Keep fetching/parsing and the full catalogue unelevated. Show actual pipeline progress and truthful unknown values.

### Slice C: implement service authority, owned runtime and protected lifecycle

Implement the actual Windows service and authorization, narrow runtime handoff, process/effect ownership, responsive asynchronous control, journal/guard and recovery. Write the production paths and non-destructive tests rather than leaving perpetual refusing adapters. Preserve Linux-host no-network-mutation rules. Use Windows hosted CI for what it can actually validate; it is not a replacement for Windows 11 destructive networking acceptance.

### Slice D: execute installed networking acceptance safely

On an authorized isolated Windows 11 environment with independent recovery, execute TUN connectivity, DNS/IPv6/active-probe isolation, core/broker/UI failures, failover, sleep/network changes, two-user authorization and owned-state restoration. Add direct negative controls and packet evidence. Do not run destructive network tests on the only machine carrying the control session. If access is missing, identify the exact remaining prerequisite after finishing independently achievable code/tests.

### Slice E: package, install from scratch, and hand off the real product

Build the installer from a clean pinned source, publish verified artifact/checksums, complete notice/SBOM/provenance and install/upgrade/uninstall/recovery instructions, then rerun the original user journeys from the delivered package. Complete the UI matrix and a sustained resource/refresh/health soak. Do not expand v1 with unrelated services, features or architectural rewrites.

The finish line remains: the owner installs, opens the application, receives an actually verified pool, connects through TUN, survives refresh/failure, and can safely disconnect/recover. Green source-level tests alone do not meet it. Work in finite slices, clean up child tasks/processes, and retain a resumable checkpoint instead of an unbounded autonomous loop.

## 5. Closure ledger and carry-forward scope

Create `docs/AUDIT_ROUND6_STATUS.md`, one row per R6-01 through R6-25. Include fix commit, production paths, exact regression IDs, executed environment/run/artifact, remaining limitation and next executable deliverable. Status values: OPEN, IN_PROGRESS, IMPLEMENTED_NOT_VALIDATED, BLOCKED_EXTERNAL, VERIFIED. A missing implementation is OPEN/IN_PROGRESS, not automatically BLOCKED_EXTERNAL. Windows behavior is VERIFIED only after its real Windows gate runs. Keep this audit baseline immutable; put justified corrections in the closure ledger with evidence.

Prior findings remain binding, with the following grouping to prevent them disappearing behind new IDs:

| Prior group | Continue under | Required retained distinction |
|---|---|---|
| R5-01/02; original F01/F05/F10 | R6-01/02/17/24 | Missing service/guard/handoff is different from unavailable OS evidence. |
| R5-03 through R5-06; F02/F16/F22/F25/F28 | R6-06 through R6-09 | Successful immutable lookup and mirror fixes stay; fallback/snapshot/scheduling completion remains. |
| R5-07/08; F14/F15/F27 | R6-03/04/05/22 | Routine helper ordering is not universal publication or maintained availability. |
| R5-09 through R5-12; F06-F09 | R6-10 through R6-14 | Deliberate manual selection works; automatic attempts need distinct policy and cleanup ownership. |
| R5-19; original F15/F27/F28 | R6-05/22 | Traffic/clock fixes do not substitute for sustained measured budgets and honest metrics. |
| R5-13/14; F04/F06 | R6-14/15 | Fresh safety requests and replay rejection both need exact bounded semantics. |
| R5-15/25; original F03/F26/F34 | R6-13/16/23 | Working window/reopen is not a connected interactive product. |
| R5-22/23; original F11-F13 | R6-17/18 | Markers and non-TUN tests are not actual OS reconciliation/packet containment. |
| R5-16/17/18; original F17-F21 | R6-19/20/21 | Passing selected real handshakes do not prove every imported option or migration. |
| R5-20/21; original F23/F24 | R6-03/07/20 | Targeted assessment write/reopen are fixed; authority, snapshots and all-operation scaling remain. |
| R5-24/26/27/28; original F29-F33 | R6-21/24/25 and Slices A-E | Clean logs, safe process cleanup, package provenance and real acceptance remain required. |

For every previous finding not named individually in this grouping, map it explicitly in the ledger or show its existing verified closure. Do not silently discard prior negative controls. A justified correction such as S603 is welcome; an unsupported green status is not.

The final report to the owner must be Russian and identify the exact pushed commit, clean source/tree, package location/size/SHA-256, platform/core/driver versions, actual command/results, unavailable gates and recovery steps. Do not claim native configuration validation, controlled non-TUN HTTPS and installed TUN/WFP are the same evidence level. Do not force-push, overwrite unrelated owner work or commit live credentials/runtime databases.

## 6. Primary references and rerun entry points

Repository source and test evidence are pinned above. External implementation references were checked as primary documentation; validate against the exact pinned version rather than copying changing examples blindly.

- Production source: https://github.com/alinescafs3mp-afk/vpn/tree/bee26022245fb7fd1ede1d5edbe845db97e1205b
- Baseline: https://github.com/alinescafs3mp-afk/vpn/actions/runs/37139717970
- Canonical regressions: https://github.com/alinescafs3mp-afk/vpn/actions/runs/37140722817
- New actual handshakes: https://github.com/alinescafs3mp-afk/vpn/actions/runs/37140597619
- First measured SQLite/WPF workload: https://github.com/alinescafs3mp-afk/vpn/actions/runs/37140515716
- Microsoft.Data.Sqlite connection/pooling/cache semantics: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings
- Official Mihomo listener reference: https://wiki.metacubex.one/en/config/inbound/
- VLESS controlled listener schema: https://wiki.metacubex.one/en/config/inbound/listeners/vless/
- VMess controlled listener schema: https://wiki.metacubex.one/en/config/inbound/listeners/vmess/
- Trojan controlled listener schema: https://wiki.metacubex.one/en/config/inbound/listeners/trojan/

Canonical audit branch commands, with native prerequisites provisioned and hash-verified by the supplied workflow:

```text
dotnet build AutoVpn.slnx -c Release --nologo
dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --filter FullyQualifiedName~IndependentRound6Tests
dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --filter FullyQualifiedName~Round6NativeHandshakeTests
dotnet run --project tests/AutoVpn.AuditRound6Measure -c Release -- audit-measure
dotnet run --project tests/AutoVpn.AuditRound6Wpf -c Release -- audit-wpf
```

The first test command intentionally remains red on this audited production snapshot, and S603 must be interpreted as documented. WPF requires Windows. Native tests require the exact per-platform executable/hash; do not bypass that prerequisite or turn an absent binary into a pass.

**Start by inspecting the current repository and preserving concurrent owner work. Complete Slice A, then continue to the real product slices. Communicate meaningful progress and genuine blockers in Russian.**
