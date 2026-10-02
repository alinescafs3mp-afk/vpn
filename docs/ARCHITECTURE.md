# Architecture

AutoVPN is four processes in intention, and two of them are deliberately inert on this host.

| Piece | Project | Owns |
|---|---|---|
| Desktop | `AutoVpn.Desktop` | Russian WPF window, disclosure, tray icon. Sends IPC. Does not change routes |
| Broker | `AutoVpn.Service` | One local pipe, catalogue handle, effect journal, connect/disconnect decisions |
| Core | external Mihomo | Would own TUN. The service constructs `RefusingCoreController`, so this build never starts it |
| Guard | `UnavailableNetworkGuard` | Would own WFP/DNS/route effects. It records none |

`AutoVpn.Domain` holds eligibility, ranking, failover, the tunnel reducer, and product limits. `AutoVpn.Application` holds the catalogue contract and the IPC dispatcher. It does not reference Infrastructure. Import, SQLite, fetch, probe, and profile generation live in `AutoVpn.Infrastructure`.

## Identity

A node id is opaque. The digest is SHA-256 of canonical JSON (`CanonicalizerVersion` 3). Version 3 always writes `udp` as `node.Udp ?? false`, so null and false are one identity and true is another. Versions at or below 2 still write `udp` only when it is false. Display names are not part of identity. The profile names the proxy `n` plus the ASCII letters and digits of the node id.

## Tunnel phases

`Disconnected`, `PreparingProtection`, `Connecting`, `Connected`, `Reconnecting`, `Blocked`, `RestoringNetwork`.

`Connected` is entered only by `ProductionVerified`. IPC cannot send that event. `CoreStarted` does not change the phase; the broker applies `ProtectionArmed` first, which moves `PreparingProtection` to `Connecting`. A successful arm in tests therefore stays on `Connecting` until an in-process `ConfirmProduction(true)`.

## Profile rules

Controller secret is at least 16 characters and bound to `127.0.0.1`. DNS mode is `redir-host`. UDP/TCP port 53 goes to `AUTO_SELECT` before any LAN `DIRECT` lines. The final rule is `MATCH,AUTO_SELECT`, or `MATCH,REJECT` when no node is present. There is no `MATCH,DIRECT`.

`ValidateAsync` refuses a TUN profile when the OS is not Windows, before it looks at the binary.

## Catalogue

Membership is per artifact. Deleting one artifact drops only that artifact's nodes from the family. The family remains current while another artifact still maps to it.

Empty text does not erase nodes that came from another artifact. A 304 and a failed fetch keep the previous healthy node.

SQLite schema 1. A newer or older schema is left untouched. A bad header, an unreadable file, or a failed `integrity_check` first writes `<path>.recovery-unknown`, then moves the database together with its `-wal`, `-shm`, and `-journal` sidecars to `*.quarantine-<timestamp>`. The read handle is closed before that rename. Opening a replacement database does not clear that marker, and recovery stays incomplete until owned OS state is actually reconciled. The first open also writes `<path>.journal-seen` and never deletes it. A missing journal is not a clean recovery when that presence file, the unknown marker, a quarantine file, or a sidecar remains. An unsupported schema is left untouched and does not write the unknown marker. Effect-journal connections disable pooling. An unversioned user table throws and is left in place. Recovery counts distinct removed ids; a repeated id does not close the remaining rows.

Two open catalogue handles still use a serializable transaction. A stale revision throws `CATALOGUE_CONFLICT` and does not replace the committed nodes. When node identity is unchanged — semantics JSON, artifact membership, and current or historical families — a later save updates only the mutable columns and does not call the secret protector again. A change of semantics, membership, or the node set rewrites the file. The first settings save still inserts the revision row before later updates can take the in-place path.

Production secrets use DPAPI current-user on Windows. Off Windows, `UnavailableSecretProtector` throws. The service therefore uses `MemoryCatalogue` off Windows and `SqliteCatalogue` on Windows. The Windows open path is compiled and not executed.

## IPC

Frame: 4-byte little-endian length plus UTF-8 JSON, maximum 256 KiB, protocol version 1. Unknown operations, a bad version, a conflicting replay, a remote pipe, and forbidden payload fields fail closed. Read results, safety commands, and other mutations do not share one cache. Completed mutations stay in a 256-entry window. When that window is full, the oldest completed id moves into an 8192-entry tombstone and a later replay returns `REPLAY_EXPIRED` without running the handler. There is no lifetime `REPLAY_WINDOW` stop. Sixty-four in-flight calls still return `BUSY`. Disconnect and recovery are not evicted by ordinary mutations.

`LocalIpcServer` keeps at most four live server streams. At that capacity it waits instead of counting a creation failure. Three creation failures while a slot is free stop the listener, and the service process leaves when that listener stops. `PipeOptions.CurrentUserOnly` is set. On Linux the server requires `SO_PEERCRED` to match its own effective uid. That check passed for same-user clients in the unit test. It was not verified against a second Windows user. On Windows an unverified peer is rejected; the service does not stamp a fixed `windows-user` identity.

A non-zero `ExpectedStateRevision` that does not match the broker is rejected with `STALE_REVISION`. Zero means the caller did not pin a revision. Nested forbidden fields fail closed. Cooldown and target-outage health reports do not move a connected session to `Blocked`.

The desktop keeps one session mailbox. It records the broker boot id and sequence. An older sequence on the same boot is ignored, and the same sequence with a lower revision is ignored. A new boot id replaces the session. A response with no snapshot keeps the protocol error, such as `PEER`, in the detail line. Transport loss still uses the broker-unreachable text. On load the window asks the pipe `autovpn-broker` for `GetSnapshot` before the one-minute refresh timer. Exit sends Disconnect when an operation is pending, a safety disconnect is available, or the last known protection was armed, then refuses to close until the latest snapshot is a verified disconnect or no protection is outstanding. That window was compiled on Linux and not launched.

Connect and failover capture revision, insecure-proxy consent, LAN access, protection-on-connect, country mode, country, and disabled families immediately before the core start. If that stamp differs, or the selected node is no longer eligible, when the start returns and the operation is still owned, connect ends as `POLICY_CHANGED` with protection still armed. Production confirmation checks the stamp, exclusion, and insecure-certificate consent again and does not enter `Connected` on a mismatch. On `CoreExit` the broker leaves `Connected` and clears `CoreRunning` before it waits for a replacement. A policy change during that switch stops the replacement and does not restore `Connected`. A token that is already cancelled is rejected before `Arm`. An explicit unprotected connect requires both `protectionRequired: false` and `ProtectionOnConnect: false`; it reaches `Connecting` with `ProtectionArmed` false. A protected arm that returns `Armed` false is still refused. The production guard still refuses to arm on this host.

A probe observation is stored only while the captured revision and insecure-proxy flag still match and the node is still scheduled. Excluded nodes, a policy reason, `SkipCertVerify` without owner consent, a disabled family, and a strict-country mismatch are not probed. `skip-cert-verify: true` is written only when the node asks for it and the profile build was allowed insecure proxy certificates. Otherwise the profile is rejected with `CERT_VERIFICATION_DISABLED`. The HTTPS probe target is authenticated separately. An untrusted target certificate is rejected when the caller does not supply that certificate as a trust anchor. A published success is an authenticated HTTP/1.0 or HTTP/1.1 response with status 204, no redirect, no HTML body, and a declared body of at most 8192 bytes. When the observation carries a candidate digest or target URI, those values must match the node and the probed URI. One unsupported node is recorded as failed and the queue continues. A real candidate failure during on-demand admission marks that node failed and keeps the older success time, so the newer failure wins. Caller cancellation before publication does not charge the budget or write health.

The desktop stores the daily probe counter in `probe-budget.txt` under its AutoVPN local data directory. A missing file or a new UTC day starts at zero spent. A corrupt, negative, or unreadable file is exhausted for that day. A charge stamped for an older day is ignored. A download sample is kept only when the caller binds it to the node digest and network epoch and both still match after the read. That helper does not write health.

## What is not wired

The service still constructs `RefusingCoreController` and has no admission transport. `config/*.json` remains an operator pin, not a file the window loads. The desktop refresh, working list, and session resync are in the WPF project and were not executed as a window. There is no installer and no SBOM. The public subscription was not fetched from GitHub in the tests; the refresh journey used a local stand-in for the pinned tree URL.
