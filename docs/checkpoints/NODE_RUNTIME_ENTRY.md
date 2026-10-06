# Owned node runtime entry — 6 October 2026

This increment continues the published V3G / assembly 0.1.7 implementation.
It does not integrate the unpublished V3H candidate. The migration checkpoint
`CHAT_TRANSFER_2026-10-06.md` retains owner instructions and earlier evidence.

## Scope and API

The application can now create `RuntimeNodeSelection.Create(NodeSemantics)` and
pass that immutable selection to `OwnedCoreSupervisor.StartNodeAsync(selection,
generation, operationId, token)`. The supervisor admits one operation from its
original canonical digest. Duplicate waiters share its task; a different node
cannot take over the same owner. The backend must implement
`IOwnedNodeCoreProcess`; no raw-profile fallback is synthesized for an old backend.

`MihomoRuntimeProcess.StartNodeAsync` performs endpoint resolution, exclusive
TCP/UDP loopback port reservations, random controller-secret generation, trusted
profile construction, binary-hash verification, private file creation and native
startup inside the same retained task. The existing startup limit now covers
preparation as well as native readiness. Stop cancels and joins that task before
disposing any process, file or binary handle; the existing eight-second cleanup
limit remains unchanged. A late uncancelable OS DNS result retains its resolver
slot until completion, but cannot create a process after the owned start is over.

Only the execution copy receives a permitted numeric endpoint. Original identity,
TLS server name and supported transport names retain their established meaning.
Both port leases remain held through configuration and binary preparation and
are released immediately before native start. The handoff is not atomic, so
readiness still verifies the exact process's ownership of both TCP listeners and
an authenticated controller response for the pinned core version.

The existing YAML generator remains byte-for-byte pinned. The new closed selection
is its input; the permissive nested proxy parsing in `RuntimeProfileContract` is
not used as a validator for untrusted imported nodes.

## Accepted subset

| Protocol | Security / credentials | Transports in this increment |
|---|---|---|
| VLESS | TLS, UUID, encryption `none` | TCP, WebSocket, gRPC |
| VMess | TLS, UUID, alterId 0; auto / aes-128-gcm / chacha20-poly1305 | TCP, WebSocket |
| Trojan | TLS and password | TCP |
| Shadowsocks | aes-128-gcm / aes-256-gcm / chacha20-ietf-poly1305 and password | Native, no plugin |
| Hysteria2 | TLS and password | Native |
| TUIC | TLS, UUID and password; reviewed bbr/cubic and native/quic options | Native |

Unsupported field combinations are rejected explicitly. This first entry does
not accept Reality, fingerprints/flow, external plugins, obfuscation, port hopping,
bandwidth hints or unreviewed transport combinations. This restriction belongs
to the new typed runtime entry; the historical import and compatibility records
are not rewritten into an assertion that these features do not exist.

Every supplied string is checked before normalization, JSON or canonical hashing.
Unpaired UTF-16 surrogates, controls and incompatible YAML separator characters
are rejected with bounded reason codes. Valid surrogate pairs are preserved as
UTF-8, including non-BMP characters in opaque passwords and paths. Opaque values
are not trimmed or Unicode-normalized. A bounded ALPN snapshot prevents a caller
from modifying the request after admission. Public JSON and ToString expose only
the digest, with no credential snapshot or inner exception text.

## Validation plan and evidence state

Initial candidate state: **VALIDATION_PENDING**. No local SDK or PowerShell is
available on the implementation host; actual builds and native execution must be
established by the retained GitHub Actions artifacts before acceptance.

First candidate `483d758df39915c82f0c0709bef060e6981d0aad`, run `37488283718`,
passed all six Windows full suites and the six-protocol real standard-user lab.
Linux iteration 6 failed `RuntimeStartupDeadlineIncludesDnsPreparation`: expected
`CORE_START_TIMEOUT`, observed `ENDPOINT_DNS_TIMEOUT`. The resolver and owner both
had the same duration and their timer callbacks could complete in either order.
The correction maps resolver expiry of the full startup budget to the startup
timeout code, preserving cancellation precedence, time limits and the original
test assertion. This first run remains failed evidence; validation of the corrected
source is pending a new commit/run, not a rerun that overwrites the first outcome.

Expected full-suite discovery: 866 unique cases, comprising the previous 741,
110 selection-validation cases and 15 owned-entry cases. The exact count, identities
and platform skip names are checked for all six planned runs on each platform.
No previous failed run is replaced by retrying a suite until green.

The new native fixture uses synthetic VLESS/gRPC, VMess/WebSocket, Trojan,
Shadowsocks, Hysteria2 and TUIC selections. Its assertions cover exact port owner,
UTF-8 and parsed-scalar preservation, unauthenticated controller rejection,
authenticated pinned version, retained process-handle exit, private directory
removal, TCP/UDP port rebinding and sealed admission after Stop. Test requests go
only to the local controller; no remote proxy handshake or throughput is claimed.

The ordinary Windows regression runner keeps its privilege-related skips.
A separate disposable Windows Server job creates a real standard-user primary
process token, verifies its SID and nonzero session, and attaches a private
kill-on-close Job Object before releasing the fixture's stdin gate. Successful
evidence requires natural process exit and an empty job before forced cleanup,
then removal of owned files, profile and account. An impersonated thread is not
accepted as evidence of an unprivileged native process. Production runtime now
explicitly rejects impersonation as well as elevated/service/session-zero use.

## Boundaries and next work

The broker's existing Connect path still requests TUN and is not redirected into
this unprivileged entry. Installed service transport, SYSTEM runtime execution,
WFP protection, system DNS/IPv6 policy, recovery and product installation remain
unaccepted. Neither local startup nor Windows Server CI grants Windows 11 desktop
or protected VPN acceptance. No public catalogue node is marked measured or
connected by this increment.

The earlier intermittent VLESS cleanup failure from run 37468579830 remains
historical unresolved evidence. Preserved cleanup diagnostics can identify a
future recurrence; a green later suite is not its causal explanation.

Next: use the validated node/runtime contract when reconciling the explicit
selected-node service handoff, retaining the installed protection and recovery
gates before enabling SYSTEM or TUN. Do not replay the previously declined V3H
publication through a different tool or workflow.
