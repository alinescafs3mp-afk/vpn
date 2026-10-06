# Runtime preparation checkpoint (2026-10-06)

Base: 28b3d8354537d452032da1432201d06d4af8409c, published V3G.
Tested code: c98da7aadb1d13ac5517fd194474075351809844.
Tested tree: b1ee277afbdf6bd371b264878164ace01b805edc.
Status: CROSS_PLATFORM_REGRESSION_AND_EXISTING_STATUS_SERVICE_PASSED.
This is an independent runtime correction, not a cumulative V3I release.
Assembly version stays 0.1.7. The unpublished V3H package is NOT integrated here.
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

## Completed validation

Run 37461124888, attempt 1, on the exact tested commit above. All three jobs passed.
The full solution built successfully on both platforms. Warning counts were not
independently inspected, so this checkpoint makes no zero-warning claim.

| Job | Complete runs | Cases per run | Passed total | Failed | Explicit platform skips |
|---|---:|---:|---:|---:|---:|
| Linux 112260663694 | 6 | 619 | 3624 | 0 | 90 |
| Windows Server 2025 112260664079 | 6 | 619 | 3684 | 0 | 30 |

Every iteration recorded exit=0 and valid=True. The checked-in script checks TRX
case count, unique IDs, outcomes and exact skip allowlists. Counts above were read
from completed CI job logs; raw TRX were not independently recounted locally.
Native pinned Mihomo and TLS diagnostics were enabled. No retry-to-green occurred.
20 new cases: 17 cross-platform, one Windows-only bounded-pool control using the
actual runtime reader, and two Linux-only official non-TUN core lifecycle cases.
Linux skips 15 Windows-specific cases; Windows skips 5 Linux-specific cases.
The two added Windows skips do not replace or disable any previously running case.
The native race case allows cancellation before spawn; it is not proof that a
process necessarily started in every race. The active-core case proves a real spawn.
The Windows reader control exercises the production read helper with a synthetic
quiet child, not the entire Mihomo runtime under a privileged Windows account.

Installed status-service job 112260664046 passed on Windows Server 2025:
authorized standard user Ready; outsider AccessDenied; forbidden process rights
denied; malformed, oversized, idle and unsupported requests refused; SCM stop,
restart with a new instance, remove and owned-installation cleanup confirmed.
This is the existing read-only status service, not V3H node handoff or VPN execution.

Raw source snapshots, manifests, build/test logs and TRX are retained in CI artifacts.
See ../evidence/RUNTIME_PREPARATION_VALIDATION.json for artifact IDs and hashes.
Local container execution remained unavailable (ClientError). No local build,
independent archive validation or new Windows binary package is claimed.

## Still separate

No installed-service code, pipe ACL, permissions, configuration generator, TLS/HTTP
validation, binary pins, SDK, TUN, routes, firewall or system DNS is changed.
This does not grant elevated execution to MihomoRuntimeProcess.
The original V3H source package remains separate and unintegrated; its declined
publication was not retried or routed through another operation.
The V3H Windows handoff and WPF gates remain NOT_RUN.
Its original patch overlaps this increment only in scripts/test-v3f.ps1;
reconcile the accounting instead of replacing either test set (731 combined cases,
15 Windows-specific skips on Linux and 5 Linux-specific skips on Windows,
subject to actual discovery and future validation).
Do not execute V3H's staged draft with placeholder ports, and do not remove the
privileged/TUN refusal merely to connect it to this runtime.
