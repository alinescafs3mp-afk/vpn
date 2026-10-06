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

The next bounded implementation is prepared from `8b12e709777c352c384cbb8ea76b266d7e70c00d`.
See `docs/checkpoints/WINDOWS_PIPE_COMPLETION.md`. It addresses the demonstrable
worker-queue dependency in idle cancellation, removes remaining normal-path
blocking process readers, publishes cleanup ownership before callbacks, and
records bounded port role/phase/native errors. The existing deadlines and
32-attempt port budget remain unchanged. Four isolated controls include the
frozen previous delay mechanism, without changing the runner or product pool.

The suite now expects 892 cases (26 added), with 17 exact Linux skips and the
same 14 Windows skips. Exact-source CI is PENDING; the three preceding Windows
findings are not declared resolved merely because code has changed. Preserve
the previous failed results and record the new source, full TRX and control JSON.
