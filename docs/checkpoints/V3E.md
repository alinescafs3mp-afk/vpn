# V3E: main-only consolidation and owned resource lifecycle

Development checkpoint, 2026-10-05. Validation is pending until exact run outcomes are recorded. This is not a working installed VPN.

## Branch consolidation

The owner requested one branch only. Consolidation commit `cdebddf1a2fd2ce811fb12e46a626c7eb41cfe57` retains every former tip as an ancestor of main. All 17 other branch names were deleted atomically with explicit expected-tip leases. No main history was rewritten. The independently verified Git bundle is retained in run `37353615748`, artifact `11363383396`, and was downloaded into the conversation. Its bundle SHA256 is `06272071a0d24e1e944fe7cd5a665536769483ac4a0b532bb615af0e50a13c3c`.

`docs/branch-archive/inventory.json` maps unique reviewable files into a dormant archive. Older audits and alternate runtime implementations are preserved without blindly overwriting newer code. Historical transfer chunks remain recoverable from history but are not copied back into the active working tree. No new branches should be created.

## Useful active migration

From alternative V3D `0dd9f78da000642e0b4d24dbbdb5a3ca31b21e80`: retained Windows process-handle liveness, accurate SCM transitional states, post-exchange service-configuration recheck, and nine state tests plus three Windows-native handle tests. The fixture gains an exit-259 mode. The service is still status-only and not installed by this work.

The alternative V3 live-session implementation and Windows Job Object helper remain reviewable reuse candidates in the archive. Its explicitly unprotected TUN and controller-only readiness must not be mistaken for a completed protected VPN or override the current broker. The alternate ProbeWorker/Socks5 split is not overlaid on the V3D TLS fix.

## V3E implementation

- `CorePortLease` binds UDP first and TCP on the same loopback port, holding both during configuration writing. Both halves are disposed on failed reservations. Only local bind collisions are retried, with 32 attempts maximum. The release-before-child-bind gap is explicitly not eliminated.
- Production non-TUN probes, the controlled Round6 server and the held-provider fixture use the joint lease.
- ProbeWorker cleanup callers join one attempt. A failed attempt remains visible and may be retried on the same object. A retained process is awaited before output resources or files are released. Windows sharing/lock violations may be retried for at most one second after confirmed process exit; persistent failures remain failures.
- A failed cleanup cannot return an authenticated successful probe. The outer probe scope does not independently delete files after the worker takes ownership. Pre-canceled requests do not spawn a child.
- TLS/HTTP exchange source, certificate policy, core pin, SDK and generated profile implementation are unchanged.

Implementation uses a small exact source refactor in CI because the local authoring runtime is unavailable. The resulting ordinary main commit, not its preparation parent, is the validation source. Every replacement is checked against exact anchors and the original blob; TLS exchange bytes are compared before publication.

## Validation plan and limitations

Six predetermined complete runs on Linux and Windows Server, 511 cases per run: 486 V3D cases + 12 migrated service cases + 13 new resource cases. Three old Linux-specific cases are skipped on Windows; three native handle cases and one Windows file-sharing case are skipped on Linux. All other failures fail the series. No retry-to-green.

Tests cover joint binding, local bind conflict cleanup, lease ownership, cancellation before spawn, a real pinned native worker, concurrent disposal, failed spawn, another worker remaining alive, and retry after a held cache file is released. Full TRX, build logs, canonical source ZIP, manifest and patch are retained.

Not claimed: every historical Windows timing failure explained; generic descendant process exit (WaitForExit tracks the parent); autonomous recovery after an OS-denied process termination; installed SCM lifecycle; privileged node handoff; TUN; WFP; DNS/IPv6 isolation; rollback/reboot/sleep. Grok remains BUILD/VERIFY/PACKAGE only. Source development continues directly on main.
