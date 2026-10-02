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

A node id is opaque. The digest is SHA-256 of canonical JSON (`CanonicalizerVersion` 1). `udp: true` is omitted so a share link and a Clash default match. `udp: false` stays in the document. Display names are not part of identity. The profile names the proxy `n` plus the ASCII letters and digits of the node id.

## Tunnel phases

`Disconnected`, `PreparingProtection`, `Connecting`, `Connected`, `Reconnecting`, `Blocked`, `RestoringNetwork`.

`Connected` is entered only by `ProductionVerified`. IPC cannot send that event. `CoreStarted` does not change the phase; the broker applies `ProtectionArmed` first, which moves `PreparingProtection` to `Connecting`. A successful arm in tests therefore stays on `Connecting` until an in-process `ConfirmProduction(true)`.

## Profile rules

Controller secret is at least 16 characters and bound to `127.0.0.1`. DNS mode is `redir-host`. UDP/TCP port 53 goes to `AUTO_SELECT` before any LAN `DIRECT` lines. The final rule is `MATCH,AUTO_SELECT`, or `MATCH,REJECT` when no node is present. There is no `MATCH,DIRECT`.

`ValidateAsync` refuses a TUN profile when the OS is not Windows, before it looks at the binary.

## Catalogue

Membership is per artifact. Deleting one artifact drops only that artifact's nodes from the family. The family remains current while another artifact still maps to it.

Empty text does not erase nodes that came from another artifact. A 304 and a failed fetch keep the previous healthy node.

SQLite schema 1. A newer or older schema is left untouched. A bad header or a failed `integrity_check` is renamed to `*.quarantine-<timestamp>`, and a new empty database is opened. The quarantine path is exposed. An unversioned user table throws and is left in place.

Production secrets use DPAPI current-user on Windows. Off Windows, `UnavailableSecretProtector` throws. The service therefore uses `MemoryCatalogue` off Windows and `SqliteCatalogue` on Windows. The Windows open path is compiled and not executed.

## IPC

Frame: 4-byte little-endian length plus UTF-8 JSON, maximum 256 KiB, protocol version 1. Unknown operations, a bad version, a replayed id (64 remembered), a remote pipe, a second SID, and forbidden payload fields fail closed.

`LocalIpcServer` accepts one connection at a time until cancelled, then waits again. `PipeOptions.CurrentUserOnly` is set. That flag was not verified against a second Windows user (`NOT_RUN`). The service still passes a fixed caller identity (`windows-user` on Windows, `uid:<pid>` off Windows). It does not read the connected client's SID.

## What is not wired

The desktop does not refresh the subscription, does not list measured nodes, and does not load `config/*.json`. Those files are pins for operators. The probe coordinator exists and has not been pointed at the public subscription.
