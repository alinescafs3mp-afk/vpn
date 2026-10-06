# Runtime preparation checkpoint (2026-10-06)

Base: 28b3d8354537d452032da1432201d06d4af8409c, published V3G.
This is an independent runtime correction, not a cumulative V3I release.
Assembly version stays 0.1.7. The unpublished V3H package is NOT integrated here.
Its declined publication is not retried, encoded, or routed through another operation.
Only main is used; no new branches and no force-push.

## Scope

MihomoRuntimeProcess still used its original raw StreamReader loop although V3G
had fixed the probe worker. It now calls the same exclusive available-byte reader,
with a fixed sanitized error on every non-EOF result. There is no raw output retention.
The adapter's V3G restrictions still apply: fresh owned pipe, exactly one reader;
PeekNamedPipe is not advertised as universally nonblocking for arbitrary handles.
Reference: https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-peeknamedpipe

The runtime itself now fences Stop-before-Start. Start and Stop publish owned tasks
under one gate. Stop cancels and joins startup before touching its resources;
canceling a waiter does not abandon cleanup. Concurrent stops share one task.
Only a faulted/canceled cleanup may be retried. Once stopped, this runtime cannot
be restarted; create a new instance through the existing supervisor.
The existing eight-second stop deadline is retained and also covers the startup join.
An unconfirmed startup/exit/output completion retains the cleanup obligation.
A terminal output read fault is still a fail-closed cleanup error, not a clean exit.

## Validation plan and initial status

Local container execution: unavailable (ClientError); no local build/test claim.
20 new cases: 17 cross-platform, one Windows-only bounded-pool control using the
actual runtime reader, and two Linux-only official non-TUN core lifecycle cases.
Total published-source suite: 619. Exact permitted skips: Linux 15; Windows 5.
The two added Windows skips do not replace or disable any previously running case.
The native race case allows cancellation before spawn; it is not proof that a
process necessarily started in every race. The active-core case proves a real spawn.
CI results are PENDING until read from a completed run tied to the tested commit.

## Still separate

No installed-service code, pipe ACL, permissions, configuration generator, TLS/HTTP
validation, binary pins, SDK, TUN, routes, firewall or system DNS is changed.
This does not grant elevated execution to MihomoRuntimeProcess.
The V3H node handoff and its Windows/WPF checks remain unintegrated and unaccepted.
Its exact original patch overlaps this increment only in scripts/test-v3f.ps1;
reconcile the accounting instead of replacing either test set (731 combined cases,
15 Linux-only-platform skips and 5 Windows-platform skips, subject to actual discovery).
Do not execute V3H's staged draft with placeholder ports, and do not remove the
privileged/TUN refusal merely to connect it to this runtime.
