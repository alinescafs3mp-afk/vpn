# Astra implementation checkpoint R1

Date: 2026-10-04. Base: `cf619323be36130977dfd27832a495a442cb3b6a` (round-6 audit); production parent `bee26022245fb7fd1ede1d5edbe845db97e1205b`.

**Status: DEVELOPMENT CHECKPOINT, NOT A WORKING/RELEASE-READY WINDOWS VPN.** This is code, not a seventh audit. The original specification and audit acceptance gates still apply. Unfinished implementation is not relabeled an external test blocker.

## Implemented in this checkpoint

- One per-catalogue probe authority reserves and consumes immutable attempts across routine/on-demand checks. It lives outside copied node DTOs, rechecks digest/epoch/policy under the catalogue lock, and cannot refresh an old observation on a new epoch or after expiry. Test doubles explicitly bind context; production has no permissive fake-proof adapter.
- Durable artifact-snapshot records distinguish missing representation from a valid empty subscription, survive SQLite reopen, and bind conditional validation to the committed canonical hash/membership. SQLite row updates remain targeted. Incomplete branch resolution no longer pairs the bootstrap pin with a mutable tree. Historical raw URLs are grouped by logical artifact for scheduling.
- Core ownership is tracked separately from the selected/confirmed session, including partially failing failover starts. Disconnect retries exact outstanding handles. Freshness is evaluated at switch completion; automatic switches do not inherit a manual exclusion override. Policy is captured before profile generation/Arm and full current eligibility is checked at confirmation.
- Protocol v2 replaces the Bloom filter and retired-ID approximation with a server-issued lease and exact bounded 4096-sequence replay window. Mutations and safety commands share exact sequence authority but separate bounded result caches. The pipe dispatcher awaits asynchronous handlers instead of blocking a worker thread. A failed result remains uncertain, not a license to repeat effects.
- Windows pipe peers are identified by OS impersonation/SID and session. Explicit ACLs restrict the owner and SYSTEM and deny network logons. This closes same-owner console transport code, not the remaining installed two-account service authorization/handoff design.
- The GUI retains unresolved process/resource state and refuses a success-shaped Exit. Native configuration validation continuously drains output without retaining raw credential-bearing lines, distinguishes cancellation/timeouts and awaits child termination. Managed child-process tests run on both platforms.
- Reproducible scripts run normal/native tests, pin and verify core assets, build a self-contained DEVELOPMENT EXE ZIP, and verify all packaged file identities. `docs/GROK_BUILD_HANDOFF.md` is a build-only handoff, not a request for Grok to implement the remaining project.

## Test migration, not weakened assertions

The wire schema changed to v2. Existing in-process fixtures now use `ProtocolTestDispatcher`, which represents an explicit test client and reuses the same exact sequence for repeated IDs. New tests also exercise the production dispatcher directly, including missing/old leases, owner mismatch, version-1 rejection, out-of-order sequences, 64 asynchronously blocked mutations and 100,000-command retirement/fresh-safety behavior.

Legacy successful transport doubles now explicitly echo their supplied attempt context through `BoundTestProbeTransport`. A reused observation object retains its first context to preserve replay negatives. New direct unbound-transport tests prove plausible strings alone never publish health.

Four older positive source/cache fixtures used unrelated placeholder hashes or returned a tree document for a commit endpoint. They now provide the correct committed hash and proper commit/tree objects. Corrupt known cache is fetched unconditionally without first sending an invalid validator. Negative malformed-object/lost-cache tests remain intact.

The old 64-worker synchronous saturation fixture could starve the thread that releases its own gate. It now uses asynchronous pending handlers. Windows fixture-owned SQLite connections disable pooling and close before deliberately deleting their own files. Linux-specific shell tests are explicitly labeled; managed cross-platform output/cleanup coverage is additional, not a fabricated Windows pass.

## Remaining mandatory implementation

| Area | Checkpoint boundary |
|---|---|
| Installed SCM/TUN core and real guard | OPEN. Production still uses refusing core/guard adapters. |
| Privileged handoff and authenticated server discovery | OPEN beyond same-owner console pipe identity/ACL and v2 request ordering. |
| Asynchronous effect actor / stop-before-late-spawn guarantees | PARTIAL. Pipe dispatch is asynchronous and cleanup is scoped/bounded; blocking guard I/O and complete process-supervisor cancellation remain. |
| Windows WFP, DNS/IPv6, recovery, crashes | OPEN / NOT_RUN. No packet containment or installed recovery claims. |
| Maintained two-target catalogue, source coverage/retention/budgets | PARTIAL. Strong publication/cache boundaries exist; complete fair continuous maintenance is not yet implemented. |
| UI server selection/favorites/metrics/themes/accessibility | OPEN beyond existing UI and improved safety state. |
| Real installer / complete supply chain and signing | OPEN. The script produces a DEVELOPMENT ZIP only. |
| Performance | Targeted SQL preserved; a fresh scale/soak run is not implied by compilation. |

The supplied evidence records actual commands and outcomes. Local .NET tests do not establish the Windows networking gates. No routes, DNS, firewall rules or TUN devices on the implementation host were changed.
