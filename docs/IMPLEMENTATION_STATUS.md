# Implementation status

Дата среза: 2026-10-02. Готовности к выпуску нет.

## Кратко

Собран первый исполняемый срез AutoVPN 0.1.0: домен, импорт, каталог SQLite, брокер, который отказывается поднимать туннель, окно WPF на русском, инвентаризация дерева источника. Архив `win-x64` самодостаточный и неподписанный. Это не установщик. Проверки Windows не запускались.

## Milestones

| Milestone | State | Evidence |
|---|---|---|
| 0 Inspect and pin | Done in this tree | Directive saved. Core and source manifests. Inventory JSON. Remote was empty |
| 1 Vertical slice | Partial | Synthetic profile and broker contract are tested. Windows TUN → traffic → disconnect is `NOT_RUN`. UI compiled, not clicked |
| 2 Ingestion | Partial | Parsers, identity, SQLite, family accounting, and a complete tree count exist. Live subscription bodies are not stored and were not probed |
| 3 Retention and failover | Partial | Unit tests cover empty-text retention, 304, pinned node, strict country, and stale generation. No live tunnel |
| 4 Desktop | Partial | Russian shell, disclosure, tray type. No screenshot, no measured latency on screen |
| 5 Windows hardening | Not started as a Windows run | Guard refuses. Recovery does not claim a cleanup it did not do. No installer |
| 6 Handoff | This document | Push is the remaining step of this slice. Owner-ready release is not this slice |

## What the code does

- Imports VLESS, VMess, Trojan, Shadowsocks, Hysteria2, and TUIC from URI, Clash, and Xray JSON into a canonical digest.
- Blocks insecure certificates and plaintext VLESS/VMess/Trojan by default. Rejects non-public destinations.
- Keeps nodes per artifact. An empty text file does not delete another artifact's nodes. A 304 or a failed fetch does not drop a healthy node.
- Refuses a truncated Git tree as a full catalogue.
- Ranks and fails over in memory without switching a pinned node or leaving a strict country.
- Generates a Mihomo profile with `redir-host`, port 53 toward `AUTO_SELECT`, and `MATCH,AUTO_SELECT` or `MATCH,REJECT`. No `MATCH,DIRECT`.
- Refuses TUN validation off Windows. The shipped service uses `RefusingCoreController` and `UnavailableNetworkGuard`.
- Speaks to the desktop over a local length-prefixed JSON pipe and keeps accepting until cancel.
- Opens SQLite on Windows with DPAPI. Off Windows the service stays in memory and the protector throws if persistence is asked.

## Tested on 2026-10-02

- `dotnet build AutoVpn.slnx -c Release`: 0 warnings, 0 errors, before 19:41:48Z. Desktop included, not run.
- `dotnet test AutoVpn.slnx -c Release`: 29 passed, 0 failed. `AUTOVPN_MIHOMO_PATH` was unset, so the Mihomo fact did not start the binary.
- One extra run with the pinned Linux Mihomo: `mihomo -t` accepted a synthetic non-TUN VLESS profile. Exit 0. Not a tunnel and not a public node.
- Direct HTTPS to the two `generate_204` URLs at 19:41:48Z returned 204 and an empty body. That is the build host, not a VPN.
- LICENSE fetches for the pinned source commit at 19:42:10Z: five mirrors returned `text/plain`. Bitbucket returned 404 HTML. raw.githack returned 301 HTML. Yandex was not requested.

## Not tested

Windows edition, administrator rights, TUN, Wintun load, DNS, IPv6, sleep, crash of the core or broker, pipe ACL for a second user, UI clicks, installer, upgrade, uninstall, Authenticode. Public nodes: no latency, no speed, no country measurement.

## Artifact

Local file only, gitignored:

`artifacts/autovpn-0.1.0-win-x64-self-contained.tar.xz`

SHA-256 `bd314673d6947e7d14a385d1f5bdbe624e541a096053cd79fb9aa7ad2948baee`

Self-contained publish of the four executables. No Mihomo, no Wintun, no service registration, no signature. Packed around 19:44Z. The binaries match this source tree; the commit object did not exist yet at pack time.

## Release gate

Code for the slice is compiling and unit-tested. It is not Windows-validated and not an owner-ready test release.
