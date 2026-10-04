# V3C: status-only Windows service candidate

Base: `7d99416c01499ed69593f4c948103cf0feb0d8c0`, tree
`5f72a5df2c73865cbf5ea8db5d8f17ef046facae`. Assembly 0.1.4.

## Implemented source

A separate `--service` path registers SCM callbacks, reports monotonic lifecycle
states, opens a bounded status endpoint, signals stop outside blocking callbacks
and joins the listener. It never opens the user catalogue or launches a core.
The development console broker refuses built-in service identities.

The status client checks SCM configuration and running PID, pipe server PID and
process image before sending a request, retains a process handle and rechecks
state after the reply. Its reply checks correlate a fresh request ID and require
explicit false networking/protection fields. A read-only `--service-status` CLI
and a separate WPF settings button expose this status without changing the
legacy broker connection state. The WPF label uses the assembly version; initial
maintenance captions respect absent consent.

Frames are at most 4096 bytes, exact-length reads, strict JSON, required fields,
duplicate-field rejection and bounded depth. Only GetStatus is supported. The
listener has a protected per-owner pipe ACL, rejects network identities and
retains its first pipe instance across clients. The installation layout uses a
fixed Program Files path and rejects reparse points and broadly writable entries.
These are implementation properties, not proof of installed Windows security.

## Corrections and test scope

The final Linux native regression has 463 cases, 59 more than V3B, with no failures
or skips in the retained local run. Initial 456-case run preceded seven malformed
client/authorization isolation controls. Review found that InvalidDataException
was not covered by an IOException-only client refusal path. The exchange now
contains malformed/truncated/cancelled input and is exercised independently from
SCM using controlled streams; this does not test Windows pipe ACLs.

The status-only CI builds/tests on Linux and Windows and checks the absent-service
CLI and WPF button on a fresh Windows runner. It does not install a service,
create user accounts, alter ACLs or change system networking. Read its exact run
results before claiming a CI pass. Local cross-compilation is not Windows runtime
validation. Explicit platform-specific skips must remain visible.

## Publication boundary

A tool explicitly blocked transfer of the installation script. That action was
not retried via encoding or alternate transport. Installation, removal, account
provisioning and their test scripts are NOT part of this source increment.
Only independently permitted status/lifecycle source and non-installing tests
are published to the candidate branch. main remains the verified V3B checkpoint.
No privileged installation is requested through CI or supplied as a workaround.

## Open gates

Installed SCM start/stop/restart, actual SYSTEM/standard-user authentication,
pipe squatting, installation ACL enforcement and recovery: NOT_RUN.
Privileged selected-node handoff, authoritative source/probe evidence, child
process isolation, WFP/watchdog, TUN, DNS/IPv6 leakage, installer/upgrade/uninstall,
reboot/sleep and long-run soak remain OPEN. A healthy status channel must never
be substituted for an established protected VPN connection.

Continue implementation from the full V3C candidate without discarding V3B.
Do not silently transfer unfinished implementation to Grok; his role stays
BUILD / VERIFY / PACKAGE. Do not use the historical V3 packaging script for V3C:
it contains fixed V3B identifiers. This increment supplies source, not an installer.
