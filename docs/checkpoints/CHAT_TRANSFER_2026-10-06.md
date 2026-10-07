# AutoVPN continuation after chat transfer — 6 October 2026

This is the current navigation checkpoint. Historical checkpoints remain evidence
for their own source revisions. Continue implementation from the current remote
`main`, never by restoring an old source archive over newer work.

## Owner instructions and product contract

- Communicate with the owner entirely in Russian; the default application UI is Russian.
- Work only in `main`, preserve history and useful earlier work, and never force-push.
  The remote branch inventory checked during this transfer contains only `main`.
- Work in small completed, versioned iterations, approximately forty minutes when
  practical. An iteration is not automatically a release or a cumulative version.
- The assistant owns the implementation. The later V3G instruction supersedes the
  original assignment to Grok: Grok may build/check a completed handoff, not finish
  implementation silently delegated back to him.
- Keep C# / .NET 10, WPF, the pinned official Mihomo core, SQLite and a minimal
  privileged Windows broker. The owner should not need to choose routine internals.
- Keep the existing owner data, checks, negative assertions, resource ownership,
  consent, and credential boundaries. Do not change this Linux host's networking.

The full product specification is `docs/IMPLEMENTATION_DIRECTIVE.md`, supplemented
by `AGENTS.md`, the architecture/ADRs, security model, protocol compatibility,
source coverage, Windows test plan and recovery documentation. All were recovered
as actual source files, including historical audit reports and branch archives.

Required user journey: a small attractive Windows app; automatic/manual refresh
of relevant `igareck/vpn-configs-for-russia` families; a persistent deduplicated
catalogue; locally validated selectable nodes; retention of still-working older
nodes; automatic/manual selection with country/source constraints; bounded
failover; one-click TUN; honest latency/traffic/speed information; favorites and
tray; safe disconnect and restoration; recoverable installation. Background work
requires consent and retains its existing cancellation, pacing and traffic budgets.
Third-party subscription data is input, never an executable profile supplied to
SYSTEM. Ready status and a listening port are not protected connectivity.

## Published source recovered

Transfer baseline:

- Commit: `b65fb898f752d29db350267d5e5b2e7557cf63d4`.
- Tree: `a1e7b04278c2c48036bd31c4bbb7f44a66ddf397`.
- Assembly version: `0.1.7`, V3G plus subsequent independent corrections.
- Source checkpoint run: `37468579788`, artifact `11415583706`.
- Artifact ZIP SHA-256:
  `1f7b1e316e5f73f5c838ba345201399fe606d3e07d14f7f5dbe0c36469725547`.
- All 348 source files were independently checked locally for size, SHA-256 and
  Git blob identity against the exact source manifest. ZIP CRC also passed.

The baseline includes the published V3G output reader, the runtime Stop/Start
correction (`c98da7aadb1d13ac5517fd194474075351809844`, results recorded by
`acc5f548c392230b445a784299b8654a3ef5e2e5`) and the new endpoint preparation.
Endpoint preparation validates the complete DNS answer set and binds a permitted
numeric address in an execution copy while preserving the original identity and
TLS/transport names. It does not establish live public-node reachability.

Detailed entries: `README_V3G_RU.md`, `README_RUNTIME_PREPARATION_RU.md`,
`docs/checkpoints/RUNTIME_PREPARATION.md`, `docs/checkpoints/ENDPOINT_PREPARATION.md`.

## Baseline regression is not entirely green

Run `37468579830` exercised the exact baseline source. The raw Windows artifact
`11415389090` was downloaded and its SHA-256 and source identity were verified;
all six TRX files were independently recounted locally, including unique test IDs.

| Baseline platform | Cases per run | Six-run outcome |
|---|---:|---|
| Linux | 741 | 4356 passed, 0 failed, 90 platform skips (completed CI logs) |
| Windows Server 2025 | 741 | 4415 passed, 1 failed, 30 platform skips (raw TRX recounted) |

The failure is in Windows iteration 3:
`AstraV3DTlsInvestigationTests.Native_Round5_V3D_ObserveControlledHandshake(variant: "vless")`.
The underlying positive assertion in `Round6NativeHandshakeTests` receives
`CORE_CLEANUP_REQUIRED`. The recorded process-wide TLS events do not prove their
correlation with this attempt. This is not an observed UTF-16/JSON parsing failure.

The existing installed status-service jobs on Windows Server 2025 and 2022 passed:
standard-owner Ready, outsider AccessDenied, SCM stop/restart/remove and cleanup.
These are status/control-plane tests, not selected-node handoff, SYSTEM core
execution, TUN or product-installer acceptance.

## First continuation increment: V3G cleanup diagnostics

Code commit: `9bfc43e28e6885e2a9594007edb43fc20ffb1c89`.
Code tree: `7e26dafbfadeffc4842ba367d24461f38e72f66c`.
Assembly version stays `0.1.7`; this does not integrate V3H or claim a V3I release.

The existing `ProbeCleanupReport` was lost in the transport catch boundary.
The transport now preserves its bounded summary in `LastDiagnostic`, while the
public observation reason remains exactly `CORE_CLEANUP_REQUIRED` and unsuccessful.
Terminal directory errors now reach the existing sanitizing cleanup boundary with
their actual exception category and HResult. The current directory-removal flag is
reset before each attempt. No raw exception message, child output, path or credential
is added to this diagnostic. Cleanup deadlines and admission checks are unchanged.

The existing Windows locked-file negative compares the observed native File.Delete
HResult with the cleanup report, checks that the private directory is absent from
the diagnostic and still verifies successful cleanup after releasing the lock.
No cases or platform skips were added or removed: the suite remains 741 cases.

Validation run: `37482454951`, attempt 1. All three jobs passed. Both builds
recorded zero warnings and errors. All twelve raw TRX files were downloaded and
independently recounted; each has 741 unique cases and the exact platform skips.
All 348 source blobs in each platform artifact match the reviewed code exactly.

| Diagnostics platform | Completed runs | Passed | Failed | Platform skips |
|---|---:|---:|---:|---:|
| Linux | 6 | 4356 | 0 | 90 |
| Windows Server 2025 | 6 | 4416 | 0 | 30 |

The strengthened native Windows locked-file control passed in all six runs.
The installed status-service's raw summary confirms acceptance of the standard
owner, rejection of the outsider, SCM lifecycle and complete owned-resource
cleanup. Full provenance and per-run accounting are in
`docs/evidence/CHAT_TRANSFER_VALIDATION.json`.
The local transfer host has no .NET SDK; builds and Windows execution use the
existing GitHub Actions jobs. A static review is not reported as a local build.

This increment repairs loss of diagnostic information. It does not prove the
cause of the original intermittent native cleanup failure fixed, even if the new
suite passes. Keep the original failed series and inspect any recurrence by phase:
PROCESS_STOP, OUTPUT_DRAIN, DIRECTORY_CLEANUP or OUTPUT_RESULT.

## Preserved unpublished V3H candidate

The complete original source/evidence package and exact patch were recovered and
checked independently. All 455 package entries and 356 source blobs matched their
manifests. All 341 baseline source paths were preserved by that candidate.

- Package: `AutoVPN-V3H-source-candidate-2026-10-06.zip`, 3,514,310 bytes.
- Package SHA-256:
  `55024e2700f6ffdbf834ab00f08bf0008ceb61574698f65e1e287618a5f00821`.
- Patch: `AutoVPN-V3H-from-28b3d83.patch`, 120,286 bytes.
- Patch SHA-256:
  `1c31a142073bfa5a5a210e30d2539ed5c1582a656d9af559a19263ca85f1d179`.
- Exact candidate base: `28b3d8354537d452032da1432201d06d4af8409c`.
- Candidate tree: `53cb49bac940b40258ba56890f4a1816019395cd`.
- Candidate assembly version: `0.1.8`, unpublished and not accepted on Windows.

V3H introduces `AutoVPN.Broker.Node.v1` (GetNodeState, StageNode, ClearNode),
bounded strict node validation, service identity/revision binding, a short-lived
in-memory draft and desktop handoff controls. Its 112 new cases and Linux-native
configuration parsing do not prove Windows handoff. Its draft uses placeholder
ports and must not be launched. CanConnect/CoreRunning/ProtectionArmed and
NetworkVerified remain false in that candidate's staging handler.

V3H's previous publication was declined and was not retried in this transfer.
It remains a separate preserved deliverable, not part of published main.
Before any subsequent integration, reconcile the actual diffs with current main;
do not blindly apply the original patch, overwrite newer README/checkpoint text,
or replace the current regression accounting with V3H's old 711-case accounting.
The arithmetic union would be 853 cases (741 + 112), subject to actual discovery;
no combined build or regression is claimed. The original package's Windows
ServiceLab and WPF checks remain NOT_RUN.

## One transient-source gap

The previous chat's last progress message said that a new runtime launch entry
had started Mihomo for six protocols and that UTF-16 to JSON handling was being
checked. No method/file names, exact patch, saved source package or completed
evidence for that final experiment were recoverable. It is absent from published
main and is not the saved V3H package. Treat it as unfinished work, not preserved
implementation or accepted six-protocol runtime coverage.

## Second continuation increment: owned node runtime entry

The missing transient experiment was not recovered. Its defined scope was
implemented independently from verified current main in `483d758df39915c82f0c0709bef060e6981d0aad`,
then corrected in `959174545e044c98796f1bcf14a03ff4a8bac42a` without integrating V3H.
Details: `docs/checkpoints/NODE_RUNTIME_ENTRY.md` and
`docs/evidence/NODE_RUNTIME_ENTRY_VALIDATION.json`.

The new entry validates an immutable six-protocol selection and UTF-16 before
canonicalization, then owns DNS, local ports, trusted YAML, credentials and native
startup through the existing supervisor. Corrected-source Linux runs passed all
866 cases six times except the 15 expected skips per run. The 125 new cases had
no failures: 750 executions on Linux, 696 on Windows plus 54 explicit Windows
skips. A separate real standard-user Windows Server process passed native startup,
controller authentication, string preservation and complete cleanup for all six
protocols. Installed status-service acceptance passed separately.

The full Windows gate remains **FAILED / NOT ACCEPTED**. Main run `37489076324`
has one existing VLESS/gRPC probe port-reservation failure; the V2/V3 Windows runs
also exposed output-drain and cancellation-deadline failures. The new diagnostics
confirm process exit before the output-drain timeout in the observed VLESS case;
they do not prove its root cause or a shared cause with the other failures.
All outcomes, including the first candidate's corrected DNS reason-code race,
were retained. Further identical reruns are not a substitute for investigation.

## Ordered continuation after the second increment

1. Investigate the recorded Windows pipe-cancellation and output-drain failures
   with bounded controlled evidence. Keep current deadlines, negative assertions
   and every failed outcome; do not retry remote TLS to obtain a pass.
2. Add precise local port-reservation diagnostics for the recorded Windows
   `CORE_PORT_UNAVAILABLE`, without assuming its missing cause or increasing
   attempts. The new node-runtime entry is implemented; do not redo the lost
   experiment or execute the V3H placeholder-port draft.
3. Reconcile and accept the preserved selected-node handoff separately, with real
   Windows service/WPF evidence and independent service-side validation.
4. Connect accepted handoff to service-owned core lifecycle and cancellation.
5. Implement owned network-state journaling and restoration before enabling
   disposable Windows TUN/WFP/DNS/IPv6 tests. Then test installation, update,
   uninstall, crashes, restart, sleep/resume, tray and Windows 11 user journeys.

Production SYSTEM core execution, TUN, WFP, system DNS/IPv6 protection, recovery,
the normal installer and Windows 11 acceptance remain OPEN/NOT_RUN. Keep feature
implementation, a successful build, status-service acceptance and an actual
protected connection distinct in every report.

## Third continuation increment: Windows pipe completion and port diagnostics

The next bounded implementation was published as `c8c43a7ffff95a235b248c6dc38b15e235b7ef78`
from `8b12e709777c352c384cbb8ea76b266d7e70c00d`.
See `docs/checkpoints/WINDOWS_PIPE_COMPLETION.md`. It addresses the demonstrable
worker-queue dependency in idle cancellation, removes remaining normal-path
blocking process readers, publishes cleanup ownership before callbacks, and
records bounded port role/phase/native errors. The existing deadlines and
32-attempt port budget remain unchanged. Four isolated controls include the
frozen previous delay mechanism, without changing the runner or product pool.

The suite now expects 892 cases (26 added), with 17 exact Linux skips and the
same 14 Windows skips. All seven workflows passed, attempt 1 without reruns.
Main run `37497048946`: Linux 5250 passed / 0 failed / 102 skipped; Windows
Server 2025 5268 passed / 0 failed / 84 skipped across six full iterations each.
Both source archives' 362 blobs, all twelve TRX and forty-two control JSON reports
were independently verified. Actual Windows stdout/stderr canceled in 5–7 ms
in the six isolated occupied-pool controls, before releasing either worker or
terminating the child. The frozen previous delay mechanism remained pending at
its one-second observation and then canceled after workers were released.

The six-protocol standard-user Windows primary-token lab and installed
status-service lab also passed. V2/V3 normal/native suites, five Windows TLS
matrix repetitions, five lifecycle repetitions per OS and both WPF smokes passed.
Full evidence is `docs/evidence/WINDOWS_PIPE_COMPLETION_VALIDATION.json`.
Previous failures remain recorded; no historical native port error was available,
so its old root cause remains unknown despite no recurrence in these runs.

At that checkpoint, the next step was to close the exceptional drain-timeout lifetime of
the validator and controlled-server helper, which can still dispose original
readers after a failed join. This needs bounded forced-failure tests; normal-path
success does not accept it. Then continue selected-node service handoff and owned
runtime lifecycle, with recovery before privileged networking. Product/SYSTEM/TUN
and Windows 11 gates remain unaccepted as described above.

## Fourth continuation increment: exceptional validator/server ownership

Implemented from `7c26fb139d03be3a012c42d1c07f5fcff897c952` in
`9fcb46530849b568f90e8eefd21e46c3141e03db`; the test-only Linux correction is
`6f819b12f04b056bd40244f68f2f3a4df5891e3a`, tree
`4b75afaa6b713a553e34985342ab2d951e9f6378`. Assembly remains 0.1.7, V3H excluded.
Details: `docs/checkpoints/VALIDATOR_CLEANUP_OWNERSHIP.md`; complete evidence:
`docs/evidence/VALIDATOR_CLEANUP_OWNERSHIP_VALIDATION.json`.

The validator and controlled TLS server retain the same process/readers/binary
lease/private input on failed exit or output joins. Cleanup is explicit, bounded
and retryable; late EOF does not run later cleanup stages or upgrade the original
result. Terminal output faults/cancellation settle ownership without becoming EOF.
Native exception metadata is bounded and excludes raw output, messages and paths.
Controlled-server and its outer diagnostic wrapper preserve original exception
objects together with cleanup capability. The existing real validator callsites
make at most one explicit retry while preserving the initial failed result.

Fifteen cases were added: ten managed ownership controls and five actual synthetic
validator process cases. Every prior 892 case remains, giving 907 with 18 exact
Linux skips and 14 Windows skips. Existing TLS/readiness/wrong-credential tests,
SDK/core pins, wait budgets, port attempts and runner parallelism are unchanged.

All 24 full TRX and four source archives (367 blobs each) were independently
verified; both builds have zero warnings/errors. Initial main run `37505925500`
passed Linux 5334/0/108 and Windows 5358/0/84 across six iterations. Its V2 Linux
normal run still failed on ETXTBSY after cancellation; that outcome remains. The
corrected Linux test verifies the same advisory lock with an initial negative
exclusive read and a successful exclusive read after release. It adds no sleeps,
retry loop, skipped assertion or production change.

**At the fourth-increment checkpoint, Windows acceptance remained FAILED / OPEN.** Corrected main run
`37506558777`: Linux 5334 passed / 0 failed / 108 skipped; Windows 5357 passed /
1 failed / 84 skipped. Windows iteration 5 of the new cancellation case passes
exit/EOF/cleanup/input assertions, then fails exclusive write to the copied
apphost. The holder is unknown. Only 53/54 validator JSON records exist because
that case fails before reporting. Both revisions retain all 42 old control JSONs;
the original has all 54 new records. All six Windows retained-input/explicit-retry
cases passed on both revisions with unchanged initial failure snapshots.

Corrected V3 Windows normal additionally fails during fixture directory deletion
in DisposeAsync. Its verified raw TRX has no output JSON, so successful body
completion is not established; disposal may have replaced a preceding failure.
That V3 Windows native/lifecycle/WPF/package work was skipped. V2/R1 suites and
WPF pass; V3 Linux suites and lifecycle pass. Both main Windows labs pass on both
heads: actual standard-user primary-token runtime for six protocols, separately
the installed status-service with impersonated real-account pipe authorization.
Win2022 was not triggered by the test/docs correction; its prior service pass is
retained under `9fcb465`. Every run is attempt 1; none was rerun until green.

Next bounded step: preserve body and fixture-disposal errors together; emit the
cleanup snapshot before the existing binary assertion; observe the same owned
handle and collect bounded read-only evidence about the actual Windows holder.
Keep assertions/deadlines and unknown causes honest. Do not infer antivirus or
kernel timing without positive evidence, bypass the failure with sleeps, or move
to privileged networking before the remaining lifetime acceptance is addressed.
Then continue selected-node handoff as a separate reviewed implementation.


## Fifth continuation increment: Windows file-lifetime evidence

Published from `9f3652fe4a5495525b4244c3504685ccea5b3150` as code commit
`97f8423ad5314037270024ace083a22061273cd6`, tree
`98504872d2c42df30b1a27af3eb2c72529107b03`. Assembly remains 0.1.7; V3H excluded.
Details: `docs/checkpoints/WINDOWS_FILE_LIFETIME_DIAGNOSTICS.md`. Complete normalized
evidence: `docs/evidence/WINDOWS_FILE_LIFETIME_DIAGNOSTICS_VALIDATION.json`.

Body and fixture-disposal exceptions are now preserved together. The same owned
binary SafeFileHandle is captured before hash I/O; immutable reports separately
record presence, returned Dispose calls, IsClosed and IsInvalid. Validator snapshots
are emitted before the unchanged single file probe. On failure, a two-second
helper queries one exact copied file through bounded Restart Manager calls;
maximum two list calls, 32 reported owners in three categories, 4096 retained bytes, actual EOF and owned
exit/output cleanup. Cached PID plus creation FILETIME survives fixture Process
wrapper disposal without retaining another native handle. No foreign process is
acted on. Existing assertions, deadlines, pool settings and port attempts remain.

All 907 prior identities remain in the 920-case suite. Thirteen new cases pass:
132 passed / 0 failed / 24 explicit platform skips across all twelve iterations.
Main run `37539642819`: Linux 5394/0/126; Windows 5429/1/90. All 375 blobs in
both main source archives match; raw TRX and control records were verified.

**Windows file-lifetime acceptance is still FAILED / OPEN.** In iteration 2 the
existing cancellation case again fails Write/None with IO `0x80070020`. Its saved
pre-probe report confirms exit/EOF/input removal/Complete, both Dispose calls
returned, and the same binary SafeFileHandle reports IsClosed=true/IsInvalid=false.
The later RM query returns no owners with Incomplete=true; helper cleanup succeeds.
Fixture root deletion then succeeds, leaving the original body failure intact.
The holder is unknown; neither native closure beyond the managed observations nor
kernel/antivirus attribution is established. All six records from that failed case
survive, alongside 53 actual success reports, 271 lifetime records, 24 helper
controls and all 42 prior pipe records. Missing success records are not fabricated.

All six other automatic workflows pass in attempt 1. V2/V3/R1 normal/native suites,
Windows TLS repetitions, both-OS lifecycle repetitions and three WPF smokes pass.
The two status-service labs (Server 2025/2022) and the six-protocol standard-user
primary-token lab pass within their existing boundaries. Four source archives
contain exact 375-blob code. Large development binary archives were not locally
downloaded; their API metadata and complete package-job verification logs are kept.

Next bounded step: controlled investigation of the remaining file-access interval,
including the fixture's independent retained process handle versus its release.
This is an unproven hypothesis. Preserve the existing failed write outcome and
budgets, and do not rerun identical workflows, delay or retry writes until green.
Then continue selected-node service handoff as a separate reviewed increment.
Production SYSTEM core/TUN/network protection/recovery/installer/Windows11 remain
unaccepted. This fifth increment supersedes the fourth increment's next-step list
without erasing its failed evidence.


## Sixth continuation increment: native process signal and original output readers — 7 October

Implementation `463048597a73977c43ba425b829a5bcb0e69f4bc` strengthens the exact Windows
process-handle exit gate and explicit ownership of the original stdout/stderr
readers. Corrected test/source `19196ebafa8d5a9697f465a275afb4a350d47098` changes only
a new Linux handle-observation expectation and its checkpoint; all production
blobs between the two revisions are identical. Continue from remote main, whose
final documentation commit follows this tested source.

Details: `docs/checkpoints/NATIVE_PROCESS_EXIT_OWNERSHIP.md` and
`docs/evidence/NATIVE_PROCESS_EXIT_OWNERSHIP_VALIDATION.json`.

The exact .NET 10.0.12 source shows that managed HasExited can return by exit code
before native signal, and Process.Close leaves externally accessed SyncMode
readers to their caller. Both are now handled explicitly. Native cleanup accepts
only WAIT_OBJECT_0 from the same owned SafeProcessHandle within the existing
remaining exit budget. A finite dedicated native wait does not occupy a managed
pool worker; normal managed continuation scheduling remains. Original readers
are released only after original tasks settle and input-directory cleanup succeeds.

Corrected main run `37561949766`, attempt 1: six times 940 unique cases per OS.
Windows Server 2025 totals 5550 passed / 0 failed / 90 skipped; Linux totals
5496 passed / 0 failed / 144 skipped. All previous 920 identities remain. Twenty
new cases yield 222 passes / 0 failures / 18 platform skips per source. All six
triggered workflows passed. Four primary archives from both revisions, all
24 TRX and every 380-blob source manifest were independently checked.

Each source directly observes 18 actual validator cancellations where managed
HasExited is true before native signal, plus six timed-out-helper controls with
the same gap. The new gate waits for signal. Independent held/disposed observer
arms both pass six times; their same SafeProcessHandle states are recorded before
one unchanged Write/None probe each. All 42 Windows writes and 24 Linux reads
per source, plus all 66 fixture-directory deletions, succeed. No file-operation
retry or timer padding was added.

Keep two first-source failures distinct. The main Linux series has 24 new
assertion failures because Unix uses AnonymousPipeClientStream and FileStream
handle observations are correctly null. That expectation is corrected. First R1
Windows run `37561704390` has one separate QUERY_TIMEOUT in the no-holder RM
control; cleanup succeeds, but no query return codes were observed. Its full
record and two-second budget remain, and its cause is OPEN. Later passing CI is
not an explanation. The older 0x80070020 sharing failure remains preserved with
cause UNPROVEN; the observed native exit gap does not uniquely prove causation.

Corrected secondary evidence includes 28 distinct raw TRX (11978 passed / 0 failed /
304 skipped), V2 TLS repetitions, V3 lifecycle repetitions on both OSes, and
V2/V3/R1 WPF eight-PNG smoke. Actual Server 2025 node runtime and installed-service
labs pass on both sources; Server 2022 passes on implementation source only and is
NOT_RUN_ON_CORRECTED because the two changed paths do not trigger that workflow.
The relevant source blobs are unchanged. Large package archives have API/log
checks only. Runtime uses a real standard-user primary process token, service
client authorization uses impersonation; runner housekeeping remains separate
from owned-job forcedJobTermination=false.

Next bounded work: explicitly own the diagnostic helper stdin writer on failed
write/close paths and add phase observations distinguishing helper start/input/RM
query/response inside the same total two-second budget. Retain the failing R1
control, all original assertions and every outcome. Then continue selected-node
service handoff from the existing runtime entry; the unpublished V3H remains
separate. SYSTEM runtime, owned network recovery, TUN/WFP/DNS/IPv6, the installer
and Windows 11 user journeys remain unaccepted. Do not spend further identical
reruns merely to accumulate green executions.
