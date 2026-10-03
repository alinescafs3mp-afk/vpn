# Round-5 audit status

V1 is not accepted. This file records work after `for_fix/ASTRA_HARD_AUDIT_ROUND5_2026-10-03.md`. It does not replace that directive, the round-2, round-3, or round-4 directives, `for_fix/ASTRA_HARD_AUDIT_FIX_DIRECTIVE_2026-10-02.md`, or `docs/IMPLEMENTATION_DIRECTIVE.md`.

Audited production commit was `49e5bd54e41731b9b96b83789def31e86c701ee6`. The round-5 directive arrived in `5057d6606d07d5afc93b0665134503ba748b4173`. Linux heads recorded here are `b886200b85baa916783e1de942c4fdbfc3700e0c` and `ebaefa5dcd7f90eb35dc441e9faaaa8649ddad79`.

Allowed statuses are OPEN, IN_PROGRESS, IMPLEMENTED_NOT_VALIDATED, BLOCKED_EXTERNAL, and VERIFIED. A Linux unit result is not a Windows install, a packet capture, or a clicked window. No row is VERIFIED. No row is BLOCKED_EXTERNAL: the missing installer, service, and packet proof are implementation gaps.

## Evidence used below

| Run | Command and environment | Result |
|---|---|---|
| E-34 | `dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --nologo --filter FullyQualifiedName!~Native_Round5` on Linux, after the Slice A edits and before `b886200`. Tests copied from `ff87b7dcd36c86d441edf4bcc21813a81ad54647`. The audit workflows were not merged. | IndependentRound5: 34 passed, 0 failed, 0 skipped. |
| E-206 | `AUTOVPN_MIHOMO_PATH=/var/tmp/autovpn-mihomo/mihomo R5_CORE_PATH=/var/tmp/autovpn-mihomo/mihomo R5_CORE_HASH=3122d100e8177501776109f1a6253a694611627cf4d7c7ec82705855cf8626a8 dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --nologo` from `/home/jericho/src/vpn`. SDK 10.0.112. Pinned Mihomo `v1.19.32` commit `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`, Linux SHA-256 `3122d100e8177501776109f1a6253a694611627cf4d7c7ec82705855cf8626a8`. The binary is local and not in git. | Tree committed as `b886200`: 206 passed, 0 failed, 0 skipped, 50 s. Includes IndependentRound5 and Native_Round5. This host's routes, DNS, and firewall were not changed. |
| E-211 | Same command and environment, after the schedule and single-row assessment change. | Tree committed as `ebaefa5`: 211 passed, 0 failed, 0 skipped, 30 s. Desktop `net10.0-windows` Release build completed on Linux and was not executed. |
| E-gha-b886200 | GitHub Actions run `37138698991`, job `unit`, commit `b886200`. `AUTOVPN_MIHOMO_PATH` empty. Filter `FullyQualifiedName!~Native_Round5`. | Failed. 192 passed, 1 failed, 3 skipped, 196 total. The failure was `T03_Control_AuthenticatedExpected204CanPass`: `OperationCanceledException` inside loopback `TcpClient.ConnectAsync` under a 3-second budget. Native facts were skipped, not passed. |
| E-gha-ebaefa5 | GitHub Actions run `37139116734`, commit `ebaefa5`. Jobs `unit` and `native-linux`. | Both jobs succeeded. `native-linux` checked `config/core-manifest.json` archive SHA-256 `8451100836c9eda194331c2babfad490b2faf30cebc1a04b0e76fd8ac2d35d10` and executable SHA-256 `3122d100e8177501776109f1a6253a694611627cf4d7c7ec82705855cf8626a8`, then ran `FullyQualifiedName~Native_Round5`. This is not a Windows run. |
| E-gha-89b17b1 | GitHub Actions run `37139241627`, commit `89b17b1`. | `native-linux` succeeded. `unit` failed. 197 passed, 1 failed, 3 skipped, 201 total. The failure was `Q24_Contradictory204FramingMustNotPass` for `Content-Length : 3`: `OperationCanceledException` in loopback `TcpClient.ConnectAsync` under the 5-second budget. The framing assertion did not run. |

The audit's earlier runs (`37133249313`, `37133873472`, `37134634399`) describe `49e5bd5`. They are not evidence for `b886200` or `ebaefa5`. `docs/evidence/build-manifest.json` is an older package and is not cited.

## R5 findings

### R5-01

Finding: the installed Windows VPN is still the refusing core and guard.
Status: OPEN.
Links: R4-01, F01, F10.
Fix commit and production paths: none. `AutoVpn.Service` still constructs `RefusingCoreController` and `UnavailableNetworkGuard`.
Regression ids: none added.
Executed environment: not run. E-211 does not start a service.
Remaining limitation: no SCM service, no owned TUN core, no Windows packet exchange.

### R5-02

Finding: Windows authorization and the catalogue-to-broker handoff.
Status: OPEN.
Links: R4-02, F03, F05.
Fix commit and production paths: none.
Regression ids: none.
Executed environment: E-211 does not open a Windows pipe or a second account.
Remaining limitation: no SID/ACL/session lease and no unelevated catalogue handoff.

### R5-03

Finding: discovery must bind one validated commit object to its immutable tree.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: R4-03, F02, F22, F25.
Fix commit and production paths: `b886200`. `Fetch/GithubTreeParser.cs` (`TryReadCommit`), `Refresh/CatalogueCoordinator.cs` (`ResolveCommitAsync`, `DiscoverAsync`). A 40-hex tree URL still skips the commit API. The pin remains the fallback when the commit object cannot be read.
Regression ids: S01, S02, C01. Q07, Rt11, Rt12, and A13 stayed green.
Executed environment: E-206 and E-211 on Linux. No live GitHub metadata read in this round.
Positive and adverse result: a blob object and a tree listing are not commit objects. The fetch URL is `git/trees/{treeSha}` when the commit names a tree, and a document whose sha disagrees is incomplete.
Remaining limitation: no bounded live metadata smoke on this commit, and no Windows discovery run.

### R5-04

Finding: a ledger etag is not a recoverable subscription snapshot.
Status: IN_PROGRESS.
Links: R4-05, F16, F22, F25.
Fix commit and production paths: `b886200`, `Refresh/CatalogueCoordinator.cs` (`DownloadAsync`). An empty catalogue refetches through a 304. A catalogue that still holds the artifact may reuse 304.
Regression ids: S03. Q06 stayed green.
Executed environment: E-206 and E-211 on Linux.
Positive and adverse result: S03 makes two fetches and restores one node. A valid empty document stays eligible.
Remaining limitation: the catalogue and the ledger are not one crash-safe transaction. The ledger is still metadata, not a committed artifact snapshot.

### R5-05

Finding: a malformed HTTP 200 must not stop mirror selection.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: F02, F16, F22, F25.
Fix commit and production paths: `b886200`, `Refresh/CatalogueCoordinator.cs` (`DownloadAsync`, `DocumentEligible`).
Regression ids: S04. Rt13 stayed green: the last good etag is not replaced, and a later conditional GET still runs.
Executed environment: E-206 and E-211 on Linux.
Positive and adverse result: the malformed primary is not published; the reviewed mirror is fetched. If every URL is ineligible, the first bad body is ingested so the REJECTED path still runs.
Remaining limitation: mirror disagreement beyond that pair is not separately measured.

### R5-06

Finding: durable queue state and distinct schedule times.
Status: IN_PROGRESS.
Links: R4-04, R4-05, F02, F16, F25, F28.
Fix commit and production paths: `b886200` and `ebaefa5`. `Refresh/SourceLedger.cs` (`LastAttemptUtc`, `LastContentUtc`, `RetryAfterUtc`, `FailureCount`, `LiveSuccessStamps`). `Refresh/CatalogueCoordinator.cs`. `Desktop/MainWindow.xaml.cs` uses the live stamps. A negative cursor is wrapped, including a persisted `-1`.
Regression ids: S05, S06. Q05 and Q06 stayed green. `Round5ScheduleTests` covers an immutable tree URL, a silent artifact beside a fresh branch, unchanged-versus-changed content time, fetch backoff, and a hostile negative failure count.
Executed environment: E-211 on Linux. The desktop timer was compiled and not clicked.
Positive and adverse result: S06 advances `LastSuccessUtc` on the TreeApi key when the cached tree sha matches. A 503 waits out the retry window and is fetched again after it. A rejected body that still has a last-good etag is not held, so Rt13 can revalidate it.
Remaining limitation: ledger history is not capped. Last attempt, last content change, and retry-after are stored, but the catalogue and the ledger still commit separately. The UI process was not executed.

### R5-07

Finding: health proof needs an attempt lifetime and ordered publication.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: R4-06, F06, F07, F14, F15.
Fix commit and production paths: `b886200`. `Probe/ProbeCoordinator.cs` (`ProbePublication`, `ProofToken`, `AdmitIfStaleAsync`). `Domain/Eligibility.cs`. `Application/Catalogue.cs`.
Regression ids: P01, P02 both orderings, P03, C02.
Executed environment: E-206 and E-211 on Linux. Synthetic transports, not a remote core forging traffic.
Positive and adverse result: the same proof token does not become healthy on another network epoch. An older completion does not replace a newer assessment. Success plus Unsupported does not pass admission. C02 still connects and disconnects.
Remaining limitation: the proof token is an internal publication identity, not a packet-level attestation.

### R5-08

Finding: the verified catalogue is not yet an independently paced maintenance loop.
Status: IN_PROGRESS.
Links: R4-04, R4-07, R4-09, R4-24, F02, F14, F15, F24, F27, F28.
Fix commit and production paths: `ebaefa5` only for source backoff and live scheduling. Probe passes are still invoked by the caller, not by a separate long-lived queue.
Regression ids: none that close the maintenance loop. Q04 still paces a probe retry.
Executed environment: E-211 on Linux.
Remaining limitation: no resumable per-node health queue, no measurement history beyond the current assessment, and no proof that candidate B cannot pass through tunnel A.

### R5-09

Finding: a deliberate manual exclusion must survive admission and confirmation.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: R4-08, R4-17, R4-28, F03, F19, F26.
Fix commit and production paths: `b886200`. `Probe/ProbeCoordinator.cs` (`AdmitIfStaleAsync` purpose) and `Broker/BrokerEngine.cs` (`ConfirmProduction`, `SelectReadyAsync`). Hard country, certificate, and disabled-family checks stay. The default purpose remains PreConnect.
Regression ids: B01 at ages 0 and 65.
Executed environment: E-206 and E-211 on Linux.
Positive and adverse result: a manually selected excluded node reaches Connected at both ages. Automatic selection still rejects an excluded node.
Remaining limitation: the owner warning UI was not executed.

### R5-10

Finding: cancellation must leave a phase other than Connecting and must not commit a canceled failover.
Status: IN_PROGRESS.
Links: F07.
Fix commit and production paths: `b886200`, `Broker/BrokerEngine.cs`. Cleanup Stop uses `CancellationToken.None`.
Regression ids: B02, B05.
Executed environment: E-206 and E-211 on Linux, with scripted cores.
Positive and adverse result: cancel after Start returns a non-Ok result, an empty active set, and a phase other than Connecting. A canceled failover does not commit the standby.
Remaining limitation: a real Windows pipe Disconnect during a stuck arm was not executed.

### R5-11

Finding: cleanup ownership must be recorded before Start returns.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: F07.
Fix commit and production paths: `b886200`, `Broker/BrokerEngine.cs` (`_ownedOperationId`, `_ownedGeneration`). IOException and a failed policy Stop do not clear that identity. A superseded attempt does not clear a newer session.
Regression ids: B03, B04.
Executed environment: E-206 and E-211 on Linux.
Positive and adverse result: a later Disconnect removes the resource after both partial failures.
Remaining limitation: this is a lifecycle double, not an installed process.

### R5-12

Finding: failover must recheck the advertised country and keep the connect-time LAN policy.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: R4-28, F26, F27.
Fix commit and production paths: `b886200`, `Broker/BrokerEngine.cs` (`_sessionLanAccess`, `CountryAllows`).
Regression ids: B06, B07.
Executed environment: E-206 and E-211 on Linux. Profiles were generated, not installed.
Positive and adverse result: a standby whose advertised country leaves the strict set is not committed. `ConnectPayload.LanAccess=false` stays off the failover profile.
Remaining limitation: no packet capture of LAN traffic.

### R5-13

Finding: the bloom must not be the authority that denies a fresh safety command.
Status: IN_PROGRESS.
Links: F06.
Fix commit and production paths: `b886200`, `Application/IpcDispatcher.cs`. Disconnect and RecoverOwned skip the bloom and still use the exact retired set. Other mutations still use the bloom. The 8192 ring and the 256-entry exact cache stay bounded.
Regression ids: I01. Q09 stayed green.
Executed environment: E-206 and E-211 on Linux. The handler is side-effect free.
Positive and adverse result: after 100000 ReportHealth ids, Disconnect `R5-disconnect-70` is Ok. An aged-out ReportHealth id still does not execute.
Remaining limitation: a safety id that leaves both the 64-entry safety cache and the 8192 ring can still replay. No authenticated pipe carried this test.

### R5-14

Finding: the pipe server still waits for the whole engine operation.
Status: OPEN.
Links: R4-02, F06.
Fix commit and production paths: none in this round. `Broker/LocalIpcServer.cs` still calls `HandleAsync(...).GetAwaiter().GetResult()` inside `ServeOneAsync`.
Regression ids: none added.
Executed environment: not run as a Windows pipe.
Remaining limitation: slow connect, stop, and recovery still occupy the serving call.

### R5-15

Finding: a UI snapshot must match the protocol and the process.
Status: IN_PROGRESS.
Links: F03, F06.
Fix commit and production paths: `b886200`, `Application/UiSession.cs`. `SessionMailbox.Apply` rejects `ProtocolVersion != 1` without replacing state. `FromSnapshot` requires `!CoreRunning` before a verified disconnect.
Regression ids: I02, I03.
Executed environment: E-206 and E-211 on Linux.
Positive and adverse result: protocol 999 does not replace Disconnected. Disconnected with `CoreRunning=true` is not a verified disconnect.
Remaining limitation: request-id correlation and an authenticated boot handshake are not a separate executed control.

### R5-16

Finding: an endpoint must not inherit a sibling user's flow, and `allowInsecure` must be read.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: F19.
Fix commit and production paths: `b886200`, `Import/XrayOutboundParser.cs`. Endpoint user, password, encryption, flow, and alter-id come from that endpoint. `tlsSettings.allowInsecure: true` sets `SkipCertVerify` when the owner opt-in is on. VMess still emits omitted AlterId as 0.
Regression ids: X01, X02, X03. Native_Round5 profile facts passed inside E-206 and E-211.
Executed environment: E-211 on Linux with the pinned core. No live proxy.
Positive and adverse result: the second VLESS user does not receive the first user's flow. A second Shadowsocks server without a password stays non-pending. Explicit allow-insecure is preserved. TLS verification is not disabled by default.
Remaining limitation: no handshake matrix beyond the executed Shadowsocks and profile-syntax facts.

### R5-17

Finding: HTTP field names must be RFC 9110 tokens.
Status: IN_PROGRESS.
Links: F15.
Fix commit and production paths: `b886200`, `Probe/NonTunCoreProbeTransport.cs` (`HeaderFraming`, `IsFieldToken`). Transfer-Encoding stays rejected. Duplicate Content-Length stays rejected. TLS verification is unchanged.
Regression ids: W01 three names, W02, W03.
Executed environment: E-206 and E-211 on Linux, local TLS peer.
Positive and adverse result: leading space, an embedded space, and NUL fail closed. A valid 204 succeeds. An untrusted certificate fails.
Remaining limitation: split and coalesced header reads, delayed bodies, and oversize bodies are not a separate new control set.

### R5-18

Finding: persisted settings and hostile ledger fields need a typed reject.
Status: IN_PROGRESS.
Links: R4-08, F16, F19.
Fix commit and production paths: `b886200` (`ProductSettings.Validate` rejects a null `DisabledFamilyIds`) and `ebaefa5` (a negative `FailureCount` loads as 0). The cursor is still normalized at use, including `int.MinValue`.
Regression ids: D01, S05. `LedgerRoundTripKeepsScheduleFieldsAndClampsANegativeFailureCount`.
Executed environment: E-211 on Linux.
Remaining limitation: future schemas, nested key conflicts, and truncated bodies are not a new fuzz set.

### R5-19

Finding: a returned cancellation still consumed bytes, and a future budget day refunded today.
Status: IN_PROGRESS.
Links: R4-07, R4-09, R4-24, F15, F22, F28.
Fix commit and production paths: `b886200`. `Probe/ProbeCoordinator.cs` charges every returned observation, including `ProbeClass.Canceled`, when the caller was not canceled. `Probe/ProbeByteBudget.cs` treats a future stored day as today exhausted at the limit.
Regression ids: P04, D02.
Executed environment: E-206 and E-211 on Linux.
Positive and adverse result: 100 canceled payload bytes are spent. A file dated 2026-10-04 loaded on 2026-10-03 is exhausted. A past day still resets to 0.
Remaining limitation: routine, on-demand, and speed work do not share one atomic reservation. Payload bytes are not wire bytes.

### R5-20

Finding: catalogue file lifetime. Do not quarantine a live database, and do not clear every pool as the fix.
Status: IN_PROGRESS.
Links: R4-02.
Fix commit and production paths: `ebaefa5`, `Persistence/SqliteCatalogue.cs`. The catalogue connection sets `Pooling=false`, matching the effect journal. The header probe uses `FileShare.ReadWrite | FileShare.Delete`. An `IOException` from that probe does not move the file aside.
Regression ids: Rt09 stayed green. No new Windows file-lifetime run.
Executed environment: E-211 on Linux.
Remaining limitation: the nine Windows file-lifetime failures were not re-run. Same-process reopen was not executed under WPF.

### R5-21

Finding: one assessment must not rewrite every credential.
Status: IN_PROGRESS.
Links: R4-24, F24.
Fix commit and production paths: `ebaefa5`, `Persistence/SqliteCatalogue.cs` (`ApplyAssessment`). The update writes `assessment_json` and the revision for that node. `Protect` is not called again.
Regression ids: `AssessmentUpdateDoesNotRepretectSiblingCredentials`. The existing SQLite round trip stayed green.
Executed environment: E-211 on Linux, two nodes.
Positive and adverse result: the sibling semantics blob is unchanged, the sibling stays Pending, and the target latency survives reopen.
Remaining limitation: other mutations still copy the in-memory catalogue. The 10000-node allocation workload was not repeated. No row is a portable timing claim.

### R5-22

Finding: DNS, IPv6, and resolved-address boundaries are not packet-proven.
Status: OPEN.
Links: F10.
Fix commit and production paths: none in this round.
Regression ids: none added.
Executed environment: not run. No capture.
Remaining limitation: no isolated Windows packet control.

### R5-23

Finding: the journal is not an owned OS recovery backend.
Status: OPEN.
Links: F10.
Fix commit and production paths: none in this round. Rt22 and the duplicate-removal regression stayed green and were not weakened.
Regression ids: none added.
Executed environment: E-211 does not change routes, DNS, or firewall.
Remaining limitation: no route, DNS, adapter, or filter reconciliation.

### R5-24

Finding: owned child cleanup is not yet one reviewed lifecycle.
Status: OPEN.
Links: F07.
Fix commit and production paths: none in this round. The existing worker drain and Rt03 output bounds stayed green.
Regression ids: none added.
Executed environment: E-211. Native_Round5 used the pinned Linux binary as a validator, not as a faulted child matrix.
Remaining limitation: the profile validator can still outlive a confirmed exit, and Windows job/ACL coverage was not run.

### R5-25

Finding: the desktop server list is not a usable selector.
Status: OPEN.
Links: F03, F26, F27.
Fix commit and production paths: none. `MainWindow` still schedules from the ledger; the server surface was not replaced.
Regression ids: none added.
Executed environment: the desktop project built on Linux and was not started.
Remaining limitation: no clicked refresh, selection, favorite, exclusion, tray, or theme pass.

### R5-26

Finding: there is no retrievable installer bound to this commit.
Status: OPEN.
Links: R4-01, F01.
Fix commit and production paths: none. Packaging still publishes folders. No SBOM and no Authenticode signature were produced.
Regression ids: none added.
Executed environment: not run.
Remaining limitation: a missing installer is not a signing-only gap. No artifact size or SHA-256 is claimed.

### R5-27

Finding: native facts must stay explicit, and Windows failures must not be hidden.
Status: IN_PROGRESS.
Links: none from R4 beyond the existing Windows baseline.
Fix commit and production paths: `b886200` excludes `FullyQualifiedName~Native_Round5` from the ordinary unit job so a missing binary is not a green skip. `ebaefa5` adds `.github/workflows/ci.yml` job `native-linux`, which checks the archive and executable SHA-256 values in `config/core-manifest.json` and then runs `FullyQualifiedName~Native_Round5`.
Regression ids: Native_Round5, executed locally in E-206 and E-211 because the environment variables were set. The ordinary unit job leaves `AUTOVPN_MIHOMO_PATH` empty.
Executed environment: E-211 locally, E-gha-ebaefa5, and E-gha-89b17b1. Both red unit jobs died in loopback `ConnectAsync` before the HTTP assertion. Native facts were skipped on the red unit jobs and passed on `native-linux`.
Remaining limitation: the Windows job still has the previously reported pipe, file-lifetime, and `/usr/bin/pkill` failures. Those tests were not deleted and were not re-run. `Socks5Client.ExchangeAsync` retries one canceled loopback connect when the caller token is still live, then starts a fresh exchange budget. Caller cancellation still propagates. The 204 and framing assertions are unchanged. T01–T03 allow 15 seconds. This retry is not yet observed on Actions.

### R5-28

Finding: acceptance is the install-to-disconnect journey, not a pass count.
Status: OPEN.
Links: the original directive and every OPEN row above.
Fix commit and production paths: this file.
Regression ids: none that close acceptance.
Executed environment: E-211.
Remaining limitation: Windows 11, TUN/WFP, SCM, installer, SBOM, Authenticode, packet capture, tray, sleep, and two-account ownership were not run. v1 is not accepted.
