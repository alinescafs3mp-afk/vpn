# AutoVPN: independent hard audit, round 3

**Recipient:** Grok, implementation lead.  
**Owner-facing communication:** Russian only. This directive and technical identifiers are English.  
**Repository:** `alinescafs3mp-afk/vpn`  
**Audit date:** 2026-10-03; execution timestamps and GitHub logs use UTC.  
**Audited production commit:** `e7797c7d24e4f20befb7e43bfb8e72d5efd436f1`  
**Audited production tree:** `cc40dc15c2db4f389b7a3da758d8fd6e1f5dbd31`  
**Latest implementation parent:** `aa343d27a56a401e28dc9e43d3a85bdbd200aeb7`  
**Independent test branch:** `audit/round3-e7797c7-independent`  
**Canonical regression-test commit:** `eabbf3f5407971cc70a497a8f1856400e5f96819`  
**Source/evidence export commit:** `a4b2b78e2fde0399798e907daef76e9a2b5c09de`  
**Verdict:** **V1 NOT ACCEPTED. Substantial improvements are real, but the production Windows journey is incomplete and independent execution exposes important remaining failures.**

This is a mandatory remediation and completion assignment. Read this document, `ROUND3_EVIDENCE_2026-10-03.json`, both previous audit directives, and `docs/IMPLEMENTATION_DIRECTIVE.md`. The original product scope remains binding. Do not substitute another status-only commit for implementation. Do not rewrite the stack, discard owner data, disable adverse assertions, or make unavailable tests appear successful.

## 1. What was actually audited and executed

### 1.1 Exact source coverage

The full tracked production snapshot was exported from the pinned commit and inspected locally, not inferred from a few search snippets. It contains **118 files, 1,115,967 bytes, 22,943 text lines, and 61 C# files containing 17,592 lines**. Every exported file's Git blob hash was independently recomputed. The complete directory tree was reconstructed from file bytes/modes/names and matched the production tree above. This proves which source was reviewed, not that every possible execution has been tested.

The review covers application entrypoints, WPF wiring, IPC, broker state and ownership, source discovery, HTTP fetching, parsing, canonical identity, catalogue persistence, refresh/retention, probes, native process lifecycle, security policy, recovery, configuration, tests, CI, packaging and documentation. Previously identified unchanged gaps remain open. A 17-commit history inventory was captured; an exhaustive scan of every historical blob was **not** performed.

The audit branch adds only synthetic tests, an actual-WPF smoke harness and audit workflows. Comparison against production shows **seven added files and no modifications to existing production files** at the export commit. Do not merge this branch wholesale simply to acquire the tests. Integrate the relevant regressions deliberately and preserve their acceptance meaning.

### 1.2 Independent execution, not merely Grok's report

Canonical workflow: [run 37076157596](https://github.com/alinescafs3mp-afk/vpn/actions/runs/37076157596), test commit `eabbf3f5407971cc70a497a8f1856400e5f96819`. SDK **10.0.112**, runtime **10.0.12**. Release builds passed on both platforms.

| Executed suite | Passed | Failed | Skipped | Total | Exact boundary |
|---|---:|---:|---:|---:|---|
| Independent regressions, Ubuntu 24.04.5 x64 | 6 | 24 | 0 | 30 | Production code with synthetic inputs, controlled peers and state doubles |
| Independent regressions, Windows Server 2025 Datacenter 10.0.26100 | 5 | 24 | 0 | 29 | Same suite except the explicitly Linux-only output fixture |
| Existing tests, Linux, native core unset | 91 | 0 | 3 | 94 | Three native checks honestly skipped |
| Existing tests, Windows, native core unset | 76 | 15 | 3 | 94 | Includes real platform failures and nonportable test-fixture failures |
| Existing tests, Linux, verified pinned Mihomo available | 94 | 0 | 0 | 94 | Actual controlled non-TUN native positive/negative checks ran |

There are **25 distinct failing independent expectations across the two platforms**, not 48 separate defects and not a claim of 25 exploitable vulnerabilities. A14 fails only on Windows; A16 runs only on Linux. In-process broker tests prove state-machine defects, not actual packet leakage by an installed guard.

The native job downloaded the official `v1.19.32` Linux archive and verified both archive and executable SHA-256 against the committed manifest before execution. Source pin: `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`. The executable hash is `3122d100e8177501776109f1a6253a694611627cf4d7c7ec82705855cf8626a8`. This is real native validation, but it does not prove Windows TUN, all supported protocol handshakes, public-node availability, or WFP protection.

### 1.3 Actual Windows UI execution

[WPF run 37075649029](https://github.com/alinescafs3mp-afk/vpn/actions/runs/37075649029), job `111064839369`, constructed the actual `AutoVpn.Desktop.App` and `MainWindow`, exercised the four navigation handlers and produced four 880-by-640 rendered images. The images were downloaded and visually inspected. A fresh unconsented profile was used: no public subscription download, node probe, installation or TUN operation was triggered.

This closes the narrow claim that the real window can be constructed and those four navigation handlers execute on that Windows runner. It does **not** close keyboard accessibility, themes, DPI behavior, tray lifecycle, settings changes, manual selection or a connected user journey. The runner is Windows Server 2025, **not** a Windows 11 acceptance machine.

### 1.4 Additional checks and limitations

The transitive NuGet advisory query completed for nine projects and returned **zero vulnerable package entries**. This is a result from that NuGet advisory source and time, not an all-dependencies security certificate. Native Mihomo/Go dependencies, Wintun, installer contents, signing and a complete SBOM were not covered by that query. A narrow current-tree token/private-key-signature screen returned no matches; it was not a comprehensive secret scan or a historical-secret scan.

Downloaded CI archives were CRC-checked and SHA-256-verified against GitHub artifact metadata. Their identities and run/job links are recorded in the accompanying JSON. Raw TRX, source inventory, test fixtures and actual WPF renders are retained in the audit evidence bundle. GitHub artifact retention is finite, so preserve the evidence rather than relying indefinitely on an expiring artifact.

**Not executed:** installed Windows TUN/WFP/SCM acceptance, production DNS/IPv6 packet capture, two-account Windows authorization, Windows 11 install/upgrade/uninstall/reboot/sleep, a sustained 10,000-node benchmark, a long-duration soak, every protocol's real handshake, or complete native-dependency/license/signature closure. Do not relabel those gates as passed.

**Auditor correction:** intermediate runs `37075374477` and `37075648890` failed to compile the newly written TLS fixture because its stream read ignored the returned byte count and triggered CA2022. This was an auditor test-fixture defect, corrected at the canonical test commit. It is excluded from product findings. The exclusion-during-start test was also made explicitly automatic so it does not contradict an intentional manual override. The final reported suites compiled successfully.

## 2. Preserve the improvements

The TLS-through-SOCKS implementation now performs real certificate authentication; the previous plaintext-HTTPS finding must not be repeated as if unchanged. The controlled native positive and broken-candidate negative both run. Mixed-case security normalization, rejection of unknown security, pending-node exclusion, H2/UDP mapping work, 304 tree reuse, consent gating, the refresh timer, catalogue copy/commit publication, stale-writer conflict detection, and several broker ownership checks have improved.

The former all-response 256-entry failure is partly fixed: **1,500 reads no longer prevent Disconnect**, and safety operations have a separate path. Completed pipe sessions are pruned and saturation handling improved. Journal recovery now has a durable unknown marker. The Mihomo license record was corrected from MIT to the actual GPL version 3 text without inventing an SPDX suffix.

Keep those protections and tests. Remaining findings below concern incomplete closure, newly connected code, and adverse combinations that the existing green suite did not cover.

## 3. Evidence labels and remediation rules

- **EXECUTED:** an acceptance assertion failed against the pinned production code in the independent runs, or an existing platform test exposed the stated failure. Its test name and platform are supplied.
- **STATIC:** a defect is visible in the source/call graph but the complete consequence was not independently exercised.
- **GAP:** a required implementation or acceptance path is absent or unvalidated.

P0 blocks the promised product or protection journey. P1 is serious correctness, reliability or trust-boundary work. P2 is required completeness, performance or evidence quality. These are engineering priorities, not CVSS scores. Thirty remediation groups are not thirty independent vulnerabilities.

References below are repository-relative paths and method/type names at the audited commit. Test IDs resolve to the exact methods in the two `IndependentRound3Tests*.cs` files in the audit branch. Original `F` and second-round `R2` IDs remain tracked by the crosswalk at the end.

## 4. Mandatory remediation groups

### R3-01 [P0] Complete the actual Windows product composition

**Evidence: GAP.** `src/AutoVpn.Service/Program.cs`, `Broker/ICoreController.cs`, `Broker/UnavailableNetworkGuard.cs`, `Recovery/Program.cs`, packaging scripts.

The shipping entrypoint still constructs `RefusingCoreController` and `UnavailableNetworkGuard`; there is no installed operational SCM/core/guard composition or current installer. A successfully opened WPF window is not a functioning VPN. Refusal is correct for unsupported environments, but refusal on every Windows installation does not implement v1.

**Required:** implement a real supervised core, SCM-compatible service lifetime, narrowly owned Windows protection/recovery, protected asset lookup and the UI-to-broker journey. Keep untrusted subscription HTTP/parsing and the full catalogue unelevated. The broker accepts a bounded typed runtime set and independently verifies its authority, effective semantics and operation context; never accept arbitrary YAML, URLs, executable paths or scripts. Do not make the user-writable database authoritative for privileged effects.

**Closure:** fresh install, fresh profile, consent, source update, measured eligibility, one-click TUN connection, ordinary application traffic, guarded failover, explicit disconnect and network restoration. No seeded test catalogue, fake guard or environmental variable manually locating the core may substitute for the installed journey.

### R3-02 [P1] Implement Windows peer and process ownership, not permanent rejection

**Evidence: EXECUTED A14 on Windows; existing Windows IPC failures; GAP.** `Broker/PipePeer.cs`, `LocalIpcServer.cs`, `NonTunCoreProbeTransport.cs` / `ProbeWorker.ProcessOwnsLoopbackPort`.

The socket ownership checker works on Linux but rejects a real listener owned by the current process on Windows. The pipe peer path rejects unverified non-Linux clients instead of performing Windows token/SID/session authorization. These are safe refusals, not completed Windows implementations.

**Required:** actual Windows process/socket ownership using supported OS APIs, explicit restrictive named-pipe ACLs, real client token/session checks, remote-client rejection, an authorized owner lease and authenticated server discovery. Handle PID reuse, process death, port squatting and service-account boundaries. Never fix A14 by treating every open port as owned or fix pipe tests by accepting every client.

**Closure:** run on the installed service identity and two real Windows accounts/sessions; deny an unrelated owner and spoofed server, accept the authorized UI, bind readiness to the exact owned core, and keep safety commands responsive.

### R3-03 [P1] A TLS handshake is not a valid probe response

**Evidence: EXECUTED T01, T02; STATIC caller defect.** `NonTunCoreProbeTransport.cs`: `Socks5Client.ExchangeAsync`, `GetStatusAsync`, `NonTunCoreProbeTransport.ProbeAsync`.

Authenticated `200` HTML headers with a declared but missing body are accepted for an expected `generate_204` target. Even `NOT-HTTP 204 Fine` is accepted as a status line. Independently, the transport accepts `Authenticated=true` and status 200/204 while ignoring a non-null `TlsProbeExchange.Failure`. A parser rejection can therefore be converted back into success by its caller.

**Required:** use a standards-compliant bounded HTTP client over the already authenticated candidate stream, or implement and test the exact limited protocol correctly. Enforce the target-specific expected status/body policy, valid HTTP syntax/framing, deadlines and byte caps. Success must require the absence of every protocol/authentication/body failure. Do not accept arbitrary 200 responses as equivalent to the approved 204 target. Preserve certificate and hostname verification; fixture trust anchors must remain test-only.

**Closure:** trusted TLS with valid expected response passes; wrong status, HTML, malformed status, partial headers/body, contradictory lengths, redirect, over-limit data and stalled body fail with precise reasons. Test the full `ProbeAsync` publication path as well as `ExchangeAsync`.

### R3-04 [P1] Bind admission evidence to the candidate, target and owned attempt

**Evidence: EXECUTED A04; GAP.** `Probe/ProbeCoordinator.cs`, `ProbeObservation`, native transport.

A successful observation carrying the wrong candidate digest and wrong target URI can still publish the current node as Healthy. Merely adding `CandidateDigest`, `TargetUri` and `WorkerId` properties is not validation. The workflow also uses only the first configured probe target rather than the complete independent-target admission contract.

**Required:** an immutable attempt context containing node ID, effective configuration digest, network epoch, applicable policy revision, target definition and owned worker identity. The caller must validate returned context and completion before publishing. Worker IDs must come from the owned process/session, not arbitrary self-asserted text. Native validation, authenticated target checks and capability assessment are distinct stages.

**Closure:** positive evidence from candidate A, another target, another worker, another policy/network epoch or an older attempt cannot promote B. Execute the required independent-target checks. Repeat with one good production TUN A and broken candidate B to prove B cannot borrow A's working path.

### R3-05 [P1] One unsupported node must not abort the whole validation queue

**Evidence: EXECUTED A05.** `ProbeCoordinator.RunAsync` returns the whole report for both `CoreFailure` and `Unsupported`.

An unsupported candidate is a record-local outcome. Treating it as an unavailable common engine leaves later supported working nodes untested. Existing endpoint fairness improvements do not prevent this new starvation path.

**Required:** distinguish candidate-local unsupported/configuration rejection from a systemic missing/tampered/unstartable engine, target outage and uplink loss. Quarantine one unsupported node with its reason, continue fair bounded work, and pause a cycle only for a genuine shared-environment failure. Resume pending work after cancellation/budget exhaustion without permanently prioritizing the same failed records.

**Closure:** unsupported first candidate followed by a working candidate yields both attempts and one success. A real missing core does not mark every node failed. Mixed endpoint variants eventually all receive an opportunity.

### R3-06 [P1] A failed on-demand admission must invalidate obsolete success

**Evidence: EXECUTED A06; STATIC clock/freshness concerns.** `ProbeCoordinator.AdmitIfStaleAsync`, `NeedsOnDemandAdmission`, `NeedsProbe`, `Eligibility`.

A node last verified 65 seconds ago can fail its new pre-connect check yet retain the previous Healthy assessment and remain in the 30-minute working pool. The on-demand path now exists, but its negative result does not consistently update authoritative health. Raw time subtraction also needs the same conservative rollback policy as eligibility.

**Required:** publish successful and failed admission outcomes atomically under captured context. Distinguish a real candidate failure from cancellation, an unavailable target and an offline uplink. The failed current proof must not leave the old proof eligible; cancellation must not invent failure. Use completion timestamps and monotonic durations, not a cycle-start timestamp presented as the time of every measurement.

**Closure:** 65-second old Healthy plus current failure is no longer usable; a cancelled check remains untested/stale rather than failed; new network and clock rollback force appropriate revalidation. Do not solve this by widening the 60-second admission requirement.

### R3-07 [P1] Cancellation must win at publication and across the entire local operation

**Evidence: EXECUTED A15; STATIC UI integration.** `ProbeCoordinator.RunAsync`, `AdmitIfStaleAsync`, `Desktop/MainWindow.xaml.cs`.

A transport can return success immediately after its caller is cancelled, and the coordinator still publishes Healthy. Cancellation checks before I/O alone are insufficient. The on-demand UI path passes `CancellationToken.None`, while a second click can send Disconnect without cancelling that local admission task.

**Required:** operation-scoped cancellation and a generation fence covering discovery, download, admission, publication and any subsequent Connect. Recheck cancellation and authority immediately at the serialized publication point. Distinguish bounded critical rollback from ordinary cancelled work. Prevent a delayed local admission from issuing a new Connect after the owner's Disconnect/Exit.

**Closure:** cancel immediately before a successful return; no success is published. Hold an on-demand check, press Disconnect, release the check; no subsequent Connect is emitted. Test actual UI coordination and pipe behavior, not only a standalone reducer.

### R3-08 [P1] Daily traffic accounting can overflow or refund consumed budget

**Evidence: EXECUTED A08, A09.** `Probe/ProbeByteBudget.cs`, `ProbeCoordinator`, refresh-owned budget lifetime.

A near-maximum stored byte count can wrap when charged. A delayed charge tagged with the previous day can reset/refund the current day's allowance. Persisting a counter does not by itself make daily limits enforceable under concurrent attempts, crashes or clock changes.

**Required:** saturating or checked arithmetic, validated persisted counters, monotonic day advancement, explicit handling of late charges and atomic reservation/charge publication. Do not reopen budget on clock rollback or silently repair malformed data to a generous fresh allowance. Define exactly which payload/wire traffic is counted and charge error paths consistently. Apply the same contract to on-demand and manual tests.

**Closure:** overflow, negative/corrupt counts, midnight overlap, late previous-day completion, restart and concurrent workers cannot exceed or refund the approved allowance. Budget exhaustion leaves untested work pending and preserves active-session safety.

### R3-09 [P1] Throughput results must belong to the current measured path

**Evidence: EXECUTED A07; GAP.** `CatalogueCoordinator.cs` / `SpeedMeasurement.MeasureHealthyDownloadAsync`, `BoundedTransfer`, catalogue measurement schema.

The helper accepts a binding for network epoch 1 while the current catalogue is on epoch 2. A superficially bound stream is not sufficient if current eligibility and the binding are not revalidated at start and completion. An arbitrary supplied stream also does not prove candidate-specific download speed.

**Required:** bind a real candidate-owned transfer to node/digest/epoch/policy/target/attempt, validate actual current eligibility and cancellation, and store bounded timestamped samples. Distinguish benchmark throughput, current session traffic rate and latency. Do not call total core startup plus handshake time pure network ping, or a single sample a measured median of a history that does not exist.

**Closure:** stale epoch/digest, excluded/stale node, cancelled transfer and unrelated stream produce no accepted speed sample. Real bounded transfer tests verify payload bytes, elapsed time and the exact selected path without testing all public nodes at full bandwidth automatically.

### R3-10 [P1] Output retention caps must not stop draining child-process pipes

**Evidence: EXECUTED A16 on Linux; GAP on real Windows processes.** `NonTunCoreProbeTransport.cs` / `ProbeWorker.CountAsync`, disposal/readiness paths.

A child writing 3 MiB cannot reach its next instruction because the reader stops consuming after its 1 MiB cap. The operating-system pipe fills and blocks the child. A cap on retained diagnostics is not a cap on how much a running process may safely write.

**Required:** continuously drain stdout and stderr until owned process exit, discard excess bytes after a bounded ring/tail buffer, and keep collection independent from foreground readiness. Use owned process handles, bounded stop/kill/wait, cancellation-safe cleanup and explicit cleanup-failure reporting. Verify child exit before claiming credential files were safely removed. Retain no unbounded output and do not kill unrelated processes by name.

**Closure:** large concurrent stdout/stderr output does not deadlock; cancellation, early exit, readiness timeout, child descendants and kill failure have bounded cleanup. Port ownership must still be proven on Windows. A newer worker survives cleanup from an old worker.

### R3-11 [P1] Diagnostics still expose common secret representations

**Evidence: EXECUTED A11; STATIC.** `Domain/TextPolicy.cs` / `SecretRedactor`, native worker diagnostics, `MihomoProcessController`.

A quoted JSON password/controller-secret canary survives the generic redactor. Raw process output may be retained in diagnostic fields. Control-character rejection in configuration is useful but does not establish safe logging of valid secrets.

**Required:** structural redaction plus the exact known secret set before any diagnostic leaves the process boundary. Cover JSON/YAML quoting, whitespace, URI userinfo, Base64 share links, controller secrets, auth headers and nested error records. Protect runtime files and DLL/executable search locations; hash verification followed by execution from an attacker-writable path is not sufficient provenance.

**Closure:** synthetic secret canaries in all supported encodings are absent from normal/error logs, UI errors, crash/recovery exports and temporary-file leftovers. Retain useful reason codes without retaining credentials. Never commit live subscription bodies, runtime profiles, databases or private packet captures.

### R3-12 [P0] Resolve the current source commit correctly; tree SHA is not a commit ref

**Evidence: EXECUTED A13; STATIC.** `Fetch/GithubTreeParser.cs`, `ReviewedRegistry.cs`, `CatalogueCoordinator.BuildDiscovery`, `config/source-manifest.json`.

The source registry still contains the initial pinned commit. New code then treats the Git Trees response's tree identity as a raw-file commit ref. A tree object and a commit object are different Git objects. A synthetic valid response with a distinct tree SHA causes the work item URLs to contain the wrong ref. The local fixture that treated these identities as interchangeable did not validate real GitHub behavior.

**Required:** resolve the approved default-branch head to a commit, capture that immutable commit for one refresh, read its tree, and retain separate commit/tree/blob/content identities. Build raw/mirror URLs from the correct commit identity. Validate that requested and returned objects belong to the intended snapshot. Allow reviewed mirror fallback without silently mixing generations. The initial pin is bootstrap/evidence, not a permanent freeze.

**Closure:** a controlled Git object graph with different commit/tree hashes passes; subsequent branch-head changes produce new work; real read-only GitHub discovery resolves a current commit and fetches a bounded representative object without storing live credentials in Git. Incomplete discovery never authorizes pruning.

### R3-13 [P1] Aggregate limits currently starve every source beyond the first eight

**Evidence: EXECUTED A12.** `CatalogueCoordinator.RefreshAsync`, product download/candidate limits.

Nine tiny feeds offered over two cycles still never fetch the ninth, because every item reserves the full 8 MiB per-file maximum against a 64 MiB cycle cap. The same first eight win again. This is bounded resource use, but it violates all-source coverage and fair progress.

**Required:** account for actual bounded consumption, maintain a resumable fair cursor/queue, and reserve/release capacity according to a tested policy. Bound total retries, mirrors, decoded bytes, discovered work and expanded records, not only an individual response. Select a preferred adequate representation before redundantly downloading every duplicate export. Report unfinished coverage honestly.

**Closure:** all nine tiny feeds are eventually processed under the same byte allowance; a large feed cannot starve others; a malicious expansion or many mirrors cannot bypass the aggregate cap. Source discovery, validation and retained-catalogue caps must operate together without deleting favorites or the active node to make a count green.

### R3-14 [P1] Finish source transactions, 304 freshness, scheduling and refresh ownership

**Evidence: STATIC + GAP; existing improvements preserved.** `SourceLedger`, `CatalogueCoordinator`, `RefreshMerge`, `RefreshScheduler`, desktop timer.

The recurring timer, consent gate, refresh fence and 304 discovery cache now exist. Their presence does not prove a complete crash-safe source transaction. Source ledger and SQLite publication remain separate; successful empty representations cannot be identified solely by node membership; stale cached discovery needs explicit coverage/freshness. Unchanged successful checks and failed checks need different scheduling timestamps, or repeated timer pulses can immediately retry forever.

**Required:** durable per-artifact snapshot validity, successful check time, last content change, rejected version and next retry. Couple accepted ETags to the actually committed representation. Serialize publication/fence ownership across all await boundaries and preserve source provenance. Distinguish complete, partial, unchanged, removed, failed and discovery-incomplete states. Validate unknown families and never claim all subscriptions were handled when only seed matches were processed.

**Closure:** 200, valid empty, invalid body, 304 with/without cache, cancelled old cycle, crash between catalogue/ledger writes, removed representation and mirror disagreement all preserve the intended last-good state. Repeated unchanged checks respect the interval; failure backoff does not block manual retry or cause a retry storm.

### R3-15 [P1] Normal mutations still have a lifetime 256-command limit

**Evidence: EXECUTED A03; passing control A02.** `Application/IpcDispatcher.cs`, `ProductLimits.IpcIdempotencyEntries`.

Reads and Disconnect are no longer disabled by the old common cache. However, the non-safety mutation journal still stops accepting new mutations at entry 256 with `REPLAY_WINDOW`. A long-running client cannot depend on a manual service restart to resume ordinary health/connection/runtime-set operations.

**Required:** a bounded renewable owner/session lease with safe request identity and operation-result retention semantics. Reconcile in-flight operations before lease rollover; expired requests must not resurrect effects. Preserve request-payload conflict detection and uncertain-effect results. Do not merely grow the dictionary without a bound, or evict old mutations while still accepting their old authority tokens.

**Closure:** thousands of ordinary mutations and reads, reconnects and bounded lease renewals retain correct memory use, responsive safety controls and exactly-once/explicitly-uncertain effect semantics. Include process restart and delayed old requests.

### R3-16 [P1] Keep safety control responsive under long authenticated operations

**Evidence: STATIC + GAP; Linux saturation improvements acknowledged.** `LocalIpcServer`, `IpcDispatcher.Dispatch`, service lifetime.

Frame deadlines and bounded pipe instances are useful. The handler still waits synchronously for slow engine operations. Filling the available authenticated sessions with long requests can consume transport capacity that safety commands need. A bounded count alone is not a priority/liveness guarantee, and a cached authority reset does not itself cancel old engine effects.

**Required:** asynchronous operation handles and guarded completion events, a serialized authority/state decision path, bounded admission and guaranteed capacity for cancel/disconnect/recovery. Avoid unbounded parallel mutation as a workaround. Tie old work to the original boot/lease/operation identity and reconcile it before a new owner gains authority. Ensure service death/fault reaches a truthful UI state.

**Closure:** several legitimate held Start operations plus malformed/slow clients cannot starve authorized Disconnect. Run through real named pipes on Windows, not just direct engine calls. Sustained saturation returns to bounded idle resources without listener death or abandoned requests.

### R3-17 [P1] Revalidate policy and eligibility at both Start commit and production confirmation

**Evidence: EXECUTED B01, B02, B03.** `BrokerEngine.ConnectAsync`, `ConfirmProduction`, catalogue/policy stamps.

Revoking insecure-certificate permission after Start but before confirmation still permits Connected. An automatically selected node excluded during Start can still be committed. A network epoch change during Start is not rejected at that commit boundary. The later confirmation does check epoch, so B03 alone is not proof of a connected new-network session; it is proof that stale Start work was accepted and left resources/state needing reconciliation.

**Required:** capture and revalidate effective configuration, current selection purpose, node exclusion, source/country constraints, network epoch and relevant security-policy revision before every effect commitment and final Connected transition. Settings stamps must cover actual policy, not merely cosmetic settings, and must not replace node-level eligibility checks. Production proof must identify the exact core instance and configuration digest.

**Closure:** each policy dimension is changed separately while Start/confirmation is held; prohibited work is stopped/reconciled behind protection and never reaches Connected. Preserve intentional explicit manual overrides only where the original contract allows them. B02 deliberately exercises automatic selection.

### R3-18 [P1] CoreExit must change truth immediately, not after replacement readiness

**Evidence: EXECUTED B04, B05.** `BrokerEngine.ReportHealthAsync`, failover state reducer, policy-changed switch path.

During a reported CoreExit, the broker can remain Connected with CoreRunning true while a replacement Start is blocked. If policy changes during that switch, the replacement is stopped but the old Connected state can remain. The old process was reported dead; retaining the previous node ID does not justify retaining a successful-session claim. An existing test explicitly preserves that incorrect expectation.

**Required:** atomically clear live-process/production-proof truth on confirmed exit, enter protected reconnecting/blocked state before awaiting replacement, and commit replacement only with current authority and proof. Separate an optimization failure while an old path is actually alive from an outage failover. Scope busy flags and cleanup to the exact operation, including exceptions and rejected late completions.

**Closure:** held replacement readiness, changed policy, switch-budget exhaustion, pinned mode, offline uplink and old confirmation cannot keep or restore an unjustified Connected state. Retry remains guarded and bounded; no implicit DIRECT interval is allowed.

### R3-19 [P1] Pre-cancelled commands and explicit unprotected mode need coherent semantics

**Evidence: EXECUTED B06, B07.** `BrokerEngine.ConnectAsync`, guard contract, tunnel reducer.

An already-cancelled Connect still calls Arm. Conversely, an explicitly chosen unprotected session is refused because the code still requires `Armed=true` from a guard that correctly reports no protection. Forwarding a Boolean to the guard did not complete the state-machine change.

**Required:** reject cancelled work before privileged effects and check again at effect commitment. Model protected mode, explicit owner-selected unprotected mode, protection failure and unknown protection separately. Only an explicit authorized opt-out may select the unprotected path, with persistent warning. A failed protected Arm must never silently take that path.

**Closure:** pre-cancelled Connect performs zero effects; explicit supported unprotected mode does not lie about protection; protected-mode Arm failure remains refused/protected as appropriate. Test cancellation and cleanup between every stage.

### R3-20 [P1] The UI must cancel its own work, not only send a broker command

**Evidence: STATIC; associated A15/B06 failures.** `MainWindow.ConnectClick`, `ExitApplication`, `SessionMailbox`, `UiSessionReducer.PlanExit`.

Local admission can outlive a Disconnect because it uses `CancellationToken.None`. The original click handler may subsequently send a fresh Connect using the mailbox's latest revision. Shared `OperationPending` flags can be cleared by an older handler. Exit waits only briefly for refresh and does not establish a complete closing latch/timer/worker join before disposing the catalogue.

**Required:** one owned UI operation context from button press through admission, broker request, response and cleanup. Cancel and join it on Disconnect/Exit; invalidate its completion token before sending safety requests. Stop scheduler admission before shutdown, await all owned jobs or report a bounded unresolved cleanup state, and never dispose storage still used by a live job. Maintain authoritative broker resync/event updates and reject out-of-order replies.

**Closure:** actual UI tests hold admission/refresh, press Disconnect/Exit twice, deliver old replies, lose/restart the broker and resume. No hidden re-connect, stale pending flag, false verified disconnect or use-after-dispose occurs. Normal window-close may still hide to tray as specified.

### R3-21 [P1] Journal quarantine fails on Windows and recovery still lacks OS reconciliation

**Evidence: EXECUTED existing Windows RT22; STATIC + GAP.** `Persistence/EffectJournal.cs`: `Inspect`, `MoveAside`, `Recover`; recovery executable.

The bad-header path moves the journal while its `File.OpenRead` handle is still open. Windows execution produces an IOException in the production quarantine path. Sidecars are moved before the main file, and the unknown marker is written after the moves, leaving partial-failure/crash cases. Recovery also compares removal counts without requiring distinct IDs; missing journal without a marker is not proof that no owned OS objects exist.

**Required:** close handles before any rename; establish durable unknown/reconciliation intent before destructive metadata transitions; quarantine database/sidecars consistently; keep recovery independent from a healthy catalogue. Validate unique removed identities against actual AutoVPN ownership and observed OS state. Missing/corrupt metadata requires reconciliation, never blanket firewall/route resets or an invented clean result.

**Closure:** corrupt/missing/unsupported journal, locked files, WAL/SHM sidecars, crash at each move/marker step, duplicate removal IDs and repeated process restarts cannot falsely claim clean recovery. The marker persists until genuine owned OS reconciliation succeeds. Windows test must pass without suppressing the IOException.

### R3-22 [P1] Finish catalogue ownership and Windows file-lifetime handling

**Evidence: existing Windows SQLite failures; STATIC + GAP.** `SqliteCatalogue`, service/desktop entrypoints, DPAPI boundaries.

Copy/save/publish and the cross-handle revision conflict are improvements. The application still lacks a completed single-authority catalogue-to-broker transfer. A conflict detected against a permanently stale cache is not a usable recovery protocol. Several Windows tests fail deleting database/backup files with retained handles; those failures require separating test lifetime/pooling mistakes from production ownership problems, not reporting eight distinct database vulnerabilities.

**Required:** one authoritative full catalogue owner, typed bounded broker runtime handoff, deterministic conflict reload/reconciliation and correct DPAPI scope for installed identities. Make connection/reader/backup/pool lifetimes explicit. Close or isolate connections used by temporary tests; do not globally disrupt a live catalogue to make cleanup pass. Surface stale-write errors without crashing async UI handlers or silently losing owner choices.

**Closure:** two independent handles/processes, concurrent refresh/settings, backup, shutdown, crash/reopen and service/UI identity differences preserve data and produce recoverable conflicts. The Windows test suite leaves no owned file handles after fixture shutdown. A clean UI catalogue reaches the broker without shared writable-database trust.

### R3-23 [P2] Targeted SQL updates did not remove quadratic CPU work or finish retention

**Evidence: STATIC + performance GAP.** `SqliteCatalogue.TryUpdateInPlace`, `SameIdentity`, `MemoryCatalogue.Copy`, retention policies.

An assessment no longer needs to re-protect every secret. However, `TryUpdateInPlace` still iterates all nodes and uses a linear `First` lookup for each, while identity comparison serializes all semantics and each mutation copies the catalogue. Two-node tests do not prove acceptable 10,000-node behavior. Retention helpers are not a complete enforced expiry/admission workflow.

**Required:** indexed immutable/current snapshots, targeted dirty-row tracking, batching and bounded histories with measured complexity. Define retention of missing but working nodes, expired failed credentials, active nodes and favorites under overflow. Prevent pending/unsupported/current-node accumulation from bypassing the intended cap. Do not delete protected owner data simply to meet a benchmark.

**Closure:** measure 10,000-node import, one assessment, a probe batch, refresh, filtering, favorite updates, restart and eviction. Record elapsed time, allocations, peak memory, SQL writes, protector calls and UI responsiveness. Test a long-running bounded cycle, not only a microbenchmark of one helper.

### R3-24 [P1] Complete parser containment and semantic round trips

**Evidence: EXECUTED A18; STATIC + GAP.** `TextPolicy.CountryLabels`, import parsers, canonicalizer, native emitter.

A malformed surrogate label escapes as `ArgumentException`. Large Base64 and multi-record Xray/Clash support improved, but nested options, duplicate/alias conflicts and missing fields still need exhaustive per-format handling. In the multi-record Xray expansion, inheriting the first record's credential into a later incomplete record can manufacture semantics rather than mark it invalid. Native success for selected fixtures is not proof of every emitted transport/protocol combination.

**Required:** bounded strict decoding and Unicode-safe label processing; per-record recoverable failures; complete document validity distinct from count balance. Preserve every admitted connection/security field with protocol-specific defaults, reject unsupported combinations and conflicting fields before probing, and never inherit unrelated credentials to rescue malformed entries. Keep the v3 UDP identity/emission agreement and invalidate old health when effective semantics change.

**Closure:** all supported URI/Base64/Clash YAML/Clash JSON/Xray representations round-trip to effective native configuration. Add malformed Unicode, embedded wrappers, duplicate/conflicting nested keys, wrong JSON types, invalid port/cipher/security, missing credentials and expansion limits. Validate every supported protocol with the pinned native parser and controlled positive/negative handshakes. Migrations preserve owner metadata without retagging unproven health.

### R3-25 [P1] Destination, DNS and IPv6 safety still need a real dial boundary

**Evidence: EXECUTED A10, A17; STATIC + GAP.** `EndpointSafety`, `GithubTreeParser`, production DNS/TUN profile.

The non-public address list misses `198.19.0.0/16`, the second half of the benchmark `/15`. A blob with symlink mode `120000` is still admitted as subscription data. Public-looking hostnames are not protected by checking only their text; all resolved A/AAAA destinations and subsequent re-resolution need validation. Port-53 rules do not prove the core's internal user-domain DNS stays in the selected tunnel.

**Required:** explicit reviewed address/object-mode policies, validated DNS resolution bound to the actual dial and protection against rebinding/mixed answers. Distinguish endpoint bootstrap DNS, user DNS and probe targets. Configure and verify the exact pinned core's internal DNS routing without recursion. Keep test-only loopback allowances isolated from shipping policy. Protect or deliberately block every advertised IP family through all failure states.

**Closure:** object modes, private/metadata/link-local/mapped addresses, special-use ranges, mixed DNS answers and rebinding have negative tests at the real transport. Windows packet evidence covers user DNS, bootstrap exceptions, IPv4/IPv6, core death, switching and explicit release. Do not claim a measured leak before that execution; the current result is an unclosed safety contract.

### R3-26 [P2] Country, source, ranking and standby policy must agree everywhere

**Evidence: STATIC + GAP.** country extraction, settings, catalogue presentation, ranking and runtime-set/failover code.

Source-claimed localized country names and canonical country codes are not a single stable constraint representation. Conflicting labels/provenance remain incompletely retained. Ranking uses limited actual history; a bounded diverse standby helper does not automatically constrain the broker's accepted set. A measured-status label must account for freshness, epoch and current policy, not only a stored Healthy enum.

**Required:** canonical country IDs with localized display text and explicit unknown/conflict states; separate advertised geography from any optional measured exit information. Apply enabled source, exclusion, manual/pinned/preferred/strict-country policy and capabilities at staging, selection and commitment. Bound/deduplicate standbys, preserve diversity and revalidate before use. Rank real measurements under the selected mode and respect dwell/hysteresis.

**Closure:** conflicting labels, DE versus localized names, source disable after staging, owner exclusion, duplicate/oversized standby lists, stale standby and unknown throughput cannot violate the chosen constraints or fabricate quality. A minor latency improvement does not disrupt a healthy session.

### R3-27 [P2] Finish the product UI rather than the empty-shell smoke test

**Evidence: actual WPF smoke + STATIC gaps.** `Desktop/App.xaml`, `MainWindow.xaml`, code-behind and presentation helpers.

The real light-themed shell starts and navigates. The tested views remain mostly text and empty-state placeholders. Manual node selection, actionable favorites, source controls, country/protocol filters, current traffic, bounded speed actions and full status detail are not delivered merely because helper methods exist. No complete dark/system theme, DPI, keyboard or tray acceptance was executed.

**Required:** the original simple four-view experience with real commands/data, honest timestamps and unknown values, actionable failure reasons, visible protection state and no inert controls. Keep technical complexity out of the default flow. Provide virtualized measurable lists, keyboard focus/navigation, accessible names, readable contrast and appropriate loading/cancel states. Keep Russian UI natural and protocol/identifier text untranslated.

**Closure:** actual Windows screenshots and command results for populated/empty/error/refreshing/connecting/connected/protected-outage/recovery states. Exercise themes, representative scaling, keyboard-only operation, screen-reader labels, tray show/hide/exit, multiple launch and UI restart while a real session exists.

### R3-28 [P1] Calibrate Windows tests and make platform evidence a permanent gate

**Evidence: EXECUTED 15 existing Windows failures.** Existing test files and CI configuration.

The Windows failures comprise a genuine journal rename failure, deliberately unimplemented Windows IPC, file-lifetime/pooling cleanup failures, and a Linux-only `/usr/bin/pkill` fixture. They are not all production exploits. Ignoring them, changing every Windows test to skip, or weakening authorization is not remediation.

**Required:** implement missing Windows production adapters, fix platform-specific fixtures and dispose all owned resources deterministically. Add permanent Linux plus Windows build/unit/integration CI and explicit native prerequisites. Retain failed test outcomes even where workflow steps use `continue-on-error` for artifact collection; inspect raw TRX/outcomes, not a green step icon. Use an isolated Windows 11 admin test machine for disruptive gates that a hosted Windows Server runner does not establish.

**Closure:** portable tests pass on both platforms, OS-specific tests run in their intended environment with equivalent negative controls, and unavailable native checks are explicit. Pin each result to the actual source/core/test environment. A passing build must never close a runtime gate.

### R3-29 [P0 delivery; P2 supply chain] Produce a retrievable current installer with provenance

**Evidence: GAP; native hash and NuGet-query positives acknowledged.** packaging/release scripts, core manifest, notices and handoff.

No current installable Windows release is substantiated. The old local archive predates the fixes. A corrected license label and a hash-verified Linux core do not complete distribution, Windows driver assets, service registration, upgrade/uninstall, transitive SBOM or signing.

**Required:** reproducible clean-commit Windows package with protected install paths, verified official core/driver assets, service identity/ACLs and recovery/rollback integration. Generate an actual transitive SBOM and component notice/source records for shipped contents, including the real Wintun distribution terms. Review native dependencies separately from NuGet. Sign only with legitimately available credentials; clearly label an unsigned test artifact without inventing a signature or disabling platform protections.

**Closure:** another machine retrieves the exact installer and checks its size/hash/build inputs; clean install, unelevated launch, upgrade, failed upgrade rollback, recovery and uninstall preserve unrelated system state and the selected data policy. Archive/commit/manifest identities must be generated from that build, not retrospectively assigned to an older binary.

### R3-30 [P1 acceptance] Finish the full declared journey matrix, not only these counterexamples

**Evidence: GAP and incomplete cross-layer closure.** Entire implementation and evidence/status reports.

The owner explicitly requests maximum completion. Passing the new 30 tests is necessary regression work, not permission to ignore the original 20 acceptance journeys, remaining platform boundaries or previously reported defects. A smaller correct implementation is preferable to more unused abstractions, but deleting required v1 behavior is not a scope decision delegated by this audit.

**Required:** maintain one traceable requirements-to-production-path-to-test-to-evidence matrix. Treat a helper fix, a wired feature, native validation and installed Windows acceptance as separate states. Resolve every original F and R2 item or provide a narrowly evidenced external blocker. Complete all work independent of that blocker, including tests, packaging code and non-destructive Windows CI. Do not stop merely because a Windows 11 admin gate is not available on the development host.

**Closure:** every mandatory v1 journey has real positive and adverse evidence at the appropriate layer, every critical open finding is closed, and the owner can install/connect/use/update/fail over/disconnect/recover without editing environment variables or hand-seeding data. Otherwise report a recoverable checkpoint and the exact outstanding gate, not v1 success.

## 5. Exact independent regression ledger

Test sources are `tests/AutoVpn.UnitTests/IndependentRound3Tests.cs` and `IndependentRound3Tests_BrokerTls.cs` at the canonical audit commit. FAIL means an executed unmet expectation, not an unexecuted suggestion.

| ID | Acceptance expectation | Linux | Windows | Fix group |
|---|---|---|---|---|
| A01 | Normalized TLS is emitted | PASS | PASS | Preserve R3-24 |
| A02 | 1,500 reads do not exhaust Disconnect | PASS | PASS | Preserve R3-15 |
| A03 | Normal mutation traffic has no lifetime 256-command dead end | FAIL | FAIL | R3-15 |
| A04 | Wrong candidate/target proof cannot publish Healthy | FAIL | FAIL | R3-04 |
| A05 | Unsupported first node does not starve later supported nodes | FAIL | FAIL | R3-05 |
| A06 | Failed current admission invalidates old usable success | FAIL | FAIL | R3-06 |
| A07 | Previous-network speed evidence is rejected | FAIL | FAIL | R3-09 |
| A08 | Byte accounting saturates rather than wraps | FAIL | FAIL | R3-08 |
| A09 | A late old-day charge cannot refund today | FAIL | FAIL | R3-08 |
| A10 | Both halves of benchmark special-use range are non-public | FAIL | FAIL | R3-25 |
| A11 | Quoted secret fields are redacted | FAIL | FAIL | R3-11 |
| A12 | Ninth tiny source eventually receives work | FAIL | FAIL | R3-13 |
| A13 | Tree identity is not substituted for raw commit ref | FAIL | FAIL | R3-12 |
| A14 | Actual current-process listener ownership works | PASS | FAIL | R3-02 |
| A15 | Cancellation before publication prevents Healthy | FAIL | FAIL | R3-07 |
| A16 | Child output is drained beyond retention cap | FAIL | EXCLUDED: Linux fixture | R3-10 |
| A17 | A symlink blob is not subscription data | FAIL | FAIL | R3-25 |
| A18 | Malformed label Unicode is contained | FAIL | FAIL | R3-24 |
| A19 | Unknown security is rejected | PASS | PASS | Preserve R3-24 |
| A20 | Pending nodes are not eligible | PASS | PASS | Preserve R3-04 |
| B01 | Revoked certificate policy prevents final confirmation | FAIL | FAIL | R3-17 |
| B02 | Automatic selection excluded during Start is not committed | FAIL | FAIL | R3-17 |
| B03 | Network changed during Start rejects stale commit | FAIL | FAIL | R3-17 |
| B04 | CoreExit clears Connected before replacement readiness | FAIL | FAIL | R3-18 |
| B05 | Policy-changed failed switch cannot preserve dead Connected | FAIL | FAIL | R3-18 |
| B06 | Pre-cancelled Connect never arms protection | FAIL | FAIL | R3-19 |
| B07 | Explicit unprotected mode does not require an armed guard | FAIL | FAIL | R3-19 |
| T01 | Authenticated 200 HTML headers do not prove expected 204 | FAIL | FAIL | R3-03 |
| T02 | Non-HTTP status syntax cannot become success | FAIL | FAIL | R3-03 |
| T03 | Valid authenticated expected 204 passes | PASS | PASS | Preserve R3-03 |

Do not reverse these assertions to manufacture green results. Where an assertion needs refinement, document the actual contract, preserve the original adverse scenario and add a stronger equivalent test. B02 intentionally uses automatic selection. A16 must be replaced by an equivalent Windows child fixture before it can count as Windows lifecycle coverage.

### 5.1 Existing Windows failures to triage individually

Production journal failure: `Round2SliceBTests.Rt22CorruptJournalStaysRecoveryUnknownAfterRestart`.

Windows pipe/authorization gap: `Round2LifecycleTests.Rt10RealPipeResyncsAndIgnoresAnOlderReply`; `PackageCTests.PartialOversizedAndHeldPipeClientsDoNotBlockTheNextCommand`; `BehaviorTests.LocalPipeRejectsASecondOwnerAndReturnsSnapshot`; `Round2SliceCTests.Rt18SaturatedPipesRecoverAndSessionsStayBounded`; `PackageCTests.SecondPipeDisconnectsAStartBlockedOnTheCore`.

Database/backup cleanup or retained-handle failures requiring lifetime diagnosis: `PackageBTests.FreshImportStaysPendingUntilARealCoreProbeAndConsentSurvivesRestart`; `AuditRegressionTests.At22OpaqueBytesSurviveAndMigrationDoesNotInventHealth`; `AuditRegressionTests.At24FailedSqliteCommitDoesNotPublishTheCopy`; `Round2LifecycleTests.Rt29OneAssessmentDoesNotReprotectTheOtherSecrets`; `BehaviorTests.CorruptCatalogueIsQuarantinedRatherThanParsedAsEmptySuccess`; `BehaviorTests.SqliteRoundTripProtectsSecretsAndRefusesNewerSchema`; `BehaviorTests.UnsupportedJournalSchemaIsLeftUntouched`; `Round2SliceATests.Rt09StaleCatalogueCannotReplaceACommittedSnapshot`.

Nonportable test fixture: `Round2SliceATests.Rt03WorkerCancelCleansCredentialsAndDoesNotKillTheNextProcess` attempts `/usr/bin/pkill` on Windows. Replace the fixture with owned platform-appropriate child processes; do not broaden production kill privileges.

## 6. Original-findings crosswalk: none may disappear

The table reports remaining closure, not a fresh claim that unchanged code was runtime-tested. Improved subcases retain their successful evidence. Each row must appear in the new closure report together with the R3 rows.

| Original | Relevant R2 | Round-3 closure required | Current disposition |
|---|---|---|---|
| F01 | 06, 28 | R3-01, 02, 29 | Production Windows composition absent |
| F02 | 01, 07-14 | R3-03-07, 12-14 | Real native subset works; complete refresh/admission/handoff open |
| F03 | 07, 08, 12, 24, 26 | R3-19, 20, 27 | Startup/navigation demonstrated; full command lifecycle open |
| F04 | 15-18 | R3-15, 16, 28 | Read/saturation subcases improved; Windows authority/liveness open |
| F05 | 06, 16 | R3-02, 16 | Safe Windows rejection, not an implemented owner boundary |
| F06 | 15, 17 | R3-15-20 | Replay/ownership improved; mutation renewal and races remain |
| F07 | 02, 17 | R3-02, 10, 17, 18 | Native worker exists; all ownership/completion boundaries not closed |
| F08 | 18 | R3-18 | CoreExit truth still fails adverse cases |
| F09 | 17, 19 | R3-19, 21 | Cleanup improved; mode/cancellation/platform behavior open |
| F10 | 06, 19, 23 | R3-01, 21, 25 | Real protection/packet evidence absent |
| F11 | 19 | R3-21, 22 | Marker persists; Windows quarantine fails |
| F12 | 23 | R3-25 | Production DNS/IPv6 path proof absent |
| F13 | 23 | R3-25 | Resolved-destination safety absent; address/mode negatives fail |
| F14 | 04, 05 | R3-06, 07, 17 | On-demand path added; negative completion/epoch cases fail |
| F15 | 01-05 | R3-03-10 | Native positive verified; admission/isolation/fairness remain |
| F16 | 10, 11 | R3-14 | Invalid snapshot preservation improved; full durable source transaction open |
| F17 | 20, 22 | R3-24 | Wrapper expansion improved; malformed label containment fails |
| F18 | 20 | R3-24 | Schema fixes exist; complete native handshake matrix absent |
| F19 | 20 | R3-24 | Specific unknown/mixed-case security controls pass; retain and broaden |
| F20 | 20, 22 | R3-24 | More representations work; every admitted nested semantic not proven |
| F21 | 21 | R3-24 | v3 UDP/opaque changes improved; complete historical migration open |
| F22 | 13, 14 | R3-13, 14, 25 | Per-attempt HTTP controls improved; total coverage/dial boundary open |
| F23 | 07, 25 | R3-22, 23 | Copy/CAS improved; full authority/conflict recovery/platform lifetime open |
| F24 | 14, 25 | R3-13, 23 | Targeted secret writes improved; scale/retention unproven |
| F25 | 09-11 | R3-12-14, 25 | Wrong tree/commit identity and unfair feed budget fail |
| F26 | 24, 26 | R3-26, 27 | Canonical/conflicted geography and usable controls incomplete |
| F27 | 04, 24-26 | R3-09, 26 | Measurement binding/ranking/standby closure incomplete |
| F28 | 05, 12, 24 | R3-06, 08, 14, 20 | Timer/clamp improved; time/epoch/late-work consistency open |
| F29 | 02, 03 | R3-02, 10, 11 | Output-drain/redaction regressions fail; Windows ownership absent |
| F30 | 06, 28 | R3-01, 29 | No substantiated current installer journey |
| F31 | 27 | R3-28, 30 | Actual independent Windows/native evidence now available; failures must be fixed |
| F32 | 28 | R3-29 | License label corrected; full shipped SBOM/native/signing review open |
| F33 | 28 | R3-29, 30 | Source identity independently sealed; current release artifact binding absent |
| F34 | 26 | R3-27 | Actual four-view smoke passed; populated full UI matrix absent |

Every R2 item is linked through this table. Do not use an unchanged historical `VERIFIED` status to override a newly failing regression. Conversely, do not erase already demonstrated positives merely because the full product is not accepted.

## 7. Full completion matrix beyond the current regression pack

Build the following evidence in finite verifiable slices. Use controlled credentials/endpoints; public-node smoke checks are separate time/network-specific observations, never a reproducible benchmark or a promise of universal availability.

| Area | Minimum completion evidence |
|---|---|
| Fresh user journey | Clean install/profile, consent, discovery, validation, eligible list, real TUN, traffic, refresh, failover, disconnect |
| Protocol coverage | Every admitted protocol/transport/format with native config validation, positive authenticated handshake and incorrect-secret/SNI/transport negatives |
| Source coverage | New/removed family, current branch changes, correct commit/tree/blob identity, preferred/alternate formats, mirror mismatch, 304 and offline cached start |
| Input hostility | Depth/size/record/expansion limits, malformed Unicode, duplicate/conflicting fields, wrong JSON/YAML kinds, special addresses and no untrusted policy execution |
| Probe integrity | Exact candidate/worker/target context, independent targets, broken candidate behind a good active VPN, byte/time caps, cancellation, target/uplink classification |
| State concurrency | Start/Disconnect/Exit/Failover at each await; late success/exit/stop; policy/epoch changes; lease renewal and restart; no stale resurrection |
| Privilege boundary | Real service account, restricted installation/data ACLs, actual peer token/session, two users, remote denial, spoofed pipe/core, no arbitrary commands |
| Traffic protection | Windows 11 authorized isolated host; IPv4/IPv6, DNS/DoH/bootstrap exceptions, failure during each stage, core/broker/UI death, no silent DIRECT fallback |
| Recovery | Owned-effects journal and OS reconciliation, missing/corrupt/locked files, sidecars, crash/reboot, unrelated rules preserved, cleanup refusal truthful |
| Environment changes | Sleep/resume, NIC/gateway/DNS changes, offline/captive network, competing VPN, own TUN not causing epoch storms |
| Persistence/retention | Atomic publication, concurrent handles, failed commits, backup/restore, schema refusal/migration, favorites/active preservation, expiry and overflow |
| Performance | 10,000-node measured workload, bounded CPU/memory/processes/handles, fair resume, cancellation latency and at least a declared long-duration soak |
| Interface | Actual populated/error states; manual/favorite/country/source commands; separate latency/speed/live rate; themes, DPI, keyboard, accessibility, tray and resync |
| Installation | Current retrievable artifact, protected core/driver assets, service registration, unelevated UI, upgrade rollback, uninstall/recovery and data policy |
| Supply chain/evidence | Clean source/build provenance, native+managed dependency inventory, notices/source obligations, actual signature status, retained structured results |

The intended v1 scope remains the original simple Windows client. Do not add cloud account systems, arbitrary automation, a second permanent agent framework, a general routing editor or unrelated platforms to satisfy this matrix. Improve the existing architecture and reuse audited upstream capability where appropriate.

## 8. Execution order and mandatory handoff

### Slice A: close the executable counterexamples

Port the canonical independent regressions to the implementation branch without importing audit-only workflows blindly. Fix R3-03 through R3-19's pure logic/transport issues and parser/secret/counter failures, preserving controls. Correct test-fixture portability separately. Use a red-before/green-after record; assertions that never failed for the intended reason do not substantiate closure.

### Slice B: complete the real unelevated workflow

Implement current-head discovery, fair all-source coverage, durable refresh transactions, bounded two-target candidate admission, actual on-demand negative results and a single catalogue authority. Connect actual data/commands to the UI and bounded broker handoff. Demonstrate a controlled unseeded journey before adding further abstractions.

### Slice C: complete the Windows control and protection boundary

Use the demonstrated Windows CI path for non-destructive checks now. Implement real peer/process adapters, service/core lifecycle and owned network guard/recovery. Use an authorized isolated Windows 11 environment with an independent recovery path for disruptive tests. Do not change the routes, DNS or firewall of the host carrying the only development control connection.

### Slice D: finish the UI, installer, scale and release evidence

Close the full declared matrix, measured performance/soak, every original acceptance journey, current packaging and component provenance. Keep artifacts retrievable and tied to the exact build. An unavailable signing credential may remain an honestly described test-release limitation, never an invented signature; it does not justify leaving unrelated implementation incomplete.

Create `docs/AUDIT_ROUND3_STATUS.md` with one row for every R3-01 through R3-30, every original F item and every relevant acceptance gate. Record:

```text
Finding / requirement
Status: OPEN | IN_PROGRESS | IMPLEMENTED_NOT_VALIDATED | BLOCKED | VERIFIED
Fix commit and exact production paths
Regression IDs and test names
Executed command, platform, SDK/core/driver identities
Run/job/artifact identifiers and retained checksums
Positive and adverse result
Remaining limitation and exact external prerequisite, if any
```

A gate can be VERIFIED only at the scope actually executed. A mock guard proves state logic, not packet blocking. An actual native SOCKS probe proves that path, not installed TUN. Windows Server CI proves that platform and tested interactions, not every Windows 11 edition/driver/reboot behavior. A WPF render proves the exercised window, not every control or accessibility behavior.

Commit and push coherent tested increments without force-pushing or overwriting unrelated owner work. Report meaningful milestones and blockers in Russian. Finish all independently achievable work before stopping at a genuine external prerequisite. Do not run an unbounded goal loop; clean up child tasks/processes and maintain a precise recoverable handoff between slices.

The final delivery must include exact pushed source commit/tree, build inputs, installer/artifact URL and SHA-256, actual test matrix, installation/recovery instructions and remaining risks. Preserve audit source documents and adverse evidence. A final green unit count alone is not the requested result.

## 9. Reproduction and retained evidence

The audit branch is intentionally red at the audited production baseline. Review its diff before reuse:

```bash
git fetch origin main audit/round3-e7797c7-independent
git diff --stat e7797c7d24e4f20befb7e43bfb8e72d5efd436f1 a4b2b78e2fde0399798e907daef76e9a2b5c09de
git show eabbf3f5407971cc70a497a8f1856400e5f96819:tests/AutoVpn.UnitTests/IndependentRound3Tests.cs
git show eabbf3f5407971cc70a497a8f1856400e5f96819:tests/AutoVpn.UnitTests/IndependentRound3Tests_BrokerTls.cs
```

Use a separate worktree for baseline reproduction, not a destructive checkout over owner work. Representative commands after preparing that worktree:

```text
dotnet build AutoVpn.slnx -c Release --nologo
dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --no-build --filter FullyQualifiedName~IndependentRound3Tests --logger trx
```

On Windows exclude the explicitly Linux-only A16 fixture until replaced with an equivalent Windows fixture. The committed audit workflow contains the exact OS filters and commands used. Native checks require the official binary and both recorded hash checks; never substitute an unverified executable merely to make them run.

| Evidence | Run | Job / artifact |
|---|---|---|
| Linux independent + existing suites | 37076157596 | job 111066426407; artifact 11256522324 |
| Windows independent + existing suites | 37076157596 | job 111066426632; artifact 11257425116 |
| Pinned native existing suite | 37076157596 | job 111066426258; artifact 11256761980 |
| Actual WPF smoke and four renders | 37075649029 | job 111064839369; artifact 11256421609 |
| Exact production source and audit fixtures export | 37076378485 | artifact 11256737460 |

`ROUND3_EVIDENCE_2026-10-03.json` records all downloaded archive hashes, suite counts, environments and exclusions. The full companion evidence bundle also contains raw TRX, NuGet query output, the per-file source index, fixture sources and WPF renders. It contains no live subscription credentials or packet capture.

## 10. Primary technical references

The production source and executed tests are the evidence for application defects. The following official references support protocol/API boundaries, not claims of completed runtime acceptance:

- GitHub Git Trees API, object identities and modes: https://docs.github.com/en/rest/git/trees
- Microsoft.Data.Sqlite connection options and pooling: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings
- IANA IPv4 special-purpose registry, including benchmark 198.18.0.0/15: https://www.iana.org/assignments/iana-ipv4-special-registry/iana-ipv4-special-registry.xhtml
- Microsoft NuGet audit scope: https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages
- Exact pinned Mihomo source and license: https://github.com/MetaCubeX/mihomo/tree/88dcbf7f1614a67c3b36b848ee3592dfa92ada36
- Mihomo DNS configuration: https://wiki.metacubex.one/en/config/dns/

Validate changing documentation against the pinned versions. The requested outcome is a complete, simple, honest Windows VPN client with measured safety and recovery, not a collection of correctly named but disconnected helpers.

**End of round-3 directive.**
