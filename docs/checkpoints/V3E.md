# V3E: main-only history and resource lifecycle

2026-10-05. Version 0.1.5. Development candidate. Windows full-suite acceptance is NOT CLEAR.

## Resume here

All work goes directly to main; do not create branches. Tested application and test commit: `5599567c1827780eac3eff858689aa455f43ad50`, tree `c2ecb42c4a038d64b1dfb35eac8fa719ce114f32`. Documentation/delivery-only follow-ups must be checked against this runtime source. Final repeated series: run `37356265309`, attempt 1. First failed series: `37355416544`.

## Consolidation completed

Commit `cdebddf1a2fd2ce811fb12e46a626c7eb41cfe57` retains every former branch tip as an ancestor. Seventeen non-main branch refs were deleted atomically with leases for their exact reviewed tips. Main was advanced normally, not force-rewritten. The independently verified pre-deletion bundle is in backup run `37353615748`, artifact `11363383396`; bundle SHA256 `06272071a0d24e1e944fe7cd5a665536769483ac4a0b532bb615af0e50a13c3c`.

`docs/branch-archive/inventory.json` maps the unique reviewable files to a dormant byte-exact archive. Historical opaque transfer chunks remain in history rather than active source. Do not rerun one-shot consolidation/integration scripts. Archived workflows are not active workflows.

## Actively reused work

From alternative V3D `0dd9f78da000642e0b4d24dbbdb5a3ca31b21e80`: ProcessHandleLiveness, InstalledServiceClient, InstalledServiceProtocol and 12 service cases. Process liveness uses the retained SYNCHRONIZE handle instead of ambiguous exit code 259. SCM transitional state reporting and post-exchange configuration recheck are included. Three native handle cases run only on Windows. No installed-service claim follows from these tests.

Old LiveCoreSession permits explicitly unprotected TUN and controller-only readiness; it must not replace the current reviewed broker. Its WindowsProcessJob helper remains an archived reuse candidate. The old ProbeWorker/Socks5 split is not applied over current V3D readiness/LISTEN fixes. The archive preserves those alternatives without enabling them.

## Current implementation

CorePortLease reserves exclusive IPv4 loopback UDP and TCP together, trying at most 32 distinct dispersed high-port candidates. Each failure disposes partial reservations. The lease is held during configuration writing, released immediately before child bind; the release-to-bind gap is not eliminated. It is used by production non-TUN probes, the controlled Round6 peer and held-provider regression.

ProbeWorker cleanup callers join a single attempt, and failed cleanup can be retried on the same object. The retained owned process is awaited before releasing output resources and files. Directory deletion retries only Windows sharing/lock violations for at most one second after confirmed parent exit. Persistent cleanup failure is reported rather than returning a successful probe. The outer probe does not independently delete after the worker takes ownership. Pre-canceled requests cannot spawn.

TLS/HTTP exchange bytes, certificate policy, pinned core, SDK and MihomoProfileGenerator are unchanged. Normal runtime validation has read-only repository permissions; the one-time authoring integration is no longer a job in v3e-ci.yml.

## Exact observed outcomes

Six full runs per OS, 511 cases each, unchanged suite concurrency. Linux: 3042 passed, 0 failed, 24 named Windows-only skips. Windows Server: 3044 passed, 4 failed, 18 named Linux-only skips. Both builds completed successfully. Source had 311 tracked files, including dormant archive. Count is source packaging metadata, not a feature metric.

The Windows failures occurred in runs 2 and 6, once each in:
- AstraR1ContractTests.OwnedManagedChildFloodsBothPipesAndCanBeStoppedWithoutSiblingKill: CORE_CLEANUP_REQUIRED, precise stage not retained by this test.
- AstraV3EResourceTests.CleanupDoesNotDeleteAnotherWorkersDirectory: OUTPUT_DRAIN_FAILED, after owned-process stop stage.

No port allocation or native cache-file deletion failure occurred in this last finite series. First-series failures remain preserved in V3E_FIRST_SERIES.md and their original artifacts. Do not treat a later pass as an explanation of a prior failure.

## Next bounded implementation slice

Investigate and fix owned stdout/stderr reader completion on Windows while a sibling process stays alive. Retain process and reader states, exception type, timing and ownership without logging credentials. Distinguish reader cancellation, EOF, inherited pipe handles and scheduling before choosing a change. Preserve the sibling-survival assertion. Do not simply enlarge the one-second output timeout, swallow errors, disable tests or remove failed outcomes. Add a deterministic regression where possible, then run the complete native suite.

Generic descendant exit is not attested by waiting on the parent. OS-denied termination needs explicit higher-level recovery, not a false cleanup success. Installed SCM lifecycle, SYSTEM/user authorization, privileged node handoff, TUN, WFP/watchdog, DNS/IPv6 isolation, recovery, installer/update/remove/reboot/sleep and soak remain open. Grok stays BUILD / VERIFY / PACKAGE only. Main stores development progress; this checkpoint does not certify a released VPN.
