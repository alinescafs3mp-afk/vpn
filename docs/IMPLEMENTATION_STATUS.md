# Implementation status

Дата среза: 2026-10-03. Готовности к выпуску нет.

## Кратко

Срез AutoVPN 0.1.0 собран на Linux. Пакет A — `2b46431693fe58eb02c40a36a0b192ce000b6fed`. Пакет B — `1ba3cf78dd242141d8605286ac2a45101ad737c0`. Пакет C — `b532631694f81b438a3902da27d0a4e97f998ae6`: второй клиент канала может отменить заблокированный старт, повтор запроса возвращает прежний результат, чужое подтверждение не принимает следующую попытку. Таблица по F01–F34: `docs/AUDIT_FIX_STATUS.md`. Архив `win-x64` старше этих коммитов, самодостаточный и неподписанный. Это не установщик. Проверки Windows не запускались. Директива лежит в `for_fix/` и не редактировалась.

## Milestones

| Milestone | State | Evidence |
|---|---|---|
| 0 Inspect and pin | Done in this tree | Directive saved. Core and source manifests. Inventory JSON. Remote was empty |
| 1 Vertical slice | Partial | Synthetic profile and broker contract are tested. Windows TUN → traffic → disconnect is `NOT_RUN`. UI compiled, not clicked |
| 2 Ingestion | Partial | The unelevated coordinator discovers, fetches, reconciles, and probes. A fresh import stays `Pending` until a real core probe. Live public bodies were not stored |
| 3 Retention and failover | Partial | Unit tests cover retention, 304 refetch without membership, pinned node, strict country, stale generation, switch budget, and cancel-during-start. A core exit at the switch cap is `Reconnecting` with protection, not `Connected`. No live tunnel |
| 4 Desktop | Partial | The window binds consent, settings, and catalogue rows and can refresh. It was compiled and not clicked. No screenshot and no measured latency on screen |
| 5 Windows hardening | Not started as a Windows run | Guard refuses. Recovery does not claim a cleanup it did not do. No installer |
| 6 Handoff | This document | Package C code is `b532631694f81b438a3902da27d0a4e97f998ae6`. This docs commit records that SHA. Owner-ready release is not this slice |

## What the code does

- Imports VLESS, VMess, Trojan, Shadowsocks, Hysteria2, and TUIC from URI, Clash, and Xray JSON into a canonical digest.
- Blocks insecure certificates and plaintext VLESS/VMess/Trojan by default. Rejects non-public destinations.
- Keeps nodes per artifact. An empty text file does not delete another artifact's nodes. A 304 or a failed fetch does not drop a healthy node.
- Refuses a truncated Git tree as a full catalogue. Tree entries that are not blobs are not inventoried.
- Ranks and fails over in memory without switching a pinned node or leaving a strict country. A confirmed core exit at the switch cap or during cooldown becomes `Reconnecting` and keeps protection. A target outage alone does not change that phase. A live session that is only over the switch budget can still wait without being marked disconnected.
- Generates a Mihomo profile with `redir-host`, port 53 toward `AUTO_SELECT`, and `MATCH,AUTO_SELECT` or `MATCH,REJECT`. No `MATCH,DIRECT`. Control characters in scalars are rejected. `skip-cert-verify` is emitted only when the owner allowed it.
- Refuses TUN validation off Windows. The shipped service uses `RefusingCoreController` and `UnavailableNetworkGuard`.
- Speaks to the desktop over a local length-prefixed JSON pipe. A partial, oversized, or held-open client does not block the next command. On Linux the peer uid must match the service. An unverified peer is rejected. A state-changing request must name the current revision. A repeated request id returns the stored response.
- Opens SQLite on Windows with DPAPI. Off Windows the service stays in memory and the protector throws if persistence is asked. An effect journal whose schema is not 1 is left in place.

## Tested on 2026-10-03

- `dotnet build AutoVpn.slnx -c Release`: 0 warnings, 0 errors, after `b532631`. Desktop included, not run.
- `dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release`: 63 passed, 0 failed, 1 skipped, duration 1 s. `AUTOVPN_MIHOMO_PATH` was unset. The skipped fact is native `mihomo -t`, recorded as NOT_RUN rather than passed. This is the unit-test project, not the solution test runner.
- The package B unit-test run was 57 passed and 1 skipped. The earlier `dotnet test AutoVpn.slnx -c Release` after package A was 47 passed, 0 failed, 1 skipped.
- One extra run with the pinned Linux Mihomo: `mihomo -t` accepted a synthetic non-TUN VLESS profile to `203.0.113.10`. Exit 0. Hash `3122d100e8177501776109f1a6253a694611627cf4d7c7ec82705855cf8626a8`. Not a tunnel and not a public node.
- Direct HTTPS to the two `generate_204` URLs at 19:41:48Z returned 204 and an empty body. That is the build host, not a VPN.
- LICENSE fetches for the pinned source commit at 19:42:10Z: five mirrors returned `text/plain`. Bitbucket returned 404 HTML. raw.githack returned 301 HTML. Yandex was not requested.
- Two earlier starts of `dotnet test AutoVpn.slnx` aborted with `Internal CLR error (0x80131506)` before a result. The run recorded above passed. That abort is not a failed test.

## Not tested

Windows edition, administrator rights, TUN, Wintun load, DNS, IPv6, sleep, crash of the core or broker, pipe ACL for a second user, UI clicks, installer, upgrade, uninstall, Authenticode. Public nodes: no latency, no speed, no country measurement. `scripts/package.ps1` was not executed.

## Artifact

Local file only, gitignored:

`artifacts/autovpn-0.1.0-win-x64-self-contained.tar.xz`

SHA-256 `a4c142f9d88c85849278c6e7b0e13cdfc7a26975bdd3bf565d944a3ecd0c64cf`

134272704 bytes. Packed 2026-10-02T19:58:42Z. Self-contained publish of the four executables. No Mihomo, no Wintun, no service registration, no signature. The binaries are older than `2b46431`, `1ba3cf7`, and `b532631`. The previous sum `bd314673d6947e7d14a385d1f5bdbe624e541a096053cd79fb9aa7ad2948baee` is obsolete.

## Release gate

Code for the slice is compiling and unit-tested. It is not Windows-validated and not an owner-ready test release.
