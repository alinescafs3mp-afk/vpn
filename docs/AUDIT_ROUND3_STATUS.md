# Round-3 audit status

V1 is not accepted. This file records work after `for_fix/ASTRA_HARD_AUDIT_ROUND3_2026-10-03.md`. It does not replace that directive, `for_fix/ASTRA_HARD_AUDIT_ROUND2_2026-10-03.md`, `for_fix/ASTRA_HARD_AUDIT_FIX_DIRECTIVE_2026-10-02.md`, or `docs/IMPLEMENTATION_DIRECTIVE.md`.

Audited head was `e7797c7d24e4f20befb7e43bfb8e72d5efd436f1`. The round-3 directive arrived in `fcfe09428d6d18c3d0e39f7fc134f73f287dad20`. The implementation head recorded here is `c76764f69e9be4637abb670dd39a17cded7fd81f`.

Each finding uses the required fields: status, fix commit and production paths, regression ids and test names, executed command and platform, run evidence, positive and adverse result, remaining limitation. A Linux unit result is not a Windows install, a packet capture, or a clicked window. No row below is VERIFIED for the whole product.

## Evidence used below

| Run | Command and environment | Result |
|---|---|---|
| E-124 | `AUTOVPN_MIHOMO_PATH=/var/tmp/autovpn-mihomo/mihomo dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --nologo --no-restore` from `/home/jericho/src/vpn` on Linux. SDK 10.0.112. Pinned Mihomo `v1.19.32` commit `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`, SHA-256 `3122d100e8177501776109f1a6253a694611627cf4d7c7ec82705855cf8626a8`. The binary is local and not in git. | Working tree committed as `add61e1`: 124 passed, 0 failed, 0 skipped, 7 s. Includes `FullyQualifiedName~IndependentRound3` (30). The Mihomo fact ran. |
| E-127 | Same command and environment. | Working tree committed as `e050f7c`: 127 passed, 0 failed, 0 skipped, 8 s. |
| E-128 | Same command and environment. | Closing run on the tree committed as `c76764f`: 128 passed, 0 failed, 0 skipped, 8 s. One earlier run of `Rt02CurrentHeadRefreshPublishesOnlyTheAuthenticatedCandidate` returned `Succeeded` 0 and passed on the immediate rerun. The closing full run includes that test. |
| E-build | `dotnet build` Release for `AutoVpn.Service`, `AutoVpn.Recovery`, and `AutoVpn.Desktop` (`net10.0-windows` via EnableWindowsTargeting) on `c76764f`. | 0 warnings, 0 errors. No service process, no WPF process, no TUN, no host route or DNS change. |

## R3 findings

### R3-01

Finding: Windows product composition is not installed.
Status: BLOCKED.
Fix commit and production paths: none. The service still constructs `RefusingCoreController` and `UnavailableNetworkGuard`.
Regression ids and test names: none added. Auditor WPF smoke run 37075649029 is not a connected journey and was not re-run here.
Executed command, platform, SDK/core/driver: E-build on Linux. No Windows 11 machine.
Run/job/artifact and checksums: service assembly `src/AutoVpn.Service/bin/Release/net10.0/AutoVpn.Service.dll` from the local build. Not an installer.
Positive and adverse result: the service project compiles. It does not arm a guard or start a TUN core.
Remaining limitation and external prerequisite: an isolated Windows 11 host with an administrator account, a real service identity, and a non-destructive install. Do not change this Linux host's network.

### R3-02

Finding: Windows peer and process ownership.
Status: IN_PROGRESS.
Fix commit and production paths: `add61e1`, `Probe/NonTunCoreProbeTransport.cs` (`WindowsOwnsLoopbackPort`, `GetExtendedTcpTable`).
Regression ids and test names: A14. Linux uses `/proc/net/tcp`. The Windows branch was not executed.
Executed command, platform, SDK/core/driver: E-124 on Linux.
Run/job/artifact and checksums: unit suite only.
Positive and adverse result: A14 passed on Linux. An open port that the worker does not own is not treated as owned on that path.
Remaining limitation and external prerequisite: run the same ownership check on Windows. Two-account pipe identity remains open. F05 stays BLOCKED.

### R3-03

Finding: a TLS handshake is not a valid probe response.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit and production paths: `add61e1`, `Probe/NonTunCoreProbeTransport.cs`.
Regression ids and test names: T01, T02, existing RT01.
Executed command, platform, SDK/core/driver: E-128, Linux, SDK 10.0.112, pinned Mihomo as in E-124.
Run/job/artifact and checksums: unit suite. No packet capture.
Positive and adverse result: success requires an authenticated HTTP/1.0 or HTTP/1.1 status 204, no redirect, no HTML, and a body of at most 8192 bytes. `NOT-HTTP 204`, HTML, a redirect, and a contradictory length do not publish success.
Remaining limitation and external prerequisite: not a Windows TUN session. One Rt02 run failed before the closing 128/128 run.

### R3-04

Finding: admission evidence must match the candidate and target.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit and production paths: `add61e1`, `Probe/ProbeCoordinator.cs` (`ProofAccepts`).
Regression ids and test names: A15. RT28 uses the canonical digest so the allowed skip path can still publish.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: a non-null digest or target that does not match the node is not published. Null proof fields still publish for existing scripted doubles.
Remaining limitation and external prerequisite: scripted doubles with null proof are not a captured worker identity.

### R3-05

Finding: one unsupported node must not abort the queue.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit and production paths: `add61e1`, `Probe/ProbeCoordinator.cs`.
Regression ids and test names: A07.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: unsupported applies Failed and the queue continues. CoreFailure still stops the cycle.
Remaining limitation and external prerequisite: not a multi-process restart of a live broker.

### R3-06

Finding: a failed on-demand admission must invalidate obsolete success.
Status: IN_PROGRESS.
Fix commit and production paths: `add61e1`, `Probe/ProbeCoordinator.cs` (`AdmitIfStaleAsync`).
Regression ids and test names: A06.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: a real candidate failure writes Failed and keeps the old success time, so the newer failure wins. Cancel, uplink, environment, and core failure do not invent a failure.
Remaining limitation and external prerequisite: the clock and network-epoch matrix outside A06 was not run.

### R3-07

Finding: cancellation must win before publication and across the local operation.
Status: IN_PROGRESS.
Fix commit and production paths: `add61e1` (`ProbeCoordinator`), `c76764f` (`MainWindow.ConnectAsync`).
Regression ids and test names: coordinator cancel before publish; `StaleConnectCannotClearANewerUiOperation`.
Executed command, platform, SDK/core/driver: E-128 on Linux. Desktop was compiled, not launched.
Run/job/artifact and checksums: unit suite and E-build.
Positive and adverse result: a cancelled probe is not published and is not charged. The UI token is passed into admission. The window itself was not clicked.
Remaining limitation and external prerequisite: a WPF run that holds admission and presses Disconnect. See R3-20.

### R3-08

Finding: daily traffic accounting must not overflow or refund.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit and production paths: `add61e1`, `Probe/ProbeByteBudget.cs`.
Regression ids and test names: A08, A09, `Rt06`.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: a corrupt, negative, or unreadable file loads exhausted. A charge for an older day is ignored. Addition saturates at `long.MaxValue`. A missing file or a new UTC day starts at 0.
Remaining limitation and external prerequisite: concurrent workers and a crash between reservation and charge were not tested.

### R3-09

Finding: throughput must belong to the current path.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit and production paths: `add61e1`, `Refresh/CatalogueCoordinator.cs` (`SpeedStillBound`).
Regression ids and test names: A07.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: a null binding, a cancelled caller, an epoch or digest mismatch, an excluded node, or health that is not Healthy or Degraded returns null and does not call `ApplyAssessment`.
Remaining limitation and external prerequisite: no real byte transfer was measured.

### R3-10

Finding: an output cap must not stop draining a child pipe.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit and production paths: `add61e1`, `Probe/NonTunCoreProbeTransport.cs` (`CountAsync`).
Regression ids and test names: A16.
Executed command, platform, SDK/core/driver: E-128 on Linux. Windows child fixture is still absent, matching the audit exclusion.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: stdout and stderr are drained to EOF. The retained tail stays capped at 2000 characters. The total saturates at `int.MaxValue`.
Remaining limitation and external prerequisite: an equivalent Windows child process was not run.

### R3-11

Finding: diagnostics still exposed JSON secrets.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit and production paths: `add61e1`, `Domain/TextPolicy.cs` (`SecretRedactor`).
Regression ids and test names: A11.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite. The canary is the synthetic string `AUDIT_SYNTHETIC_SECRET_73`.
Positive and adverse result: quoted `password` and `secret` values are redacted before the older field pattern.
Remaining limitation and external prerequisite: not every export, log, and IPC surface was scanned.

### R3-12

Finding: a tree object SHA is not a commit ref.
Status: IN_PROGRESS.
Fix commit and production paths: `add61e1`, `Refresh/CatalogueCoordinator.cs` (`CommitRef`).
Regression ids and test names: A13. RT11 and RT12 still pass for a branch URL.
Executed command, platform, SDK/core/driver: E-128 on Linux. No live GitHub fetch.
Run/job/artifact and checksums: unit suite. No credential was stored.
Positive and adverse result: a 40-hex last segment of the tree API is kept as the raw ref and is not replaced by the JSON tree SHA. A branch URL such as `.../trees/main` still uses the response SHA.
Remaining limitation and external prerequisite: commit and tree are not stored as separate objects, and a branch head is not resolved through the GitHub commits API. Incomplete discovery still must not prune.

### R3-13

Finding: the first eight feeds starved the rest.
Status: IN_PROGRESS.
Fix commit and production paths: `add61e1`, `Refresh/CatalogueCoordinator.cs` (`_refreshCursor`).
Regression ids and test names: A12. RT16 still expects eight fetches and `:CYCLE_BUDGET` in one cycle.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: the next `RefreshAsync` on the same coordinator starts after the items reserved in the previous call, so nine tiny feeds reach the ninth item on the second call. One cycle still reserves 8 MiB per started item.
Remaining limitation and external prerequisite: this is rotation, not actual-byte accounting. A large first feed can still fill one cycle. A new coordinator starts at 0.

### R3-14

Finding: source transactions, 304 freshness, and distinct schedule timestamps.
Status: IN_PROGRESS.
Fix commit and production paths: no new transaction in this round. Prior refresh fence, 304 discovery cache, and ledger remain.
Regression ids and test names: prior RT11. No new crash-between-writes test.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: preserved 304 and fence behavior still passes inside the suite. Success and failure still share `LastSuccessUtc` on `SourceLedger.Remember`.
Remaining limitation and external prerequisite: durable per-artifact snapshot, rejected version, next retry, and a crash between catalogue and ledger writes are open.

### R3-15

Finding: normal mutations died at 256 commands.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit and production paths: `add61e1`, `Application/IpcDispatcher.cs`, `Domain/ProductLimits.cs` (`IpcRetiredEntries` 8192), `Domain/ReasonCodes.cs` (`REPLAY_EXPIRED`).
Regression ids and test names: A03. Updated idempotency section in `Round2SliceATests`. A02 still passes.
Executed command, platform, SDK/core/driver: E-128 on Linux. No process restart.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: 300 `ReportHealth` calls return Ok. A replay inside the 256 window returns the stored response. A conflicting fingerprint returns `REQUEST_CONFLICT`. After the oldest id is tombstoned, its replay returns `REPLAY_EXPIRED` and does not run the handler.
Remaining limitation and external prerequisite: an id that ages out of the 8192 tombstones can run again. The lease is not persisted across restart. In-flight cap remains 64 `BUSY`.

### R3-16

Finding: safety control under long authenticated operations.
Status: IN_PROGRESS.
Fix commit and production paths: no new liveness path this round. Round-2 pipe instance cap remains.
Regression ids and test names: A02. No new held-Start pipe test.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: reads and Disconnect stay off the mutation window.
Remaining limitation and external prerequisite: several held Starts on a real Windows pipe were not run. The handler still waits on the engine call.

### R3-17

Finding: revalidate policy and eligibility at Start and at production confirmation.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit and production paths: `add61e1`, `Broker/BrokerEngine.cs` (`SelectionHeld`, `ConfirmProduction`, `_commitPolicy`).
Regression ids and test names: B01, B02, B03.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: a changed stamp or an ineligible selection after Start is `POLICY_CHANGED` and does not stay Ok. Confirmation applies `VerifyFailed` and does not reach `Connected` when the stamp, exclusion, or insecure-certificate consent no longer matches.
Remaining limitation and external prerequisite: not every settings dimension was held on a Windows service. B02 uses automatic selection.

### R3-18

Finding: CoreExit must leave Connected before replacement readiness.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit and production paths: `add61e1`, `Broker/BrokerEngine.cs`, `Domain/TunnelReducer.cs`.
Regression ids and test names: B04, B05. The old lifecycle expectation that Connected survives a policy-changed dead-core switch was updated.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: CoreExit applies `CoreExited` and clears `CoreRunning` before replacement `Start`. A policy change during that switch does not restore Connected. RT21 still reaches Connecting and then Connected on a completed unheld switch.
Remaining limitation and external prerequisite: switch budget, pinned mode, and offline uplink were not each held as separate Windows runs.

### R3-19

Finding: a cancelled Connect must not Arm, and an explicit unprotected mode must not pretend to be armed.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit and production paths: `add61e1`, `Broker/BrokerEngine.cs`, `Domain/TunnelReducer.cs` (`UnprotectedAccepted`).
Regression ids and test names: B06, B07. RT28 still requires protection when settings demand it.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: a token that is already cancelled throws before Arm. Explicit unprotected mode is payload `protectionRequired` false and settings `ProtectionOnConnect` false. A protected arm that returns `Armed` false is still refused.
Remaining limitation and external prerequisite: there is no installed Windows guard. This is not an unprotected production mode.

### R3-20

Finding: the UI must cancel its own work.
Status: IN_PROGRESS.
Fix commit and production paths: `c76764f`, `Application/UiSession.cs` (`UiOperationLease`), `Desktop/MainWindow.xaml.cs`.
Regression ids and test names: `StaleConnectCannotClearANewerUiOperation`.
Executed command, platform, SDK/core/driver: E-128 on Linux. E-build compiled the WPF project. The window was not started.
Run/job/artifact and checksums: unit suite and `src/AutoVpn.Desktop/bin/Release/net10.0-windows/AutoVpn.Desktop.dll`.
Positive and adverse result: an older generation cannot clear a newer pending flag or remain the owner. Disconnect and Exit supersede the generation before the safety request. Admission receives that token. Exit waits up to three seconds and does not dispose the catalogue when the join times out.
Remaining limitation and external prerequisite: no clicked Disconnect, Exit, tray, or sleep run. A late IPC request already on the wire is not unsent; its response is ignored when the generation no longer owns it.

### R3-21

Finding: journal quarantine held a read handle, and a missing file could look clean.
Status: IMPLEMENTED_NOT_VALIDATED.
Fix commit and production paths: `e050f7c`, `Persistence/EffectJournal.cs`, `Recovery/Program.cs`.
Regression ids and test names: `Rt22CorruptJournalStaysRecoveryUnknownAfterRestart`, `DuplicateRemovalIdsDoNotCompleteJournalRecovery`, `MissingJournalAfterOpenRequiresReconciliation`, `QuarantineResidueWithoutAJournalRequiresReconciliation`, `UnsupportedJournalSchemaIsLeftUntouched`.
Executed command, platform, SDK/core/driver: E-127 and E-128 on Linux. The recovery project builds. Windows RT22 was not re-run.
Run/job/artifact and checksums: unit suite. Recovery assembly `src/AutoVpn.Recovery/bin/Release/net10.0/autovpn-recovery.dll`.
Positive and adverse result: the header and SQLite probe are closed before the rename. `.recovery-unknown` is written before the move. A repeated removal id does not complete recovery. After the journal has been opened, deleting the file requires reconciliation. A quarantine file or sidecar without the main file does too. A path that was never opened and has no residue still exits 0. An unsupported schema is left untouched.
Remaining limitation and external prerequisite: Windows sharing violation was not executed. The guard does not reconcile real OS routes or filters. Do not reset this host's network.

### R3-22

Finding: catalogue ownership and Windows file lifetime.
Status: IN_PROGRESS.
Fix commit and production paths: no new owner handoff this round. Round-2 serializable conflict and in-place update remain.
Regression ids and test names: prior RT09 and RT29 still inside E-128.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: preserved conflict and single-assessment protector behavior still pass.
Remaining limitation and external prerequisite: two processes, DPAPI scope, and Windows handle cleanup after fixture shutdown were not run.

### R3-23

Finding: 10,000-node cost and retention.
Status: OPEN.
Fix commit and production paths: none this round.
Regression ids and test names: none.
Executed command, platform, SDK/core/driver: not run.
Run/job/artifact and checksums: none.
Positive and adverse result: not measured.
Remaining limitation and external prerequisite: a bounded 10,000-node import, assessment, probe, and eviction measurement. Do not delete favorites or the active node to make a count pass.

### R3-24

Finding: parser containment and semantic round trips.
Status: IN_PROGRESS.
Fix commit and production paths: `add61e1`, `Domain/TextPolicy.cs` (unpaired surrogate), `Fetch/GithubTreeParser.cs` (symlink mode). Preserved canonicalizer version 3.
Regression ids and test names: A18, A01, A17, A19.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: `\ud800A` does not throw. Symlink mode `120000` is not subscription data. Normalized TLS is still emitted. Unknown security is still rejected.
Remaining limitation and external prerequisite: nested records and credential inheritance beyond A18 are open. Upstream Mihomo text stays GNU GPL version 3. No SPDX `-only` or `-or-later` suffix was added.

### R3-25

Finding: destination, DNS, and IPv6 dial boundary.
Status: IN_PROGRESS.
Fix commit and production paths: `add61e1`, `Domain/NodeSemantics.cs` (`198.19.0.0/16` beside `198.18.0.0/16`).
Regression ids and test names: A10, A17.
Executed command, platform, SDK/core/driver: E-128 on Linux. No packet capture. This host's routes were not changed.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: `198.19.0.0/16` is rejected as a node host. A symlink blob is not subscription data. Documentation ranges `198.51.100.0/24` and `203.0.113.0/24` stay allowed for parser fixtures and are not bound.
Remaining limitation and external prerequisite: no DNS, IPv6, or TUN dial proof on Windows 11.

### R3-26

Finding: country, source, ranking, and standby policy.
Status: IN_PROGRESS.
Fix commit and production paths: none this round.
Regression ids and test names: prior country and ranking tests still inside E-128. No new adverse case.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite.
Positive and adverse result: preserved behavior still passes. Agreement across UI, broker, and ranking was not newly proven.
Remaining limitation and external prerequisite: a single policy source for country and standby is still open.

### R3-27

Finding: populated product UI.
Status: OPEN.
Fix commit and production paths: `c76764f` changes cancellation only. It does not add the missing UI matrix.
Regression ids and test names: none clicked.
Executed command, platform, SDK/core/driver: E-build compiled the project on Linux.
Run/job/artifact and checksums: desktop assembly from E-build. Auditor smoke run 37075649029 was Windows Server 2025, not Windows 11, and was not repeated.
Positive and adverse result: the project compiles. No view was shown.
Remaining limitation and external prerequisite: Windows UI run of populated, error, tray, DPI, and keyboard states.

### R3-28

Finding: permanent non-destructive platform gate.
Status: OPEN.
Fix commit and production paths: none. Audit workflows were not merged.
Regression ids and test names: none in CI this round.
Executed command, platform, SDK/core/driver: local Linux unit suite only.
Run/job/artifact and checksums: no new GitHub Actions run was created for this fix.
Positive and adverse result: local Linux tests passed. Windows CI was not added.
Remaining limitation and external prerequisite: a non-destructive workflow for both platforms that does not start TUN or change the runner's routes.

### R3-29

Finding: current retrievable installer and provenance.
Status: BLOCKED.
Fix commit and production paths: none. Round-2 GPL version 3 notice remains, without an SPDX suffix.
Regression ids and test names: none.
Executed command, platform, SDK/core/driver: not packaged.
Run/job/artifact and checksums: `docs/evidence/build-manifest.json` is stale and is not cited as current. No local archive is cited.
Positive and adverse result: no installer, SBOM, or Authenticode signature was produced. A signature was not invented.
Remaining limitation and external prerequisite: a Windows packaging host and a real certificate. An unsigned test artifact may be labeled later; it does not exist yet.

### R3-30

Finding: full declared journey matrix.
Status: OPEN.
Fix commit and production paths: none as a journey.
Regression ids and test names: the independent pack and the existing suite, not the matrix in directive section 7.
Executed command, platform, SDK/core/driver: E-128 on Linux.
Run/job/artifact and checksums: unit suite only.
Positive and adverse result: the counterexamples that were ported pass on Linux. The install, TUN, sleep, and two-account journey was not run.
Remaining limitation and external prerequisite: the section 7 gates below.

## Original findings F01–F34

Round-2 positives stay in `docs/AUDIT_ROUND2_STATUS.md`. They are not restated as VERIFIED when a round-3 regression is still open. Statuses here are the round-3 disposition.

### F01

Status: BLOCKED. R3-01, R3-02, R3-29. Production Windows composition is absent. E-build compiles the service and does not install it.

### F02

Status: IN_PROGRESS. R3-03 through R3-07 and R3-12 through R3-14. Linux probe and refresh counterexamples improved. The full refresh and handoff are open. E-128.

### F03

Status: IN_PROGRESS. R3-19, R3-20, R3-27. Cancelled connect and explicit unprotected mode have unit coverage. The window was not clicked.

### F04

Status: IN_PROGRESS. R3-15, R3-16, R3-28. The 256-command dead end is gone on the unit path. Windows liveness and a permanent CI gate are open.

### F05

Status: BLOCKED. R3-02, R3-16. The service still rejects an unverified Windows peer. That is not an implemented owner boundary.

### F06

Status: IN_PROGRESS. R3-15 through R3-20. Replay and the UI generation are improved. Restart persistence and a clicked race remain open.

### F07

Status: IN_PROGRESS. R3-02, R3-10, R3-17, R3-18. Native worker, drain, policy recheck, and CoreExit are in the Linux suite. Windows ownership is not.

### F08

Status: IMPLEMENTED_NOT_VALIDATED. R3-18. B04 and B05 passed on Linux. Not a Windows core-exit run.

### F09

Status: IN_PROGRESS. R3-19, R3-21. Cleanup and the unknown marker improved. OS reconciliation is not implemented.

### F10

Status: BLOCKED. R3-01, R3-21, R3-25. No protection or packet evidence. The journal does not change host routes.

### F11

Status: IN_PROGRESS. R3-21, R3-22. The marker survives restart on Linux. Windows quarantine was not re-executed.

### F12

Status: OPEN. R3-25. No production DNS or IPv6 path proof.

### F13

Status: OPEN. R3-25. `198.19.0.0/16` is rejected. Resolved-destination safety is not proven.

### F14

Status: IN_PROGRESS. R3-06, R3-07, R3-17. On-demand failure and a policy change at Start are covered by A06 and B01–B03. The wider matrix is open.

### F15

Status: IN_PROGRESS. R3-03 through R3-10. The pinned Mihomo positive is in E-128, with the Rt02 flake noted above. Admission and fairness are not closed.

### F16

Status: IN_PROGRESS. R3-14. Invalid snapshot preservation from round 2 still passes. The durable source transaction is open.

### F17

Status: IN_PROGRESS. R3-24. An unpaired surrogate is contained. The rest of the parser matrix is open.

### F18

Status: IN_PROGRESS. R3-24. Preserved security controls pass. A complete native handshake matrix was not added.

### F19

Status: IN_PROGRESS. R3-24. A19 still rejects unknown security. Broader mixed-case cases were not added.

### F20

Status: IN_PROGRESS. R3-24. Existing representations still pass. Nested semantics are not fully proven.

### F21

Status: IN_PROGRESS. R3-24. Canonicalizer version 3 behavior is preserved. A full historical migration was not added.

### F22

Status: IN_PROGRESS. R3-13, R3-14, R3-25. The ninth tiny feed is reached on the next cycle. Actual-byte fairness and the dial boundary are open.

### F23

Status: IN_PROGRESS. R3-22, R3-23. Round-2 copy and conflict behavior still passes. Authority and 10,000-node cost are open.

### F24

Status: IN_PROGRESS. R3-13, R3-23. Targeted updates from round 2 remain. Scale and retention were not measured.

### F25

Status: IN_PROGRESS. R3-12, R3-13, R3-14, R3-25. A 40-hex tree URL keeps its commit. Live GitHub commit resolution and the dial boundary are open.

### F26

Status: IN_PROGRESS. R3-26, R3-27. No new country or populated-UI proof.

### F27

Status: IN_PROGRESS. R3-09, R3-26. Speed samples are bound to digest and epoch. Ranking and standby agreement is open.

### F28

Status: IN_PROGRESS. R3-06, R3-08, R3-14, R3-20. Corrupt budgets stay exhausted and the UI generation cancels local work. Distinct failure timestamps are open.

### F29

Status: IN_PROGRESS. R3-02, R3-10, R3-11. Drain and JSON redaction pass on Linux. Windows process ownership was not run.

### F30

Status: BLOCKED. R3-01, R3-29. No current installer journey.

### F31

Status: OPEN. R3-28, R3-30. No new permanent Windows or native CI gate was added.

### F32

Status: BLOCKED. R3-29. The GPL version 3 notice from round 2 remains. There is no SBOM and no signature.

### F33

Status: BLOCKED. R3-29, R3-30. Source commits above are on `origin/main`. There is no current release artifact bound to them.

### F34

Status: OPEN. R3-27. The four-view smoke from the audit was not repeated. The populated UI matrix was not run.

## Section 7 acceptance gates

| Gate | Status | Why |
|---|---|---|
| Fresh user journey | OPEN | No install, consent-to-TUN, or traffic run. |
| Protocol coverage | IN_PROGRESS | Existing parser and one pinned Shadowsocks TLS probe remain. The full matrix was not added. |
| Source coverage | IN_PROGRESS | A13 and the second-cycle cursor passed. Live commit, tree, and blob identities are open. |
| Input hostility | IN_PROGRESS | A18 passed. Depth, size, and conflicting-field coverage is not complete. |
| Probe integrity | IN_PROGRESS | R3-03 through R3-09 unit cases passed. A second independent target behind a live VPN was not run. |
| State concurrency | IN_PROGRESS | B04, B05, and the UI lease passed as units. Not every await was held. |
| Privilege boundary | BLOCKED | No second Windows user and no service account. |
| Traffic protection | BLOCKED | No Windows 11 packet capture. This host's network was not changed. |
| Recovery | IN_PROGRESS | Linux journal cases passed. OS reconciliation and Windows rename were not run. |
| Environment changes | OPEN | Sleep, NIC, and captive-network cases were not run. |
| Persistence and retention | IN_PROGRESS | Round-2 catalogue cases still pass. Expiry under overflow was not measured. |
| Performance | OPEN | No 10,000-node or soak measurement. |
| Interface | OPEN | The WPF window was compiled and not shown. |
| Installation | BLOCKED | No installer, upgrade, or uninstall. |
| Supply chain and evidence | BLOCKED | No current SBOM, signature, or retrievable artifact. The Mihomo hash above is the local test binary, not a shipped archive. |

## What this round pushed

- `add61e167989a8a247a57594bbae1118ea78aa36` Linux counterexamples for the independent pack.
- `e050f7c05dcc1b997d5e02116f56241fb9b71e4a` journal handle, marker order, distinct removal ids, missing-file reconciliation.
- `c76764f69e9be4637abb670dd39a17cded7fd81f` desktop connect generation.

Before this document, `git rev-parse HEAD` and `git ls-remote origin refs/heads/main` both returned `c76764f69e9be4637abb670dd39a17cded7fd81f`. This document does not change production code. V1 is not accepted.
