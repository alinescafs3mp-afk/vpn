# Round-4 audit status

V1 is not accepted. This file records work after `for_fix/ASTRA_HARD_AUDIT_ROUND4_2026-10-03.md`. It does not replace that directive, the round-2 or round-3 directives, `for_fix/ASTRA_HARD_AUDIT_FIX_DIRECTIVE_2026-10-02.md`, or `docs/IMPLEMENTATION_DIRECTIVE.md`.

Audited production commit was `12c4b920a478a6f66945d7d7512d9ada319a559c`. The round-4 directive arrived in `8ee0c71a294d587c6a8188aee1f1e0581d30212d`. The Linux counterexample head recorded here is `d8e7827ca87b0d656f9f5f2773113544bc8eceed`.

Allowed statuses are OPEN, IN_PROGRESS, IMPLEMENTED_NOT_VALIDATED, BLOCKED_EXTERNAL, and VERIFIED. A Linux unit result is not a Windows install, a packet capture, or a clicked window. No row is VERIFIED.

## Evidence used below

| Run | Command and environment | Result |
|---|---|---|
| E-red | `dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --nologo --filter FullyQualifiedName~IndependentRound4` on Linux, before the production edits, tests copied from `a5f110b3c2f326e529f7d866ad9415062e6b64f5`. | 4 passed, 29 failed, 0 skipped, 33 total. Controls Q22, Q23, Q25, Q26 passed. |
| E-161 | `AUTOVPN_MIHOMO_PATH=/var/tmp/autovpn-mihomo/mihomo dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --nologo` from `/home/jericho/src/vpn` on Linux. SDK 10.0.112. Pinned Mihomo `v1.19.32` commit `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`, SHA-256 `3122d100e8177501776109f1a6253a694611627cf4d7c7ec82705855cf8626a8`. The binary is local and not in git. | Tree committed as `d8e7827`: 161 passed, 0 failed, 0 skipped, 28 s. Includes IndependentRound4, 33 passed. This host's routes, DNS, and firewall were not changed. |

## R4 findings

### R4-01

Finding: Windows VPN composition is still the refusing core and guard.
Status: OPEN.
Links: F01, F10, R3-01.
Fix commit and production paths: none. `AutoVpn.Service` still constructs `RefusingCoreController` and `UnavailableNetworkGuard`.
Regression ids: none added.
Executed environment: not run. E-161 does not start a service.
Remaining limitation: no SCM service, no owned TUN core, no Windows packet exchange.

### R4-02

Finding: Windows identity and catalogue-to-broker handoff.
Status: OPEN.
Links: F03, F05, R3-02, R3-22.
Fix commit and production paths: none.
Regression ids: none.
Executed environment: E-161 does not exercise a Windows pipe or a second account.
Remaining limitation: no SID/ACL/session lease and no unelevated catalogue handoff.

### R4-03

Finding: a branch tree document must not become the content commit.
Status: IN_PROGRESS.
Links: F02, F22, F25, R3-12.
Fix commit and production paths: `d8e7827`, `Fetch/GithubTreeParser.cs` (`TryReadCommitSha`), `Refresh/CatalogueCoordinator.cs` (`ResolveCommitAsync`), `Refresh/SourceLedger.cs` (`ResolvedCommit`).
Regression ids: Q07. Rt11 now returns a commit object distinct from the tree document. Rt12 and the round-2 catalogue journey expect the pinned commit when the commit API does not return a commit object. A13 still uses a 40-hex tree URL and does not call the commit API.
Executed environment: E-161 on Linux. No live GitHub fetch.
Positive and adverse result: a document whose `tree` is an array is not a commit SHA. Content URLs use the resolved commit, or `PinnedCommit` when resolution fails.
Remaining limitation: `config/source-manifest.json` still pins `20c38289c29e4dba6b8f01ddd3273ec9ec169b46` as the tree URL. The pin is the fallback, not yet only the bootstrap. No live HEAD smoke.

### R4-04

Finding: refresh fairness must survive a new coordinator.
Status: IN_PROGRESS.
Links: F02, F25, R3-13.
Fix commit and production paths: `d8e7827`, `Refresh/SourceLedger.cs` (`RefreshCursor`), `Refresh/CatalogueCoordinator.cs`.
Regression ids: Q05.
Executed environment: E-161 on Linux.
Positive and adverse result: three cycles of nine tiny feeds on a new `CatalogueCoordinator` with the same ledger fetch `/audit/item-8`.
Remaining limitation: the UI still constructs coordinators per refresh and was not executed. Each started item still reserves the full artifact cap. That is rotation, not measured bytes.

### R4-05

Finding: a valid 304 must advance scheduler freshness only with committed content.
Status: IN_PROGRESS.
Links: F16, F22, F25, F28, R3-14.
Fix commit and production paths: `d8e7827`, `Refresh/CatalogueCoordinator.cs`, `Refresh/SourceLedger.cs`.
Regression ids: Q06. Rt13 still refuses to advance an etag when persistence throws.
Executed environment: E-161 on Linux.
Positive and adverse result: a 304 with a remembered etag, content hash, and `LastSuccessUtc` advances that timestamp. A 304 without a usable snapshot does not invent content.
Remaining limitation: last attempt, last content change, and retry-after are not separate durable fields. The catalogue and the ledger are not one crash-safe transaction.

### R4-06

Finding: missing probe proof must not publish health.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: F07, F14, F15, R3-04.
Fix commit and production paths: `d8e7827`, `Probe/ProbeCoordinator.cs` (`ProofAccepts`).
Regression ids: Q01 both cases, Q23. Existing success doubles now pass digest, absolute target, and a worker id. One future-timestamp admission double stays without proof so an unproven success cannot refresh it.
Executed environment: E-161 on Linux.
Positive and adverse result: null digest, null target, or null worker does not publish Eligible. A matching proof still publishes `Succeeded == 2`.
Remaining limitation: no replayed-worker case and no path isolation under a real tunnel.

### R4-07

Finding: an attempt deadline is not user cancellation.
Status: IN_PROGRESS.
Links: F14, F15, F28, R3-05.
Fix commit and production paths: `d8e7827`, `Probe/ProbeCoordinator.cs`.
Regression ids: Q03.
Executed environment: E-161 on Linux.
Positive and adverse result: `ProbeClass.Canceled` while the caller token is not canceled records `Failed` and continues. Caller cancellation still publishes no failure.
Remaining limitation: missing-core versus bad-candidate versus uplink is not a complete taxonomy.

### R4-08

Finding: denied nodes must not be dialed, and an owner cert opt-in must be reconsidered.
Status: IN_PROGRESS.
Links: F15, F19, F26, R3-06.
Fix commit and production paths: `d8e7827`, `Probe/ProbeCoordinator.cs` (`AdmitIfStaleAsync`, `NeedsProbe`), `Application/Catalogue.cs` (certificate policy on the settings setter).
Regression ids: Q02 three cases, Q04, Q15.
Executed environment: E-161 on Linux.
Positive and adverse result: excluded, disabled-family, and strict-country nodes dial zero times. A future `RetryAfterUtc` dials zero times. Setting `AllowInsecureCertificates` clears `CERT_VERIFICATION_DISABLED` on skip-cert nodes so `NeedsProbe` becomes true. `NeedsProbe` still does not take settings.
Remaining limitation: revocation at in-flight completion and target-certificate trust are not the same switch. No live dial.

### R4-09

Finding: admission scheduling is only partly paced.
Status: IN_PROGRESS.
Links: F02, F14, F15, F27, F28, R3-05.
Fix commit and production paths: `d8e7827`, `Probe/ProbeCoordinator.cs` (retry-after before the policy early-out).
Regression ids: Q04.
Executed environment: E-161 on Linux.
Positive and adverse result: a future retry time suppresses the probe.
Remaining limitation: no durable fair admission queue, no separate maintenance cycle, no multi-target isolation under a tunnel.

### R4-10

Finding: a contradictory authenticated 204 is not success.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: F15, R3-03.
Fix commit and production paths: `d8e7827`, `Probe/NonTunCoreProbeTransport.cs` (header framing).
Regression ids: Q24 three cases, Q25, Q26.
Executed environment: E-161 on Linux. Local TLS peer only. TLS was not disabled.
Positive and adverse result: `Content-Length: 3` plus a body, chunked transfer, and `Content-Length :` with a space before the colon return a non-null failure. An empty 204 stays valid. An untrusted certificate stays unauthenticated.
Remaining limitation: no fragmented-header or delayed-body corpus beyond those three responses. Not a Windows packet capture.

### R4-11

Finding: malformed documents must not escape the importer.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: F16, F17, R3-24.
Fix commit and production paths: `d8e7827`, `Import/SubscriptionImporter.cs`. An unpaired `\u` escape in a JSON name becomes `FormatException` inside `JsonSafety`. `Import` also contains `ArgumentException`, `DecoderFallbackException`, and `InvalidOperationException` from untrusted input.
Regression ids: Q27, 528 mutations.
Executed environment: E-161 on Linux.
Positive and adverse result: the corpus returns a typed batch. No exception escapes. Well-formed fixtures in the same suite still import.
Remaining limitation: the corpus is direct strings, not bytes delivered over HTTP. No property fuzz beyond it.

### R4-12

Finding: conflicting certificate-policy keys must not both apply.
Status: IN_PROGRESS.
Links: F17, F19, F20, F21, R3-24.
Fix commit and production paths: `d8e7827`, `Import/ClashProxyParser.cs` (ordinal ignore-case dictionary).
Regression ids: Q14.
Executed environment: E-161 on Linux.
Positive and adverse result: `skip-cert-verify` and `Skip-Cert-Verify` in one mapping yield Pending 0.
Remaining limitation: nested aliases and unknown nested fields are not rejected by a field whitelist.

### R4-13

Finding: one Xray endpoint and a VLESS WebSocket path must survive import. Omitted VMess `alterId` must be explicit on the wire.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: F17, F18, F19, F20, F21, R3-24.
Fix commit and production paths: `d8e7827`, `Import/XrayOutboundParser.cs`, `Core/MihomoProfileGenerator.cs`.
Regression ids: Q12, Q13.
Executed environment: E-161 on Linux. Native `mihomo -t` was not re-run in this slice. The earlier audit run is not cited as this commit's evidence.
Positive and adverse result: one `settings.servers` record imports host `203.0.113.44`. `wsSettings.path` `/opaque-path` is kept. VMess generation emits `alterId: 0` only when `AlterId` is null.
Remaining limitation: no controlled handshake matrix and no fresh native `-t` run on this commit.

### R4-14

Finding: cancellation and a start fault must not leave Connecting or a late commit.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: F07, F09, R3-07, R3-17, R3-19.
Fix commit and production paths: `d8e7827`, `Broker/BrokerEngine.cs`.
Regression ids: Q16, Q21, Rt19.
Executed environment: E-161 on Linux. State doubles only.
Positive and adverse result: cancel during a gated Start is not Ok and leaves the owned-core set empty. Cleanup uses `CancellationToken.None`. `IOException` from Start leaves a phase other than Connecting. A late first Start does not clear a newer session's `CoreRunning`.
Remaining limitation: no stalled cleanup against a second real client, and no Windows process.

### R4-15

Finding: confirmation rejection must keep the cleanup identity.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: F07, F09, R3-17.
Fix commit and production paths: `d8e7827`, `Broker/BrokerEngine.cs` (`_ownedOperationId`, `_ownedGeneration`).
Regression ids: Q17.
Executed environment: E-161 on Linux.
Positive and adverse result: after Start, a LanAccess change rejects confirmation, and Disconnect stops the original `generation:operationId`.
Remaining limitation: the core is a test double. This is not packet-leak evidence.

### R4-16

Finding: failover must recheck the candidate after Start returns.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: F07, F08, F09, F14, F28, R3-18.
Fix commit and production paths: `d8e7827`, `Broker/BrokerEngine.cs`.
Regression ids: Q18, epoch and excluded.
Executed environment: E-161 on Linux.
Positive and adverse result: an epoch change or a standby exclusion during the gated switch does not set `ActiveNodeId` to that standby. Automatic eligibility stays `PreConnect`.
Remaining limitation: health reports are not bound to a full boot, lease, and process identity. No live core.

### R4-17

Finding: a manual exclusion override must survive the post-Start check.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: F09, R3-17.
Fix commit and production paths: `d8e7827`, `Broker/BrokerEngine.cs` (`SelectionHeld` uses the connect's `SelectionPurpose`).
Regression ids: Q19.
Executed environment: E-161 on Linux.
Positive and adverse result: Connect of a manually selected excluded node is Ok. Failover still rejects an excluded standby because that path stays `PreConnect`.
Remaining limitation: hard restrictions other than exclusion were not given a new manual-bypass test in this slice. Automatic selection still cannot choose an excluded node through `IsCurrentlyEligible` with `PreConnect`.

### R4-18

Finding: an evicted mutation id must not run again in this process.
Status: IN_PROGRESS.
Links: F04, F06, R3-15.
Fix commit and production paths: `d8e7827`, `Application/IpcDispatcher.cs` (process-lifetime bloom, 32768 uints, 8 probes).
Regression ids: Q09. A03 still accepts 300 new mutation ids.
Executed environment: E-161 on Linux.
Positive and adverse result: after the response cache, the tombstone ring, and five further ids, replay of the first id does not execute the handler. A never-seen id still runs. `ResetOwner` clears the bloom.
Remaining limitation: the filter is probabilistic. At about 100000 ids the false-positive rate is about 0.6 percent. It is not infinite exact history, and it does not survive a process restart.

### R4-19

Finding: a delayed previous boot must not replace the new session.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: F03, F06, F34, R3-20.
Fix commit and production paths: `d8e7827`, `Application/UiSession.cs` (retired boot ids).
Regression ids: Q10.
Executed environment: E-161 on Linux.
Positive and adverse result: boot A sequence 100, boot B sequence 1, then boot A sequence 101 leaves `BootId` on B and phase Disconnected.
Remaining limitation: no Windows UI reconnect, cancel, or tray Exit.

### R4-20

Finding: slow guard and core I/O can still hold safety commands.
Status: OPEN.
Links: F04, F06, F07, F08, F09, R3-07, R3-16.
Fix commit and production paths: none in this slice.
Regression ids: none.
Executed environment: E-161 does not stall Arm or Stop.
Remaining limitation: no operation deadline around guard and recovery I/O, and no Windows pipe client.

### R4-21

Finding: opening a missing journal must not report a clean recovery.
Status: IMPLEMENTED_NOT_VALIDATED.
Links: F09, F10, F11, R3-21.
Fix commit and production paths: `d8e7827`, `Persistence/EffectJournal.cs`.
Regression ids: Q08.
Executed environment: E-161 on Linux. No host firewall or route change.
Positive and adverse result: deleting a previously opened journal makes `RequiresReconciliation` true, and the next Open writes the unknown marker before creating a replacement, so Recover does not return Completed. A first run with no residue does not write the marker.
Remaining limitation: owned OS objects are not reconciled. The marker is not a Windows service restart.

### R4-22

Finding: two catalogue writers and Windows file lifetime.
Status: OPEN.
Links: F21, F23, R3-22.
Fix commit and production paths: none in this slice.
Regression ids: none new.
Executed environment: E-161 on Linux.
Remaining limitation: no second process, no Windows delete-while-open proof.

### R4-23

Finding: one assessment still rewrites the whole catalogue.
Status: OPEN.
Links: F23, F24, R3-23.
Fix commit and production paths: none in this slice.
Regression ids: none. The audit's 10k measurement was not re-run.
Executed environment: none for this finding.
Remaining limitation: indexed single-node updates are not implemented.

### R4-24

Finding: retention is not enforced by the production workflow.
Status: OPEN.
Links: F22, F24, F25, R3-23.
Fix commit and production paths: none in this slice.
Regression ids: none.
Executed environment: none for this finding.
Remaining limitation: catalogue growth, source history, and favorite overflow are not closed.

### R4-25

Finding: DNS, IPv6, and resolved-destination safety have no packet proof.
Status: OPEN.
Links: F10, F12, F13, F22, R3-25.
Fix commit and production paths: none in this slice. Parser fixtures may still use `203.0.113.0/24` and `198.51.100.0/24`. Those ranges were not bound.
Regression ids: none.
Executed environment: none. This host's network was not changed.
Remaining limitation: no resolver rebinding test and no Windows packet capture.

### R4-26

Finding: the output drain stays bounded in memory and unbounded in the count.
Status: IN_PROGRESS.
Links: F07, F29, R3-10, R3-11.
Fix commit and production paths: `d8e7827` does not change the drain. The retained tail cap remains 2000 characters. `tests/AutoVpn.UnitTests/Round2SliceATests.cs` no longer caps `OutputBytes` at 1 MiB.
Regression ids: Rt03.
Executed environment: E-161 on Linux.
Positive and adverse result: a chatty child drains at least 10000 bytes and the retained tail length stays in 0..2000. Stdout is counted. The retained tail is the stderr buffer.
Remaining limitation: asset hash, job handles, orphan recovery, and secret redaction of that tail are not closed.

### R4-27

Finding: byte budgets are not a complete traffic account.
Status: OPEN.
Links: F15, R3-08, R3-09.
Fix commit and production paths: none in this slice.
Regression ids: existing budget tests still pass inside E-161. They are not a new traffic account.
Executed environment: E-161 on Linux.
Remaining limitation: failed, canceled, and on-demand work are not metered as network bytes. Speed is not bound to an owned path.

### R4-28

Finding: settings enums and standby sets must be closed and bounded.
Status: IN_PROGRESS.
Links: F19, F26, F27, F28, R3-26.
Fix commit and production paths: `d8e7827`, `Domain/Settings.cs` (`Validate`), `Broker/BrokerEngine.cs` (`ApplyRuntimeSet`).
Regression ids: Q11, Q20.
Executed environment: E-161 on Linux.
Positive and adverse result: `CountryMode`, `SelectionMode`, and `Theme` cast to 999 make `Validate` return non-null. One hundred copies of one standby produce a `StandbyCount` inside 0..`WarmStandbys` (5), and duplicate node ids are skipped.
Remaining limitation: country codes, preferred versus strict ranking, and measured sample history are not finished.

### R4-29

Finding: the desktop is not a functional catalogue.
Status: OPEN.
Links: F03, F34, R3-20, R3-27.
Fix commit and production paths: none in this slice.
Regression ids: none.
Executed environment: WPF was not started.
Remaining limitation: no clicked catalogue, tray, or theme pass.

### R4-30

Finding: the obsolete output cap is corrected. Windows CI is not permanent.
Status: IN_PROGRESS.
Links: F04, F31, R3-10, R3-28.
Fix commit and production paths: `d8e7827`, `tests/AutoVpn.UnitTests/Round2SliceATests.cs`. Audit workflows were not imported.
Regression ids: Rt03, and the ported Q suite.
Executed environment: E-red then E-161 on Linux. No GitHub Actions run was started for this commit.
Positive and adverse result: the Linux unit suite is 161/161. The previous 1 MiB ceiling is gone. The tail cap remains.
Remaining limitation: Windows file-lifetime and `pkill` fixtures were not ported. `docs/evidence/build-manifest.json` still describes an older package and is not evidence for this commit.

### R4-31

Finding: there is no current installer, SBOM, or signed artifact.
Status: OPEN.
Links: F01, F30, F32, F33, R3-29.
Fix commit and production paths: none. SPDX `-only` and `-or-later` were not inferred.
Regression ids: none.
Executed environment: none.
Remaining limitation: no installer, no SBOM, no Authenticode. Unsigned delivery is not produced yet.

### R4-32

Finding: the original journeys are not closed.
Status: OPEN.
Links: F31, F33, F34, R3-30.
Fix commit and production paths: this status file plus `d8e7827`.
Regression ids: Q01–Q27 are necessary and not sufficient.
Executed environment: E-161 on Linux.
Remaining limitation: v1 is not accepted. Windows 11 admin acceptance, packet capture, and the installer are still ahead of the Linux work that can still be done.
