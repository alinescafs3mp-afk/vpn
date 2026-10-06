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

The intended suite is 920 unique cases: all prior 907, six body/disposal controls,
three same-handle observation controls and four diagnostic controls. The latter
include three Windows-only cases and a non-Windows refusal case. Expected skips
are 15 on Windows and 21 on Linux; previous skip identities remain unchanged.
The existing six full iterations, one-second cancellation checks, 32 port
attempts, TLS and wrong-credential checks, runner parallelism and production
20/5/3-second budgets remain unchanged. No workflow rerun is used to obtain green.

Implementation-host build: **NOT_RUN** (no local .NET SDK). CI results and source
provenance will be recorded after the normal main-push workflows finish. A green
series would verify this increment and its controls; it would not retroactively
explain the prior Windows failure or accept Windows 11/TUN/product installation.

## Primary API references

- [FileStream.SafeFileHandle](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.safefilehandle?view=net-10.0): getter may flush/reposition, hence capture before I/O.
- [SafeHandle.IsClosed](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.safehandle.isclosed?view=net-10.0): closed and invalid are distinct observations.
- [RmGetList](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/nf-restartmanager-rmgetlist): bounded application/service observation and `ERROR_MORE_DATA`.
- [RM_UNIQUE_PROCESS](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/ns-restartmanager-rm_unique_process): PID and `GetProcessTimes` creation FILETIME.
