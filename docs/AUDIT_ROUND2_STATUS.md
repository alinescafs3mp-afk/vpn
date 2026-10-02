# Round-2 audit status

V1 is not accepted. This file records the Linux work after `for_fix/ASTRA_HARD_AUDIT_ROUND2_2026-10-03.md`. It does not replace that directive or `docs/IMPLEMENTATION_DIRECTIVE.md`.

Audited head was `560e5df0fb6c63b8a7daed55c97207b8b69be65a`. The tested code commit is `aa343d27a56a401e28dc9e43d3a85bdbd200aeb7`. V1 remains not accepted.

## Evidence used below

| Run | Command and environment | Result |
|---|---|---|
| E-C | `AUTOVPN_MIHOMO_PATH=/var/tmp/autovpn-mihomo/mihomo dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --nologo` from `/home/jericho/src/vpn` on Linux. The pinned Mihomo binary is local and not in git. | Tree committed as `5b03ac6`: 88 passed, 0 failed, 0 skipped, 8 s. The Mihomo fact ran. |
| E-B | Same command and environment, before the slice C files. | Tree committed as `5c3112b`: 83 passed, 0 failed, 0 skipped, 8 s. |
| E-build | `dotnet build` Release for `AutoVpn.Service`, `AutoVpn.Recovery`, and `AutoVpn.Desktop` (`net10.0-windows` via EnableWindowsTargeting). | 0 warnings, 0 errors on the `aa343d2` tree. No service process, WPF process, TUN, or host route change. |
| E-D | Same command and environment as E-C. | Tree committed as `aa343d2`: 94 passed, 0 failed, 0 skipped, 8 s. The Mihomo fact ran. |

A unit result is not a Windows install, packet capture, or UI click.

## R2 findings

### R2-01

Finding: candidate HTTPS test never negotiated TLS.
Original finding ids: F02, F15.
Status: VERIFIED for the non-TUN probe only.
Fix commit: `88b7506ce3f9822354980857bc3741747fe59ea3`.
Production paths: `Probe/NonTunCoreProbeTransport.cs` (`Socks5Client.ExchangeAsync`).
RT/AT tests: RT01.
Executed command and environment: E-C.
Evidence/run/artifact: `Rt01TlsOverSocksRejectsPlaintextFakeStatusBadCertificatesRedirectAndHtml` rejects plaintext 204, wrong-host certificate, untrusted certificate, redirect, HTML, and a non-204 status. `Rt01PinnedCoreAuthenticatesTlsAndBrokenCandidateDoesNot` marks the good Shadowsocks candidate Healthy with the probe SNI and digest, and the password `wrong-ss-secret` adds no TLS accept and is not Healthy.
Remaining limitation: this is not a Windows TUN session.

### R2-02

Finding: probe success was not bound to an owned worker.
Original finding ids: F07, F15, F29.
Status: IN_PROGRESS.
Fix commit: `88b7506`.
Production paths: `NonTunCoreProbeTransport.cs`, `MihomoProfileGenerator.cs`.
RT/AT tests: RT01 pinned-core half; RT03 process lifetime. No separate RT02.
Executed command and environment: E-C.
Evidence/run/artifact: probe profiles set `ExternalController=false` and do not emit the old fixed controller secret. The pinned-core success names the target URI, candidate digest, and worker id. Readiness waits until `/proc/net/tcp` shows the port owned by that pid.
Remaining limitation: occupied, reused, and swapped ports, plus an unauthorized controller request, are not a separate regression. `Rt02CurrentHeadRefreshPublishesOnlyTheAuthenticatedCandidate` is the refresh journey, not this port matrix.

### R2-03

Finding: worker lifecycle and path isolation were incomplete.
Original finding ids: F15, F29.
Status: IN_PROGRESS.
Fix commit: `88b7506`.
Production paths: `Probe/ProbeWorker` inside `NonTunCoreProbeTransport.cs`.
RT/AT tests: RT03. RT04 was not written.
Executed command and environment: E-C.
Evidence/run/artifact: a chatty child is drained and disposed within 5 s, a hung child and its credential directory are removed, a pre-canceled start is not ready, an exited child deletes the secret file, and disposing one worker does not kill a different sleep process.
Remaining limitation: a live tunnel A versus a broken candidate B has no packet-level isolation proof.

### R2-04

Finding: admission, cancellation, and measurement accounting disagree.
Original finding ids: F14, F15, F27.
Status: IN_PROGRESS.
Fix commit: `88b7506` for probe classes; `aa343d27a56a401e28dc9e43d3a85bdbd200aeb7` for the byte file and speed binding.
Production paths: `Probe/ProbeCoordinator.cs`, `Probe/ProbeByteBudget.cs`, `Refresh/CatalogueCoordinator.cs` (`SpeedMeasurement`).
RT/AT tests: probe class handling is in the slice A coordinator. RT06. RT05 was not written.
Executed command and environment: E-D.
Evidence/run/artifact: user cancel does not mark the node failed. `CoreFailure` and `Unsupported` stop the cycle without incrementing the failed count. A plaintext SOCKS status is 0. `Rt06BudgetAndUnboundSpeedDoNotBecomeHealth` spends a 5-byte limit, reloads the file as exhausted, starts the next UTC day at zero, and treats a corrupt file as zero spent. An unbound stream returns null and does not change `MedianLatencyMs`. A bound sample does not write health.
Remaining limitation: cancellation and failure injection against the worker process are not a separate regression. The budget counts reported payload bytes, not a measured NIC total. Two live targets were not admitted together.

### R2-05

Finding: 60-second admission versus the 30-minute recheck left a dead zone.
Original finding ids: F14, F28.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit: `88b7506`.
Production paths: `ProbeCoordinator.NeedsOnDemandAdmission`, `BrokerEngine.SelectReadyAsync`, `Desktop/MainWindow.xaml.cs` `ConnectClick`.
RT/AT tests: RT07.
Executed command and environment: E-C. Desktop was compiled, not clicked.
Evidence/run/artifact: a 65-second Healthy node stays on the 30-minute working list and is admitted on demand. A future timestamp is not eligible. An epoch change during the probe does not move `LastSuccessUtc`.
Remaining limitation: the desktop call is not an executed click. OS sleep was not signaled.

### R2-06

Finding: Windows still has no usable production composition.
Original finding ids: F01, F05, F10, F30.
Status: BLOCKED.
Fix commit: none that installs a core. Refusal remains in `Service/Program.cs` and `UnavailableNetworkGuard`.
Production paths: `RefusingCoreController`, `UnavailableNetworkGuard`, desktop/service projects.
RT/AT tests: RT08 not run.
Executed command and environment: E-build on Linux.
Evidence/run/artifact: the service still constructs `RefusingCoreController` and prints that filters and TUN are not installed. Desktop builds as `net10.0-windows` and was not launched.
Remaining limitation: no Windows machine, no TUN, no SCM install, no second-user pipe ACL test.

### R2-07

Finding: independent catalogue owners can lose data or cannot exchange state.
Original finding ids: F03, F23, F25.
Status: IN_PROGRESS.
Fix commit: `88b7506`.
Production paths: `Persistence/SqliteCatalogue.cs`.
RT/AT tests: RT09.
Executed command and environment: E-C.
Evidence/run/artifact: two `SqliteCatalogue` handles on one file. A stale writer gets `CATALOGUE_CONFLICT` and does not replace the committed nodes.
Remaining limitation: not two UI processes and not the service identity. The service catalogue is still separate from the desktop catalogue. Sending a node to the broker does not make that catalogue eligible.

### R2-08

Finding: UI startup, disconnect, and response ordering are not an authoritative session.
Original finding ids: F03, F34.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit: `aa343d27a56a401e28dc9e43d3a85bdbd200aeb7`. Earlier exit and consent behavior remains in `5c3112b`.
Production paths: `Application/UiSession.cs` (`SessionMailbox`, `PlanExit`), `Desktop/MainWindow.xaml.cs` (`ResyncAsync`, `ExitApplication`).
RT/AT tests: RT10. RT15 still covers the refresh fence.
Executed command and environment: E-D and E-build. No WPF process.
Evidence/run/artifact: `Rt10RealPipeResyncsAndIgnoresAnOlderReply` uses `LocalIpcServer.RoundTripAsync` on `autovpn-rt10-<guid>`. The first snapshot leaves Unknown. Disconnect during a held Start uses a snapshot taken after the core was entered. The connect result is `CANCELED`, the phase is not Connected, and the node is not the active session. The mailbox then ignores the earlier snapshot. A response with no snapshot keeps `PEER` and does not use the broker-unreachable text. `PlanExit` refuses a pending operation until a later verified Disconnected snapshot. The test does not call `engine.Snapshot()`.
Remaining limitation: the WPF window, tray, and a broker process restart were not executed. The null-snapshot case is applied to the mailbox after the live pipe session.

### R2-09

Finding: subscription refresh was frozen to the initial commit.
Original finding ids: F02, F25.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit: `5c3112b` for tree SHA selection; `aa343d27a56a401e28dc9e43d3a85bdbd200aeb7` for the consent-to-probe journey on that path.
Production paths: `Fetch/ReviewedRegistry.ContentUrls`, `Refresh/CatalogueCoordinator.cs`.
RT/AT tests: RT11 and `Rt02CurrentHeadRefreshPublishesOnlyTheAuthenticatedCandidate`.
Executed command and environment: E-B, included again in E-C and E-D.
Evidence/run/artifact: content URLs for tree SHA A contain A and not B. A later tree with only the mobile file does not wipe the previously ingested `203.0.113.10` node. An HTTP 503 reuses the cached tree. `Rt02CurrentHeadRefreshPublishesOnlyTheAuthenticatedCandidate` accepts disclosure, discovers tree `0123456789abcdef0123456789abcdef01234567`, fetches one body, probes it through the pinned core, publishes `good — ` and not `broken — `, and a following 304 keeps the good node Healthy.
Remaining limitation: the test server is local. This was not a live GitHub fetch. The desktop refresh timer was not executed. There is no separate branch-tip client beyond the registry tree URL.

### R2-10

Finding: a tree 304 stopped discovery instead of reusing a valid tree.
Original finding ids: F16, F25.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit: `88b7506`, with outage reuse in `5c3112b`.
Production paths: `CatalogueCoordinator.DiscoverAsync`, `SourceLedger.DiscoveryJson`.
RT/AT tests: RT12.
Executed command and environment: E-C.
Evidence/run/artifact: a 304 with cached discovery JSON still yields work. A missing or corrupt cache causes one unconditional refetch. A 304 with no usable cache stays incomplete.
Remaining limitation: local HTTP only.

### R2-11

Finding: ETags and success text could advance without a committed snapshot.
Original finding ids: F16.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit: `88b7506` and `5c3112b`.
Production paths: `RefreshMerge.IngestReport.Committed`, `CatalogueCoordinator.RefreshAsync`, `SourceLedger.RememberRejected`.
RT/AT tests: RT13.
Executed command and environment: E-C.
Evidence/run/artifact: a malformed body is labeled `REJECTED` and does not replace a good ETag. If `ApplySnapshot` throws, the previous ETag and node remain.
Remaining limitation: the failure is an injected `IOException`, not a disk fault on the user profile.

### R2-12

Finding: automatic refresh was a one-time startup check.
Original finding ids: F03, F28.
Status: IN_PROGRESS.
Fix commit: `5c3112b`.
Production paths: `RefreshScheduler`, `RefreshScheduleGate`, `Desktop/MainWindow.xaml.cs`.
RT/AT tests: RT14.
Executed command and environment: E-C. The timer host was compiled, not run.
Evidence/run/artifact: with disclosure refused, the scheduler completes zero cycles. After consent, 14 minutes is not due, a null per-source success is due, and 15 minutes runs another cycle.
Remaining limitation: no tray process and no OS sleep notification. The desktop timer is one minute and was not executed.

### R2-13

Finding: overlapping refreshes could publish stale work.
Original finding ids: F02, F22.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit: `5c3112b`.
Production paths: `RefreshFence`, `CatalogueCoordinator`, `MainWindow.RunRefreshAsync`.
RT/AT tests: RT15.
Executed command and environment: E-C.
Evidence/run/artifact: a slow cycle that loses `Begin` does not ingest and does not remember its ETag. The fast cycle publishes `203.0.113.21` with ETag `fast`.
Remaining limitation: an in-flight download can still finish on the wire. No WPF process was run.

### R2-14

Finding: per-file bounds did not enforce the aggregate refresh budget.
Original finding ids: F22, F24.
Status: IN_PROGRESS.
Fix commit: `5c3112b`.
Production paths: `CatalogueCoordinator.RefreshAsync`.
RT/AT tests: RT16.
Executed command and environment: E-C.
Evidence/run/artifact: nine artifacts are offered. Eight HTTP calls run. The ninth reason ends with `:CYCLE_BUDGET` and `203.0.113.18` is absent. `203.0.113.10` remains.
Remaining limitation: the cap reserves `MaxArtifactBytes` per item. It does not measure the received body. Candidate, queue, and retention limits are not in this test.

### R2-15

Finding: 256 completed responses could permanently disable Disconnect.
Original finding ids: F04, F06.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit: `88b7506`.
Production paths: `Application/IpcDispatcher.cs`.
RT/AT tests: RT17.
Executed command and environment: E-C.
Evidence/run/artifact: reads, safety commands, and other mutations are separate. The same id with a different fingerprint is `REQUEST_CONFLICT`. An uncertain mutation replays `EFFECT_UNCERTAIN`. After the mutation window is filled, Disconnect still runs. A fresh dispatcher returns `BUSY` at the in-flight cap.
Remaining limitation: this is an in-process dispatcher, not a saturated Windows pipe.

### R2-16

Finding: pipe saturation could stop the listener, and completed tasks accumulated.
Original finding ids: F04, F05.
Status: IN_PROGRESS.
Fix commit: `5b03ac65e123db9d53fef9dfece590243b1f148d`.
Production paths: `Broker/LocalIpcServer.cs`, `Service/Program.cs`.
RT/AT tests: RT18. The older creation-failure test still expects three faults when every open throws.
Executed command and environment: E-C. The service process was not started.
Evidence/run/artifact: four partial clients keep `ActiveStreamCount` at 4 and `PipeFault` null across a further wait. Releasing one client lets a snapshot round-trip succeed. After twelve snapshots, retained sessions are at most four. Dispose finishes within 3 s. The service source returns 2 when the listener stops before the process stop token.
Remaining limitation: Linux named pipes, not Windows instance exhaustion. No sustained malformed-traffic run and no SCM host.

### R2-17

Finding: a late Start and a failed cleanup could corrupt the next session.
Original finding ids: F06, F07, F08, F09.
Status: IN_PROGRESS.
Fix commit: `88b7506` for the late Start; `5b03ac6` for cleanup ownership.
Production paths: `Broker/BrokerEngine.cs`.
RT/AT tests: RT19, RT20.
Executed command and environment: E-C.
Evidence/run/artifact: a late Start cannot clear the newer session. A failed Stop, then a failed Disarm, then a success all use the original operation id and armed generation. Protection stays armed until the last success. A journal recovery in progress rejects Connect with `RECOVERY_BLOCKED` and does not arm a new guard.
Remaining limitation: the core and guard are test doubles. No Windows process handle or filter object was owned.

### R2-18

Finding: failover could keep false process state and stale proof.
Original finding ids: F08.
Status: IN_PROGRESS.
Fix commit: `88b7506`.
Production paths: `BrokerEngine.ReportHealthAsync`, `ConfirmProduction`.
RT/AT tests: RT21.
Executed command and environment: E-C.
Evidence/run/artifact: core exit at the switch cap does not become Connected from a late positive confirmation. Cooldown expiry can stage another attempt. A duplicate health event during staging does not block it. A pinned session stays protected.
Remaining limitation: the core is a test double. A target outage is covered by the existing broker test, not by a live process.

### R2-19

Finding: protection and durable recovery were unfinished.
Original finding ids: F09, F10, F11.
Status: IN_PROGRESS.
Fix commit: `5c3112b` for the marker; `5b03ac6` recovers the journal before disarm.
Production paths: `Persistence/EffectJournal.cs`, `Recovery/Program.cs`, `BrokerEngine.DisconnectAsync`.
RT/AT tests: RT22. `UnsupportedJournalSchemaIsLeftUntouched` still passes.
Executed command and environment: E-C. Recovery was built, not run as a separate process.
Evidence/run/artifact: a corrupt journal is quarantined with its WAL sidecar. The replacement database has no open effects, and recovery stays incomplete. Deleting the sqlite file leaves `.recovery-unknown`, and a later open is still incomplete. Pooling is off so the old connection cannot resurrect the row. The recovery program returns 2 when the file is missing and the marker exists.
Remaining limitation: no Windows route, DNS, or WFP object is reconciled. The marker is not proof that OS state was repaired.

### R2-20

Finding: emitter validation and protocol mapping disagreed.
Original finding ids: F17, F18, F19, F20.
Status: IN_PROGRESS.
Fix commit: `88b7506` for the emitter; `5b03ac6` for multi-record Xray and Clash JSON.
Production paths: `MihomoProfileGenerator.cs`, `XrayOutboundParser.cs`, `ClashProxyParser.ParseJson`, `SubscriptionImporter.ImportJson`.
RT/AT tests: RT23, RT24.
Executed command and environment: E-C.
Evidence/run/artifact: direct security values emit `tls: true` after trim and lower-case. H2 emits `h2-opts` and websocket emits quoted `network: 'ws'`. Two `vnext` records and two Shadowsocks `servers` records all keep their hosts. A flat Clash JSON proxy imports as `203.0.113.40` with `tls`.
Remaining limitation: no controlled H2 handshake and no native Xray peer. Nested conflicting options are not fully covered.

### R2-21

Finding: UDP identity merged different emitted behavior, and migration could retag health.
Original finding ids: F21.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit: `88b7506`.
Production paths: `Domain/NodeSemantics.cs`, `Catalogue.ReconcileStoredDigest`, `MihomoProfileGenerator.AppendProxy`.
RT/AT tests: RT25. Import digest test updated in the same commit.
Executed command and environment: E-C.
Evidence/run/artifact: canonicalizer version 3 writes `udp` from `node.Udp ?? false`. Null and false share an identity. True differs. A changed opaque path keeps the favorite and clears the assessment.
Remaining limitation: no historical user catalogue was migrated on a Windows profile.

### R2-22

Finding: wrapper limits and schemas rejected valid feeds or dropped records.
Original finding ids: F17, F20.
Status: IN_PROGRESS.
Fix commit: `5b03ac6`.
Production paths: `SubscriptionImporter.ImportLines`, `Base64Text.TryDecode`, `SourceLedger.Load`, Clash JSON path above.
RT/AT tests: RT26, RT24.
Executed command and environment: E-C.
Evidence/run/artifact: a Base64 document larger than 64 KiB and smaller than 8 MiB imports 500 small VLESS records. A Base64 body of a CRLF pair imports both. A non-wrapper line over 64 KiB is `SIZE_LIMIT`. A ledger array `[null, entry]` loads the entry and does not quarantine the file.
Remaining limitation: expansion, depth, and malformed Unicode are not a full abuse set. Nested Clash options are delegated to the YAML parser and were not exhaustively retested.

### R2-23

Finding: DNS, IPv6, and resolved-destination safety lack implementation and path proof.
Original finding ids: F12, F13.
Status: OPEN.
Fix commit: none for the packet path. Loopback probe profiles set `ipv6: false` and `dns.enable: false` in `88b7506`.
Production paths: `EndpointSafety`, probe profile DNS block.
RT/AT tests: RT27 not written. RT04 not written.
Executed command and environment: none for packets.
Evidence/run/artifact: none.
Remaining limitation: no resolver rebinding control and no Windows packet capture. This host's network was not changed.

### R2-24

Finding: owner policy is not applied from UI through probe and failover.
Original finding ids: F03, F26, F27, F28.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit: `aa343d27a56a401e28dc9e43d3a85bdbd200aeb7`.
Production paths: `ProbeCoordinator.Scheduled` and `PolicyHeld`, `BrokerEngine.PolicyStamp`, `MihomoProfileGenerator.AppendProxy`.
RT/AT tests: RT28, including `Rt28InsecureProxyFlagDoesNotAuthenticateTheProbeTarget`.
Executed command and environment: E-D. No WPF click.
Evidence/run/artifact: a strict-country change during a held probe publishes nothing. An excluded node, a strict FI-versus-DE node, and `SkipCertVerify` without consent are not attempted. With consent, the recording transport sees the insecure flag and one success. Connect forwards `protectionRequired: false`, then a settings revision change during Start returns `POLICY_CHANGED`, phase Blocked, protection armed, and no active node. A failover start after `AllowInsecureCertificates` changes is stopped; the previous node stays Connected. `BuildProbeYaml` without the flag throws `CERT_VERIFICATION_DISABLED`. `Socks5Client` rejects an untrusted `probe.example` certificate when no trust anchor is passed.
Remaining limitation: the settings window was not clicked. Disabled-family and protection-on-connect were not each injected on every pending path. This is not a Windows TUN policy test.

### R2-25

Finding: catalogue saves and working-list rendering stay quadratic.
Original finding ids: F23, F24.
Status: IN_PROGRESS.
Fix commit: `88b7506` for cross-handle conflicts; `aa343d27a56a401e28dc9e43d3a85bdbd200aeb7` for in-place assessment updates and one eligibility pass.
Production paths: `SqliteCatalogue.TryUpdateInPlace`, `CataloguePresentation.Servers`.
RT/AT tests: RT09 and RT29.
Executed command and environment: E-D.
Evidence/run/artifact: `Rt29OneAssessmentDoesNotReprotectTheOtherSecrets` ingests two nodes, keeps the protector call count at 2, and leaves both `semantics_blob` values unchanged when one latency becomes 40. The other node stays Pending. Eight in-memory nodes produce one `Eligible` call, and the working text contains `203.0.113.10`. `At24FailedSqliteCommitDoesNotPublishTheCopy` now lets a favorite and an assessment commit without the protector, and an identity-changing snapshot that throws `IOException` is absent after reopen.
Remaining limitation: the run used 2 SQLite nodes and 8 memory nodes. It did not measure SQL, memory, or render time at 10_000 nodes. No WPF list was drawn.

### R2-26

Finding: the desktop lacks the required controls and honest metrics.
Original finding ids: F26, F27, F34.
Status: OPEN.
Fix commit: none that closes the UI matrix.
Production paths: `Desktop/MainWindow.xaml`, `MainWindow.xaml.cs`, `CataloguePresentation`.
RT/AT tests: RT30 not run.
Executed command and environment: E-build compiled the desktop project. No window.
Evidence/run/artifact: none from a click, keyboard, theme, or DPI pass.
Remaining limitation: manual selection, favorites, live traffic, and tray were not exercised.

### R2-27

Finding: tests validated the wrong layer or the wrong protocol.
Original finding ids: F31.
Status: IN_PROGRESS.
Fix commit: `88b7506` for RT01; later commits add RT11–RT26 as named above.
Production paths: the tests call the production types listed in those findings.
RT/AT tests: RT31 is the pack itself, not one extra test.
Executed command and environment: E-C.
Evidence/run/artifact: the pinned-core positive and the broken-candidate negative both ran, including inside the consent-to-304 journey. CI still leaves `AUTOVPN_MIHOMO_PATH` empty, so that fact skips there. E-D did not skip.
Remaining limitation: no Windows job and no installer journey.

### R2-28

Finding: installer, provenance, licensing, and closure reports were incomplete.
Original finding ids: F30, F32, F33.
Status: IN_PROGRESS.
Fix commit: `5c3112b` for the license text. No installer commit.
Production paths: `THIRD_PARTY_NOTICES.md`, `config/core-manifest.json`.
RT/AT tests: RT32 not run.
Executed command and environment: the Mihomo `LICENSE` at commit `88dcbf7f1614a67c3b36b848ee3592dfa92ada36` was re-read on 2026-10-03. It is the GNU GPL version 3 text. No SPDX `-only` or `-or-later` suffix is asserted.
Evidence/run/artifact: the notice and manifest say that. There is no SBOM, no Authenticode signature, and no retrievable installer for `5b03ac6`.
Remaining limitation: the old local archive is not this source. A clean Windows machine cannot yet install this commit. Signing unavailability is not a signature.

## Original findings

| Original | Status | Round-2 linkage | What remains |
|---|---|---|---|
| F01 | BLOCKED | R2-06, R2-28 | No production core or SCM composition. |
| F02 | IN_PROGRESS | R2-01, R2-07 through R2-14 | Probe TLS, the refresh fence, and the local current-head journey are tested. Privileged handoff and live GitHub are not. |
| F03 | IN_PROGRESS | R2-07, R2-08, R2-12, R2-24, R2-26 | The session mailbox and a real pipe resync passed. The WPF window and tray were not run. |
| F04 | IN_PROGRESS | R2-15 through R2-18 | Idempotency and the four-stream wait are tested. Windows saturation is not. |
| F05 | BLOCKED | R2-06, R2-16 | Unverified Windows peers are rejected. That is not authorization. |
| F06 | IN_PROGRESS | R2-15, R2-17 | Disconnect survives the mutation window. The pipe is not the Windows service. |
| F07 | IN_PROGRESS | R2-02, R2-17 | Late Start and cleanup retry are tested on doubles. |
| F08 | IN_PROGRESS | R2-18 | Core-exit and cooldown unit cases passed. Process truth is not a live core. |
| F09 | IN_PROGRESS | R2-17, R2-19 | Failed Start keeps protection. Cleanup retry and the journal marker do not restore OS objects. |
| F10 | BLOCKED | R2-06, R2-19, R2-23 | No real protection and no packet evidence. |
| F11 | IN_PROGRESS | R2-19 | The marker survives restart. OS reconciliation does not exist. |
| F12 | OPEN | R2-23 | DNS and IPv6 path proof are absent. |
| F13 | OPEN | R2-23 | Rebinding control is absent. |
| F14 | IMPLEMENTED_NOT_VALIDATED | R2-04, R2-05 | RT07 passed. The desktop click and OS sleep did not. |
| F15 | IN_PROGRESS | R2-01 through R2-05 | The TLS probe is verified at its own layer. The daily file and unbound-stream rule passed. Worker cancellation and path isolation did not. |
| F16 | IMPLEMENTED_NOT_VALIDATED | R2-10, R2-11 | Malformed preservation, 304 reuse, and ETag failure passed on a local server. |
| F17 | IN_PROGRESS | R2-20, R2-22 | Large Base64 and null ledger entries passed. Nested limits are incomplete. |
| F18 | IN_PROGRESS | R2-20 | Emitter strings passed. No native handshake. |
| F19 | IMPLEMENTED_NOT_VALIDATED | R2-20 | Bogus URI security and direct mixed-case emission are covered. Not every transport. |
| F20 | IN_PROGRESS | R2-20, R2-22 | H2 options and multi-record Xray hosts are covered. No live H2 peer. |
| F21 | IMPLEMENTED_NOT_VALIDATED | R2-21 | UDP identity matches emission. No real user database was migrated. |
| F22 | IMPLEMENTED_NOT_VALIDATED | R2-13, R2-14 | Handler timeout and redirect remain. Aggregate budget is only a reservation. |
| F23 | IMPLEMENTED_NOT_VALIDATED | R2-07, R2-25 | Two handles cannot erase each other. One assessment did not re-protect the other secret. The 10_000-node bound was not measured. |
| F24 | IN_PROGRESS | R2-14, R2-25 | An unchanged identity no longer rewrites secrets. Retention and the measured refresh body are still open. |
| F25 | IN_PROGRESS | R2-09 through R2-11 | Tree SHA selection is tested locally, not against live GitHub. |
| F26 | IN_PROGRESS | R2-24, R2-26 | Strict country skips a probe in the coordinator. The settings window was not clicked. |
| F27 | IN_PROGRESS | R2-04, R2-24, R2-26 | A bound download sample does not write health. It is still not NIC throughput. |
| F28 | IN_PROGRESS | R2-05, R2-12, R2-24 | The scheduler unit passed. The WPF timer was not run. |
| F29 | IN_PROGRESS | R2-02, R2-03 | The probe controller secret is gone and output is bounded. Redaction is not a full pass. |
| F30 | BLOCKED | R2-06, R2-28 | No installer. |
| F31 | IN_PROGRESS | R2-27 | This Linux run executed the Mihomo fact. Windows CI does not. |
| F32 | IN_PROGRESS | R2-28 | The GPL text is recorded without an SPDX suffix. No SBOM. |
| F33 | OPEN | R2-28 | No current retrievable artifact. |
| F34 | OPEN | R2-08, R2-26 | The mailbox was tested without a window. No executed Russian UI matrix. |
