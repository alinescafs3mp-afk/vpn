# V3D: non-TUN probe startup readiness

Date: 2026-10-05. Source development candidate, not an installed VPN.

## Continuation

Base V3C: `120bc69508340bbc1da2666a2a6ab05d5d20755e`. Branch: `implementation/astra-v3d-tls`. Final application and test code: `63fb4c96afb8d73df8b5c7a306e2cfe142ad32be`. Final repeated run: `37344416073`, attempt 1. Actual outcomes are mechanically recounted from retained TRX into the delivered `DELIVERY-VALIDATION.json`; packaging success is not a passing suite.

Main stays on V3B `7d99416c01499ed69593f4c948103cf0feb0d8c0`. No installed service or new Windows binary. Assembly version remains V3C `0.1.4`. Delivery-only commits are checked for no difference in application code, tests, dependencies and existing scripts from the tested commit.

## Confirmed defect

The old probe gate checked whether the owned process had a listening SOCKS port. In pinned Mihomo commit `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`, `hub/executor/executor.go` creates listeners before loading providers and calling `tunnel.OnRunning()`. In `tunnel/tunnel.go`, `isHandle` rejects ordinary traffic before Running and `handleTCPConn` closes such connections. Successful SOCKS negotiation and a listener PID do not prove normal dispatch readiness. An early authenticated HTTPS request can fail immediately with EOF and be reported as `TLS_REJECTED`.

The external controller starts earlier too; `/version` does not prove data-plane readiness.

A controlled regression holds a local HTTP rule-provider response during startup. It verifies that the legacy listener predicate is true while valid trusted TLS fails, that the new local proof remains false, and that releasing the provider makes the new proof and the same TLS target succeed, on the same core and certificate. The corrected proof passed 8/8 on both operating systems in run `37339960790`.

This establishes a concrete cause, not proof that every historical failure has that cause.

## Changes

`LoopbackCoreReadiness` binds its own ephemeral IPv4 loopback endpoint and adds one exact TCP / destination-port / 127.0.0.1/32 no-resolve DIRECT rule to the generated non-TUN probe profile. It verifies a fresh 32-byte HMAC challenge through the owned core's SOCKS listener. No public readiness target, general DIRECT fallback, additional controller, certificate relaxation or TLS retry is added.

`NonTunCoreProbeTransport` requires the proof within its remaining original startup budget, before its single authenticated candidate exchange. Local startup failure is `CoreFailure / CORE_NOT_READY`, not a failed remote candidate. Latency excludes local startup. Linux port ownership lookup now requires LISTEN state rather than accepting an established socket with the same local port. Windows ownership lookup is unchanged.

The controlled Round6 peer is another Mihomo process with the same startup ordering. After two Windows TLS failures remained with the client-side fix alone, that peer also received an owned loopback data-plane gate before protocol checks. Candidate TLS, wrong-password and wrong-UUID assertions remain unchanged. Both fixture port reservations are held through configuration writing; the unavoidable release-before-child-bind gap is not claimed eliminated.

The helper rejects malformed SOCKS replies, zero/echo proofs, missing ownership, invalid rule sections, cancellation and disposal. A Windows `ObjectDisposedException` was traced to the new test fixture closing its listener concurrently with AcceptAsync. Cancellation now joins the owned loop before closing its listener; the helper and controlled provider use the same ordering.

The whole existing TLS/HTTP implementation (`TlsProbeExchange` / `Socks5Client`), core manifest, SDK pin and `MihomoProfileGenerator` are unchanged from V3C. `scripts/v3d-source-snapshot.py` verifies that boundary, canonical Git blob hashes and source ZIP contents. Existing worker cleanup and SYSTEM trust boundaries are not approved by this change.

## Retained runs

| Run | Scope and observed result |
|---|---|
| 37336477856 | 20 isolated rounds of six original protocols per OS; all passed, isolation did not reproduce the full-suite fault. |
| 37337183746 | Pre-fix 12 x 469 cases per OS: Linux 3 failed cases, Windows 16 including separate IPC and catalogue timing failures. |
| 37338889327 | First controlled fixture: 7/8 passed per OS; provider followed MATCH,REJECT and never reached the local server. Fixture authoring error. |
| 37339960790 | Corrected loopback provider transport; deterministic startup/TLS proof 8/8 per OS. |
| 37340995123 | First integration: Linux 12 x 486 passed. Windows stopped before build/tests on exact ZIP-byte mismatch caused by Git EOL conversion. |
| 37341316366 | Canonical Git-blob packaging: Linux 5832 passed; Windows 5791 passed, 5 failed, 36 platform skips. Original protocols passed, but new fixture startup/disposal failures remained. |
| 37342345749 | Read original Windows failure stacks, without a rerun. |
| 37343244080 | Cancel/join/close correction: Linux 5832 passed. Windows 5790 passed, 6 failed, 36 platform skips, including two original TLS failures. This is not a green result. |
| 37344416073 | Final series after adding the controlled peer readiness gate. Consult exact delivered outcome JSON and original TRX. |

There are 486 cases: 463 V3C cases, 17 new startup/safety cases, and six diagnostic wrappers repeating original protocols. Repetitions are not additional unique tests. Windows permits only the three named existing Linux-specific skips. EventListener diagnostics are process-wide observations, not guaranteed request correlation.

## Remaining investigations

Old IPC/fetch/budget timing assertions and catalogue lifecycle failures are not declared fixed merely because a later run passes. A controlled fixture failed at the legacy `worker.Ready` startup check before TLS in two integrated series; its cause is unresolved. Port reservation lifetime was improved and error messages made explicit, without increasing its startup timeout or weakening the ownership predicate. Windows TCP-table resizing is a possible hardening topic, not a demonstrated cause of this incident. Preserve any remaining final-series failures before promotion.

All traffic is to controlled loopback peers. No public subscription servers were evaluated. Windows CI is Windows Server, not Windows 11 desktop acceptance. The local authoring runtime was unavailable; builds, native tests and package verification were performed in GitHub Actions, not locally.

SCM installation, SYSTEM/user authorization, privileged node handoff, TUN, WFP/watchdog, DNS/IPv6 isolation, recovery, installer/update/removal/reboot/sleep and soak remain open. Astra continues implementation. Grok remains BUILD / VERIFY / PACKAGE only. Next: resolve remaining reported Windows failures, then a separately scoped installed-service lifecycle and node-handoff slice.
