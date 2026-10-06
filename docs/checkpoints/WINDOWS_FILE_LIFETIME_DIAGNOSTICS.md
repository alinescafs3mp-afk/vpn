# Windows validator file-lifetime diagnostics

Continuation baseline: `9f3652fe4a5495525b4244c3504685ccea5b3150`.
The prior validated source and both open Windows failures remain recorded in
`VALIDATOR_CLEANUP_OWNERSHIP.md` and its evidence JSON. This increment closes
observability gaps; it does not assume the cause of those failures.

## Scope and fixed boundaries

The existing cancellation test once failed its exclusive write-open after the
child exit, output joins, input removal and cleanup report had been checked. A
separate Windows run failed fixture directory deletion; implicit `await using`
could replace an earlier body exception. Neither failure identified a file holder.

`OwnedFixtureExecution` now always awaits the existing fixture cleanup once and
preserves both original exception objects when body and disposal fail. Single
failures keep their exception identity, stack and cancellation token. Existing
validation assertions and cleanup/retry budgets remain in place.

`NativeProcessCleanupResources` captures the same managed `SafeFileHandle` once,
before hashing starts. It retains no extra native reference. Each immutable
cleanup snapshot records resource presence, whether each `Dispose` returned,
and independent nullable `IsClosed` and `IsInvalid` values. These observations
never alter `Complete`, admission, the original validation result or explicit
retry semantics. They do not establish that no other file user exists.

Public-validator fixtures write an immutable result snapshot before assertions,
then a second record immediately before the unchanged single platform file probe.
The Windows probe remains `Write/None`; Linux retains its verified `Read/None`
negative/positive pair. A failed probe is never retried to make the test pass.
Fixture directory deletion also has before/success/failure records. The original
validation and latest explicit cleanup reports are kept separate.

## Bounded Windows query

Only after an original file operation fails, a test-only helper uses Restart
Manager to query that exact owned copied executable. It launches from the
original fixture apphost, avoiding a newly running copy of the target image.
The request is local bounded JSON on standard input; no paths, PIDs, creation
times, application/service names or native exception messages enter the report.

Only `RmStartSession`, registration of one file, at most two `RmGetList` calls,
and `RmEndSession` are used. No process is registered as a resource. There are
no `RmShutdown` or `RmRestart` calls. Only the helper that this test started may
be stopped by its existing owned cleanup capability. The query has a two-second
external deadline, followed by the existing exit-five/output-three-second owner
cleanup. Actual output EOF is drained with at most 4096 bytes retained. A finite
ten-second control verifies stopping and joining a timed-out helper.

Holder categories are test host, exact owned child, or other. Identification
requires PID plus raw creation FILETIME from the retained process handle. The
owned child's identity is captured before fixture disposal, without retaining an
extra native handle. Native errors, changing/oversized lists, unavailable input,
malformed output and timeout remain explicit diagnostic states. At most 32 owners
are returned. A later empty or incomplete observation cannot prove the file was
unheld at the earlier failed operation; Restart Manager does not report share flags.
Incomplete helper cleanup retains its original capability alongside the original
failure. Query failure never converts the test into success.

## Controls and validation

The verified suite is 920 unique cases: all prior 907, six body/disposal controls,
three same-handle observation controls and four diagnostic controls. The latter
include three Windows-only cases and a non-Windows refusal case. Verified skips
are 15 on Windows and 21 on Linux; previous skip identities remain unchanged.
The existing six full iterations, one-second cancellation checks, 32 port
attempts, TLS and wrong-credential checks, runner parallelism and production
20/5/3-second budgets remain unchanged. No workflow rerun is used to obtain green.

Implementation-host build: **NOT_RUN** (no local .NET SDK). Exact-source CI builds
on Linux and Windows succeeded with zero warnings/errors. Code commit:
`97f8423ad5314037270024ace083a22061273cd6`; tree:
`98504872d2c42df30b1a27af3eb2c72529107b03`. Documentation-only successors do not
change this validated code. Full evidence:
`docs/evidence/WINDOWS_FILE_LIFETIME_DIAGNOSTICS_VALIDATION.json`.

## Exact result and observed recurrence

Main run [37539642819](https://github.com/alinescafs3mp-afk/vpn/actions/runs/37539642819),
attempt 1, completed all six iterations on both OSes. Source and evidence integrity
are accepted; **Windows file-lifetime acceptance remains FAILED / OPEN**.

| Platform | Cases and unique IDs per iteration | Passed | Failed | Skipped |
|---|---:|---:|---:|---:|
| Linux | 920 | 5394 | 0 | 126 |
| Windows Server 2025 | 920 | 5429 | 1 | 90 |

Both primary ZIPs match API size/SHA256, ZIP CRC and all 375 reviewed source blobs.
All prior 907 identities and skip sets are preserved. The 13 new cases produced
132 passes, zero failures and 24 declared platform skips across twelve iterations.
The controlled test-host holder was classified correctly six times; six finite
query-timeout controls confirmed helper exit, EOF and cleanup. Linux refusal
started no helper. All original 42 pipe-control JSON records remain intact.

The existing cancellation case failed in Windows iteration 2, job `112529205469`,
at the unchanged one-shot `File.Open(... Write, None)` with `System.IO.IOException`,
`IO / 0x80070020`. The earlier result and before-probe snapshot now survive:

| Observation before the failed open | Value |
|---|---|
| Child exit confirmed, both reader EOF, private input removed | All true |
| Cleanup Complete / ResourcesReleased | Both true |
| Process.Dispose returned / binary FileStream.Dispose returned | Both true |
| Same captured binary SafeFileHandle.IsClosed | true |
| Same captured binary SafeFileHandle.IsInvalid | false |

The later RM helper returned `NO_HOLDER_OR_INCOMPLETE`: Start/Register/GetList/End
all code 0, one list call, zero reported owners, `Incomplete=true`, no truncation.
The helper itself exited and joined healthy EOF, then the fixture's one directory
delete succeeded. The original file-open exception remains the test outcome.
The original validation reason remains `CANCELED`; no successful cleanup upgrades it.

This establishes the managed release observations at the failed operation. It does
not establish native file-handle closure beyond those observations, identify a
holder at that moment, or prove kernel/antivirus involvement. A later empty RM list
is not a historical absence test. The cause of the sharing violation remains unknown.

All 54 validator executions have `VALIDATION_RETURNED` and `BEFORE_BINARY_PROBE`.
There are 53 actual successful-probe records and 53 legacy final validator records;
the failed case does not acquire a fabricated success record. All 54 fixture
root deletions succeeded. There are 271 lifetime observations, including the six
actual records for the failure, and 24 independent helper-control JSON records.

## Additional CI and Windows labs

All six other automatic workflows passed, attempt 1 without reruns. V2, V3 and R1
normal/native stages passed on both OSes. Normal suites contain 896 cases:
Linux 862/0/34 and Windows 877/0/19; native stages contain 920:
Linux 899/0/21 and Windows 905/0/15 (passed/failed/skipped).
V2 Windows TLS passed five six-case repetitions. V3 lifecycle passed five
20-case repetitions on each OS. V2/V3/R1 WPF smoke each produced eight PNGs and
confirmed reopen/settings/same-user DPAPI while TUN and public network consent
remained disabled. Ordinary CI and development package jobs also passed.

The secondary review preserved 28 distinct raw TRX, excluding three copied prior
stage results from execution totals. Fifteen downloaded secondary ZIPs passed
API size/SHA256/CRC; its two source archives also match the 375 reviewed blobs.
Large development binary archives were not downloaded; their API metadata and
full package verification logs were retained. This is not installer acceptance.

The six-protocol non-TUN runtime lab passed under an actual standard-user primary
process token, with natural owned process/job completion and `forcedJobTermination=false`.
Status-service labs passed on Server 2025 and 2022, including SCM lifecycle,
account authorization and negative requests. Their client identity proof uses
real-account impersonation and is distinct from the runtime primary-token lab.
Runner orphan housekeeping is recorded separately from owned runtime cleanup.

Evidence keeps every parsed main/secondary/lab observation and raw failure.
Repeated immutable cleanup snapshots are stored once under `cleanupReports` and
referenced by `cleanupReportRef`; expanding these references was checked for exact
value equality and canonical SHA256 against all three independent input reports.
Primary archive provenance and the entire reviewed source manifest are retained.

## Next bounded step

The observability gaps are repaired and their 13 new controls pass, but the original
sharing failure reproduced. Keep this failure and the older runs. Investigate the
remaining interval between managed release and exclusive file access with a bounded
controlled comparison, including the fixture's separately retained process handle
versus its released state. That is a hypothesis to test, not an established cause.
Retain the existing assertion and original failure on every path; do not add a
successful-write retry or longer sleep. Further instrumentation must retain actual
owned identities and cleanup and must not enumerate/terminate arbitrary processes.
Selected-node service handoff follows as its own reviewed increment. Privileged
networking, Windows 11 and the normal installer remain unaccepted.

## Primary API references

- [FileStream.SafeFileHandle](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.safefilehandle?view=net-10.0): getter may flush/reposition, hence capture before I/O.
- [SafeHandle.IsClosed](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.safehandle.isclosed?view=net-10.0): closed and invalid are distinct observations.
- [RmGetList](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/nf-restartmanager-rmgetlist): bounded application/service observation and `ERROR_MORE_DATA`.
- [RM_UNIQUE_PROCESS](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/ns-restartmanager-rm_unique_process): PID and `GetProcessTimes` creation FILETIME.
