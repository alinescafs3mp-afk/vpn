# Diagnostic helper input ownership and observed phases

Status: implementation prepared; compilation, regression, artifact verification and
Windows runtime evidence are PENDING. This increment follows main
`caecbc6b35f5cb682670ae620e5571b9f9148ce3`; no historical evidence is replaced.

## Scope and reasoning

The diagnostic helper now hands its exact accessed stdin writer to the same
resource owner as its process and original output readers before starting input
I/O. The original write plus flush task is retained independently of a caller's
canceled wait. Input settlement shares the existing output join budget. A failed
write, flush or close does not erase the original failure or release ownership.
Normal successful EOF close is remembered and is not repeated by later cleanup.
A failed Dispose remains a pending release even if its finally already closed
the underlying handle; DisposeReturned and SafeHandleClosed are separate facts.

The exact [.NET 10.0.12 Process source](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.cs)
sets `_standardInputAccessed` on the public getter and leaves that writer to the
caller in Close. The exact [StreamWriter source](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/IO/StreamWriter.cs)
closes its stream in Dispose's finally even when its flush throws. The relevant
source blobs are `4218596c12964482207447d271c794d08e397072` and
`229509254e1d99907753198656994d10fe742875` respectively.

A shared test-only, closed NDJSON protocol reports helper start, input receipt and
validation, each RM boundary, cleanup of the RM session, and final result. It keeps
at most 4096 stdout bytes and 16 frames. Parent observations include write, flush,
close, process wait, output join and result parsing. Unknown fields, duplicate
properties, invalid ordering, invalid enum values, inconsistent owner counts and
incomplete frames cannot become accepted evidence. No path, PID, native handle,
input content, raw stdout or exception message is included in these reports.

Every collector update reads the monotonic clock. Observations at or after 2000 ms
cannot rewrite the immutable BeforeDeadline prefix. Attempt-end and after-cleanup
snapshots are separate. Parent elapsed checks also refuse late nominal success
when a cancellation timer callback is delayed. These are parent-received
observations, not proof of the child's precise execution position at the deadline.
Synchronous Process.Start and writer Dispose cannot be preempted by this token;
this is not a guarantee that the entire diagnostic-plus-cleanup call returns in
exactly two seconds. The query budget remains two seconds; cleanup keeps its
existing five-second exit and three-second shared I/O join budgets.

## Controls and accounting

Exactly twenty new cases are intended: six original input-owner controls, three
actual input-transfer controls, eight protocol/cutoff controls, and three Windows
helper controls. All previous 940 identities and platform assertions remain.
The full native suite is now 960 unique cases. Expected skips are 15 on Windows
and 27 on Linux, the latter adding only the three new Windows controls.

The Windows controls stop before reading input and before the first native RM
query, or close the disposable child's inherited stdin pipe before the parent's
actual write. The query control deliberately stalls BEFORE RmGetList; it must
never be presented as an observed native RM hang. Broken-pipe cleanup retains
its first failure if Dispose fails, then exercises explicit retry of that same
capability without rewriting the original observation.

## Preserved evidence and acceptance limits

First R1 Windows run `37561704390` at source
`463048597a73977c43ba425b829a5bcb0e69f4bc` had a distinct no-holder QUERY_TIMEOUT.
Its RM codes were null and QueryCalls was zero; cleanup succeeded. That historical
attempt had no phase protocol, so its cause remains OPEN. New phase observations
cannot retrospectively determine its cause. The older `0x80070020` sharing failure
also remains preserved with cause UNPROVEN. See
`docs/checkpoints/NATIVE_PROCESS_EXIT_OWNERSHIP.md` and
`docs/evidence/NATIVE_PROCESS_EXIT_OWNERSHIP_VALIDATION.json`.

No version, pinned core/runtime, production 20/5/3 budgets, port attempts,
TLS/credential/readiness checks, ThreadPool controls or workflow matrix is changed.
The implementation is verified through existing disposable CI only; no local
routes, DNS, firewall, TUN or service state is changed. SYSTEM core operation,
selected-node service handoff, network recovery, TUN/WFP/DNS/IPv6 protection,
installer and Windows 11 user journeys remain unaccepted. V3H remains separate.

## First-source observation and narrow correction

Implementation source `7e3dfdc48908085f5b3f85d41e0135db144a8e51`, tree
`8a10906874e1680a1c0567967f6804bbd6ca989c`, passed all twelve main regressions:
Windows 5670 passed / 0 failed / 90 skipped; Linux 5598 / 0 / 162. All 960
identities, including the previous 940, and both 388-blob source archives were
independently checked. Its three Windows runtime labs passed as well.

A distinct additional Windows package-stage execution in V3 run `37595318409`,
job `112706416136`, failed the new BeforeInput control at its BudgetExpired
assertion. The actual report was QUERY_TIMEOUT/CANCELED with stopwatch 1999 whole
milliseconds and BudgetExpired=false. HELPER_STARTED had been received at255ms,
and complete cleanup took12ms. Two older after-input controls in the main Windows
series also recorded cancellation at1999ms; their original assertions passed.
All these records are retained, separately from the much older R1 RM timeout.
This is an observed disagreement between cancellation and whole-millisecond
elapsed reporting, not evidence of a native RM hang or failed resource cleanup.

The narrow correction records BudgetCancellationRequested from the actual owned
CTS before disposal. BudgetExpired means that this signal was observed OR the
local monotonic elapsed reached2000ms. Elapsed remains exactly measured; it is
never rounded up or replaced. Both parent and helper collectors read the captured
live token on every update, so a cancellation observed at1999ms also freezes the
prefix against later frames and EOF at that same millisecond. Conversely, the
existing2000ms clock cutoff still works if cancellation delivery is delayed.
The token itself is captured before its source is disposed because original I/O
may settle during later cleanup. The timer, two-second duration, all native
control assertions, test names and counts are unchanged. The existing managed
deadline case gains assertions for the1999ms cancellation-signal boundary.

Corrected-source execution and evidence verification: PENDING.
