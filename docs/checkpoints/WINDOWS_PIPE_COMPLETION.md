# Windows pipe completion and local port diagnostics

Continuation from `8b12e709777c352c384cbb8ea76b266d7e70c00d` on 2026-10-06.
Implementation slice prepared for the existing main-branch CI. Validation is
**PENDING**, not accepted until the exact source artifacts and raw results are
checked. No local .NET SDK is available on the implementation host.

## Observations being addressed

The previous compiled source was `959174545e044c98796f1bcf14a03ff4a8bac42a`.
Its full outcomes remain in `docs/evidence/NODE_RUNTIME_ENTRY_VALIDATION.json`.

- Run `37489073224`, Windows job `112356688512`: the managed fake idle reader
  did not complete cancellation within its existing one-second assertion.
- Run `37489073029`, Windows job `112356686311`: a controlled VLESS probe
  confirmed child exit, then timed out joining stdout/stderr before releasing
  resources. `THREADS=13;PENDING=8` is context, not proof of a specific root cause.
- Run `37489076324`, Windows job `112356698549`, full-suite iteration 2:
  VLESS/gRPC client port reservation exhausted its 32 attempts. The old report
  did not retain the role, socket phase or native error. Its cause remains unknown.

## Implementation

`AvailablePipeReadStream` still has exactly one reader per fresh owned handle,
a maximum native read of 4096 available bytes, explicit idle versus EOF, and the
same 10 ms polling interval. Its idle wait now uses a per-wait timer and private
completion signal. Requested cancellation can unwind inline instead of requiring
a newly scheduled worker merely to observe cancellation of `Task.Delay`.
Both timer and token registration are disposed; a late timer can only signal its
own completed wait, never read a process handle or caller buffer.

Inline continuation is deliberate and includes the usual possibility of caller
reentrancy. `ProbeWorker` publishes one cleanup task under its admission lock,
then runs cleanup outside that lock. A cancellation callback that requests cleanup
again joins that already published task. Faulted cleanup remains observable and
retains the existing explicit retry semantics; no failure is converted to success.

The fresh-reader adaptation is shared with the pinned-core validator and the
controlled native server fixture. Both drain actual EOF; validator counts remain
64-bit, and the controlled server retains its same 4096-character diagnostic tail.
The server's retained process/reader lifetime now extends through the existing
five-second drain join. Parent readers in the worker-pool controls use the same
adapter with bounded synthetic output. No quiet Windows pipe is deliberately
read through the old blocking path except the existing negative control.

`CorePortLease` adds an immutable, at-most-32-attempt diagnostic report: controller,
SOCKS or unknown role; UDP/TCP creation-or-options, bind or TCP listen phase;
`SocketErrorCode` and `NativeErrorCode`. Equal errors are grouped in the bounded
summary. No attempted port, endpoint, credentials, path or native exception text
is included. `NonTunCoreProbeTransport.LastDiagnostic` retains that report; its
public reason remains exactly `CORE_PORT_UNAVAILABLE`.
The candidate range, random odd stride, 32-attempt budget, UDP/TCP exclusivity,
release order and retryable error set are unchanged. The handoff to the child is
still non-atomic and readiness still requires the exact process's port ownership.

## Focused regression design

Four new controls run in finite, separate processes. Only those children cap
their worker pool at two threads, then occupy both workers before starting a read.
The runner and product pool configuration is unchanged. The main thread calls
cancellation and observes the result against the same one-second budget while
the workers remain occupied.

1. Frozen previous `Task.Delay(10, token)` mechanism: it must remain pending until
   workers are released, then complete as canceled. This is a negative control,
   not a retry or replacement of the production adapter.
2. Production managed idle stream: cancellation must complete before release and
   permit the next read after its reader fence is released.
3. Production managed drain: same condition, with explicit `Canceled` and zero
   characters, never fabricated EOF.
4. Actual Windows stdout and stderr: both drains must cancel while the owned
   child is still alive and both workers remain occupied. The child and readers
   must subsequently be joined and cleaned up.

The control JSON separately records observations before worker release and cleanup
afterward. The parent checks exact outcomes, bounded output and complete cleanup.
The old one-second managed cancellation test is retained unchanged.

Additional cases check validator pipes under the existing isolated worker-pool
fixture, reentrant cleanup admission, and local port diagnostics. The total is
892 cases: 866 previous plus 26 new. Exact full-suite skips are 17 on Linux and
14 on Windows; the two added Linux skips require actual Windows pipe handles.
The six complete regression iterations and every failure are retained as before.

## Evidence boundaries and next action

The code and .NET source identify a worker-queue dependency; only the controlled
tests can confirm its observed effect. They do not retroactively prove the cause
of every earlier timeout. A synchronous Win32 pipe handle is not a universal
nonblocking I/O guarantee; the adapter relies on its exclusive fresh-reader
contract and still keeps read faults and cleanup deadlines observable.
Custom synchronization contexts and unrelated blocked native calls are not
covered by a universal real-time cancellation claim.

The validator and controlled-server fixture retain a pre-existing exceptional-path
gap: if their final drain join itself times out, their outer disposal can still
close the original process reader while that drain is pending. Normal-path
adaptation does not establish acceptance of that failure path. It needs separate
forced-failure lifecycle coverage before treating those helpers as fully accepted
resource supervisors. The production probe worker is different: its pending
drain failure retains resources and the cleanup obligation for an explicit retry;
the existing failure/retry tests remain required.

Primary implementation reference: [.NET 10 Task.Delay cancellation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs).
Platform boundary: [Microsoft PeekNamedPipe documentation](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-peeknamedpipe).

No changes to pinned SDK/core/profile generator, target TLS/certificate policy,
wrong-credential assertions, readiness gates, probe/startup/shutdown deadlines,
or installed-service privileges are included. The existing standard-user Windows
six-protocol runtime and status-service jobs remain required.

After exact-source CI, record both passing and failing results here and in a
machine-readable evidence file. If a port failure recurs, use its new phase/code
report for the next bounded investigation. Selected-node service handoff,
production SYSTEM runtime, TUN/WFP/DNS/IPv6 protection, network recovery,
installer and Windows 11 product acceptance remain outside this slice.
