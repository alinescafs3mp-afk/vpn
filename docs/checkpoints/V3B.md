# AutoVPN V3B: live catalogue and owned replacement

Date: 2026-10-04. Canonical V2 ancestor: `70f9546d81ac58845c174d5dd4669dcd1fe27a5f`.
Continues the delivered V3 source candidate without deleting any V2/V3 source path. Assembly version remains 0.1.3; this is not a new finished VPN release.

## Implemented

- `SqliteCatalogue.OpenReadOnly` and `IRefreshableCatalogue`: a same-user broker refreshes committed revision changes through a separate query-only connection. Loads are transactional and publish atomically. Missing/corrupt/future-schema stores are refused, never created or quarantined by this reader. Decrypted semantics are reused only for identical protected blobs. A node-id dictionary avoids quadratic membership reconstruction. Active retention is ephemeral in the reader; the desktop remains the persistent writer. Existing writable optimistic-conflict semantics remain binding.
- `BrokerEngine` replacement stops every prior owned runtime before admitting the replacement. New ownership is reserved under the broker lock and rechecked before and after asynchronous start. Revocation, changed epoch/digest/target set/policy, cancellation and Disconnect invalidate admission. Uncertain cleanup is retained and retryable; no simultaneous runtimes or false Connected transition.
- `BrokerSafetyMonitor` owns one serial local observation task. Revocation, changed policy/epoch or unavailable catalogue invalidates operation authority and stops owned processes without disarming an already armed guard. Explicit Disconnect remains the network-cleanup authority. No extra public probes, automatic reconnect or DIRECT fallback is introduced.
- Desktop: bounded status polling with cancellation/join on exit, accepted-snapshot-based active retention, explicit Disconnect on consent revocation, up to three automatic attempts only after a validated side-effect-free STALE_REVISION rejection. No retry after ambiguous timeout, connection loss, BUSY or CLEANUP_UNCERTAIN.
- Disabled source families are excluded before scheduling and before subsequent request/publication boundaries. S603 is no longer filtered out; two additional late-response/no-refetch tests cover mid-download disable.

## Evidence and adverse results

The isolated SDK 10.0.112, restored packages and pinned Linux Mihomo were provisioned through GitHub Actions, transferred as hash-checked chunks, and used offline on Linux. The unchanged original V3 candidate compiled with zero warnings/errors. A first broad invocation lacked the native environment and included the formerly excluded S603: 16 native precondition failures, the S603 failure and four explicit skips were retained. This was not a properly provisioned native run. In the working source S603 is fixed, not excluded.

New test authoring initially used a nonexistent enum member and triggered two xUnit analyzer errors. The first compiled new-test run then exposed an inappropriate reference-based record/list equality assertion. These were corrected without weakening runtime checks; failing logs/TRX are retained.

At the pre-publication source state: complete solution build, zero warnings/errors; provisioned Linux native-inclusive suite 404/404 passed, no skips. This comprises the original 348 cases plus 56 new cases. Tests exercise controlled fake-process lifecycle interleavings and real SQLite connections; the existing explicit native cases use the pinned official core against loopback fixtures. No public subscription node was measured. Exact remote commit and Windows results must be read from delivery evidence, never inferred from this document or unrelated V2 CI.

Windows WPF smoke additionally checks real current-user DPAPI with two connections, live assessment refresh, ephemeral active state, persistent read-only refusal and absence of the synthetic credential in database bytes. This remains a single-process connection test, not a privileged/cross-user or installed-service proof.

## Open boundaries

The production guard and TUN refusal remain. The runtime is unprivileged non-TUN, and the service entry point is a same-user console host, not SCM installation. No authenticated privileged handoff, WFP/watchdog, isolated DNS/IPv6, reconnect verifier, installer, forced-termination recovery, reboot/sleep/upgrade matrix or production long-run soak is claimed.

The read view is not an IPC authority certificate. Revision/schema checking is not protection against a malicious same-user file editor or file-identity replacement. Large-catalogue writer changes can still cause complete reader snapshot reconstruction, though repeated decryptions and quadratic membership scans are avoided. No 10,000-node throughput/latency guarantee is made.

## Next bounded implementation

Continue from this exact source, not the historical V3 NOT_BUILT note. Implement the narrow installed Windows lifecycle and independently validated selected-node handoff together with owned process exit/recovery boundaries. Do not merely enable TUN, run the unprivileged runtime as SYSTEM, add DIRECT fallback, trust UI-generated health in a privileged process, or restart a general audit loop. Grok remains BUILD/VERIFY/PACKAGE only; implementation stays with Astra.
