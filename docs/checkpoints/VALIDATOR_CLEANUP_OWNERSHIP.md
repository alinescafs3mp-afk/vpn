# Validator and controlled-server exceptional cleanup ownership

Continuation from `7c26fb139d03be3a012c42d1c07f5fcff897c952` on 2026-10-06.
Assembly version remains 0.1.7. This slice is independent of the unpublished V3H
candidate. Implementation is published in `9fcb46530849b568f90e8eefd21e46c3141e03db`;
the Linux test correction is `6f819b12f04b056bd40244f68f2f3a4df5891e3a`, tree
`4b75afaa6b713a553e34985342ab2d951e9f6378`.
**Implementation is present; Windows cancellation file-lifetime acceptance remains
FAILED / OPEN.** Both revisions' complete CI outcomes are retained in
`docs/evidence/VALIDATOR_CLEANUP_OWNERSHIP_VALIDATION.json`. The implementation
host has no .NET SDK; the recorded builds and native tests ran in GitHub Actions.

## Problem and boundary

The previous validator used outer `using` declarations for the pinned binary and
process. A failed exit/output join could return an uncertainty reason and then
still dispose those same resources. Its `finally` could make a second five-second
kill attempt, and an `IOException` deleting the private input directory was ignored.
An access-denied error could instead escape with native path text. Preparation
before the `try` also lacked a cleanup obligation on partial failure.

The controlled native TLS server similarly disposed its `Process` in an inner
`finally` even when output joining failed. A cleanup failure could replace the
original handshake failure, and the diagnostic wrapper stringified exceptions,
discarding any retained resource capability. Normal-path success in the preceding
slice did not establish correct behavior for these exceptional paths.

## Ownership and outcomes

`OwnedProcessCleanup` retains the exact process, its original stdout/stderr tasks,
the binary's read/non-delete-sharing handle where present, and its private directory.
Only trusted creator code can construct the native resource bundle. The public
retry capability accepts neither a PID nor a path and is omitted from JSON output.

An attempt confirms process exit, joins both original reader tasks, removes the
directory, then releases process and binary handles, in that order. A failed stage
preserves its obligation and returns a bounded metadata snapshot. No automatic
background retry or finalizer removes input or closes a reader after a timeout.
A late exit/EOF alone does not run subsequent cleanup stages. An explicit retry
uses the same resources and skips only stages already confirmed complete.

One task is published before any cleanup callback runs. Concurrent/reentrant
callers join that attempt; canceling a caller's wait does not cancel or discard
the operation. A later explicit call may retry a completed failed attempt. The
original validator result and its cleanup snapshot remain immutable even if that
retry succeeds; `LastReport` on the capability records the newer cleanup outcome.

Reader settlement and healthy EOF are separate. Faulted or canceled readers are
observed and may allow resources to be released after confirmed child exit, but
never count as successful validation. A reader throwing `TimeoutException` is a
terminal read failure, not a timeout of the join. Pending readers retain ownership.

`CoreValidationResult` preserves its original three positional fields and adds
`ValidationReasonCode`, `CleanupReport`, and `PendingCleanup`. The initial operation
reason survives a cleanup override. Pending exit/directory/release maps to
`CORE_CLEANUP_UNCERTAIN`; pending output maps to `CORE_OUTPUT_UNCERTAIN`; settled
unhealthy output maps to `CORE_OUTPUT_FAILED`. Expected preparation/start failures
are converted to `CORE_VALIDATION_FAILED` with a fixed phase, exception category
and HResult. Output contents, credentials, native messages and paths are excluded.

The validation wait is still 20 seconds. Each cleanup attempt allows five seconds
for process exit and the existing three seconds for validator output (five seconds
for controlled-server output). There is no second hidden kill in the validator's
`finally`. These are asynchronous wait budgets, not a hard real-time guarantee for
OS operations or scheduling. Synchronous deletion and handle release follow the
successful joins; failure retains the same capability.

Controlled-server teardown preserves an existing test failure and a cleanup
failure together. Its caller wrapper preserves the exception object rather than
converting it to a string. Existing real validator tests explicitly handle one
bounded cleanup retry while preserving the first failed validation as a failure.

## Focused verification

Managed resource controls exercise pending exit, pending output, late completion,
fault/cancel settlement, a read-side timeout, directory IO/access errors, a canceled
waiter, concurrent/reentrant admission, and explicit retry. They do not claim native
process or Windows handle behavior.

A separate finite synthetic apphost is invoked through the public pinned-hash
validator entry and its exact `-t -f ... -d ...` arguments. It uses no networking,
TUN, live subscription or product credential. Real process controls cover finite
output and successful/failed exit. A Windows file-lock control forces directory
cleanup failure after actual child exit and checks that the retained binary handle
still prevents write/delete until an explicit successful cleanup retry. This
distinguishes the validator's retained lock from the OS lock on a running image.

The fifteen added cases comprise ten managed ownership controls and five public
validator cases: exit 0, exit 23, cancellation after actual readiness, Windows
locked-input cleanup, and malformed no-BOM UTF-8 with an otherwise successful
child exit. The full suite expects 907 unique cases with 18 exact Linux skips and
14 Windows skips; every previous case remains. Validator decoding defaults to
UTF-8 with exception fallback, while existing BOM autodetection is unchanged.
The malformed-output control establishes failure reporting for its exact no-BOM
input; this slice does not establish an all-input strict-UTF-8 contract.

Existing TLS profiles, readiness, wrong-credential assertions, certificate policy,
20/5/3-second production wait budgets, 32 port attempts, and runner parallelism
remain unchanged. The full suite must keep every prior case and exact OS skips.
No workflow rerun is used to replace an observed result.

## First CI finding and scoped test correction

The first code commit is `9fcb46530849b568f90e8eefd21e46c3141e03db`, tree
`0e39f0fa464b793dfeec9b7dd554d8a10a853c6c`. V2 run `37505925526`, Ubuntu job
`112414447230`, normal iteration recorded 851 passed / 1 failed / 31 skipped
out of 883. The new cancellation case had already confirmed actual process exit,
healthy EOF, complete cleanup, and removed input before its final write-open of
the copied apphost failed with `IOException: Text file busy` (ETXTBSY).
The native suite in that same attempt subsequently passed 889 / 0 / 18 of 907;
that later success does not replace the failed normal result.

Linux write access to an executable additionally depends on kernel image lifetime.
The precise source of that lifetime in this recorded process is not established.
The corrected Linux test instead verifies the validator's own advisory FileStream
lock: `Read/None` must fail while the validator is ready and still holds `Read/Read`,
then the same exclusive read must succeed after cleanup. The negative control is
on the same file and filesystem, so unsupported/disabled advisory locking cannot
silently satisfy the test. There are no write retries, sleeps, enlarged deadlines,
new skips, or product changes. Windows still requires exclusive write after cleanup
and both write/delete denial after actual child exit during the forced cleanup
failure. JSON distinguishes `EXCLUSIVE_READ` from `EXCLUSIVE_WRITE` and records
Linux writability as unmeasured.

Reference implementation: [.NET 10.0.12 Unix file sharing](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs).
Historical outcomes remain in the final evidence alongside the corrected source.

## Exact-source validation and current Windows failures

All four regression ZIPs were independently verified against API size/SHA-256,
ZIP CRC and the exact inner source manifest. Each contains the reviewed 367 blobs
with matching size, SHA-256 and Git blob identity. All 24 full-suite TRX contain
907 unique `(testId, testName)` pairs and preserve every previous 892 identity.
Builds report zero warnings and errors. Every workflow ran in attempt 1; none was
rerun to replace an outcome.

| Source / main run | Platform | Six complete iterations: passed / failed / skipped |
|---|---|---:|
| `9fcb465` / `37505925500` | Linux | 5334 / 0 / 108 |
| `9fcb465` / `37505925500` | Windows Server 2025 | 5358 / 0 / 84 |
| `6f819b1` / `37506558777` | Linux | 5334 / 0 / 108 |
| `6f819b1` / `37506558777` | Windows Server 2025 | **5357 / 1 / 84** |

The first main regression passed, but its separate V2 normal Linux failure above
still means the initial revision was not entirely green. The corrected main
Windows failure occurs in iteration 5, job `112416595405`, in
`CancellationAfterActualChildReadyJoinsExitReadersAndInputCleanup`. Its stack
reaches `AssertBinaryReleased`: after the source-ordered exit, EOF, cleanup report
and absent-input assertions, the existing Windows `Write/None` probe throws an
`IOException` because compatible access is unavailable. The holder is **unknown**;
the report alone cannot distinguish an own handle, another process, or image
lifetime. This failure is retained, not attributed to antivirus or scheduling.

The failed case stops before `Evidence()`. Corrected raw TRX therefore contains
**53 of 54** expected validator JSON records; the missing record is not invented.
The initial revision contains all 54. All 42 previous cancellation-control JSONs
are present and valid on each revision. All six forced Windows locked-input tests
pass on both revisions: actual child exit, IO `0x80070020`, binary write/delete
denial before explicit retry, release after retry, and unchanged initial failure.
The corrected fifteen new cases total 89 passed / 1 failed on Windows and
84 passed / 6 expected platform skips on Linux across six runs each.

A separate corrected V3 Windows normal run (`37506558738`, job `112416594915`)
records 864 passed / 1 failed / 18 skipped out of 883. Raw artifact `11432416205`
was independently verified by API digest/size and CRC. The same cancellation case
throws `UnauthorizedAccessException` in fixture directory deletion during
`DisposeAsync`. Its stdout/stderr are empty. An earlier body failure may have been
replaced by await-using disposal; a successful body or write probe is not proven.
Its V3 Windows native, lifecycle, WPF and package stages did not run.

The corrected V2 normal/native suites pass on both OSes; Windows TLS passes five
six-case repetitions and WPF renders eight states with reopen/settings checks.
V3 Linux normal/native and five twenty-case lifecycle iterations pass. R1 passes
both OS suites, Windows WPF and the development-package job; ordinary CI passes.
The previous Windows Server 2022 status-service lab passed on `9fcb465`; it was
not triggered by the test/document-only correction and is not claimed as rerun.

Both revisions' main Windows labs pass independently. The six-protocol runtime
lab uses a real standard-user primary process token, confirms natural process/job
exit and complete owned cleanup. The installed status-service lab verifies SCM
and pipe authorization with real account tokens under impersonation; that is a
different scope from the runtime lab's primary-token proof. Neither enables SYSTEM
core execution, TUN or protected public connectivity.

## Next bounded investigation

First preserve body and fixture-disposal exceptions together in the new validator
test harness. Record the immutable cleanup snapshot before the existing binary
assertion, then record that one operation's sanitized error category and native
code. Separately observe release of the same owned handle and investigate other
holders with bounded read-only Windows diagnostics. If the holder is not identified,
keep it unknown. Preserve the existing assertions, output/exit budgets and failure
history; do not add sleeps or retry the operation/workflow until it happens to pass.

Selected-node service handoff follows this investigation; do not enable privileged
networking to work around a failing resource-lifetime gate.

## Product acceptance boundary

This work closes helper resource ownership. It does not authorize or enable
production SYSTEM core execution, selected-node service handoff, TUN, WFP, system
DNS/IPv6 changes, restoration, an installer, or Windows 11 acceptance. The existing
Windows service remains status-only. Historical failed runs and their unknown
causes remain in their original evidence files.
