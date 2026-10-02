# AutoVPN — Windows TUN Client with Verified Subscription Rotation
## Grok implementation directive and technical specification · v1.0

**Prepared:** 2026-10-02  
**Implementation repository:** https://github.com/alinescafs3mp-afk/vpn  
**Primary subscription repository:** https://github.com/igareck/vpn-configs-for-russia  
**Product inspiration:** Karing; this is an independent application, not an affiliated Karing release.  
**Working product/namespace name:** `AutoVPN` / `AutoVpn`. Naming must not delay implementation.  
**Document language:** English. **All communication with the owner and all default user-facing application text: Russian.**

> Build a small, attractive Windows application that connects the computer through a real TUN interface. Its defining feature is a durable, locally verified pool of public proxy configurations: discover all relevant upstream subscription families, validate new candidates before making them selectable, retain old configurations while they remain useful, and recover automatically when the selected server stops working. A green icon must represent measured functionality, not the existence of a downloaded string.

---

## Contents

1. [Execution mandate](#1-execution-mandate)
2. [Product contract and terminology](#2-product-contract-and-terminology)
3. [Verified upstream observations](#3-verified-upstream-observations)
4. [Technology decisions](#4-technology-decisions)
5. [Architecture and ownership](#5-architecture-and-ownership)
6. [Source discovery and coverage](#6-source-discovery-and-coverage)
7. [Fetching, mirrors, and scheduling](#7-fetching-mirrors-and-scheduling)
8. [Safe import and protocol normalization](#8-safe-import-and-protocol-normalization)
9. [Identity and deduplication](#9-identity-and-deduplication)
10. [Persistent data model](#10-persistent-data-model)
11. [Status, freshness, and network identity](#11-status-freshness-and-network-identity)
12. [Candidate testing and measurement](#12-candidate-testing-and-measurement)
13. [Refresh, merge, retention, and quarantine](#13-refresh-merge-retention-and-quarantine)
14. [Selection, ranking, and automatic failover](#14-selection-ranking-and-automatic-failover)
15. [TUN, routing, DNS, and IPv6](#15-tun-routing-dns-and-ipv6)
16. [Traffic protection and recovery](#16-traffic-protection-and-recovery)
17. [Core control and configuration application](#17-core-control-and-configuration-application)
18. [Windows privilege boundary and IPC](#18-windows-privilege-boundary-and-ipc)
19. [Desktop experience and visual design](#19-desktop-experience-and-visual-design)
20. [Privacy and diagnostics](#20-privacy-and-diagnostics)
21. [Settings and default values](#21-settings-and-default-values)
22. [Performance and resource budgets](#22-performance-and-resource-budgets)
23. [Packaging, installation, and updates](#23-packaging-installation-and-updates)
24. [Repository layout and development conventions](#24-repository-layout-and-development-conventions)
25. [Implementation sequence](#25-implementation-sequence)
26. [Acceptance journeys](#26-acceptance-journeys)
27. [Adversarial and regression test matrix](#27-adversarial-and-regression-test-matrix)
28. [Release evidence and completion criteria](#28-release-evidence-and-completion-criteria)
29. [Explicitly deferred features](#29-explicitly-deferred-features)
30. [Final instruction to Grok](#30-final-instruction-to-grok)
31. [Reference sources](#31-reference-sources)

---

## 1. Execution mandate

### 1.1 What you must deliver

You are the implementation engineer and technical lead for this repository. Implement the application, automated tests, Windows packaging, operational documentation, and release evidence. Do not stop after generating another architecture document, a mock interface, a backlog, or a console-only proof of concept.

The owner has delegated programming-language and most application-design choices. Follow the decisions below unless a short, evidence-backed architecture decision record demonstrates an actual incompatibility. Resolve ordinary engineering choices yourself. Do not repeatedly ask the owner to choose frameworks, folder layouts, class names, or test libraries.

Communicate with the owner **entirely in Russian**, including progress, limitations, installation instructions, and the final report. Keep technical source identifiers, commands, protocol names, commit hashes, and upstream names unchanged. English technical documentation and source comments are acceptable. Put this language requirement in the repository's `AGENTS.md` so it survives context compaction and future sessions.

### 1.2 Repository discipline

The repository was accessible and reported empty when inspected for this specification. Recheck its current state before initializing it; do not assume it is still empty when you begin.

- Clone/use `alinescafs3mp-afk/vpn`; verify the remote and current branch.
- Read existing `AGENTS.md`, README, and owner changes first, if any.
- Use the existing default branch. If there is no initial commit, initialize `main`.
- Commit coherent, tested increments. Push completed work to the intended repository, not an unrelated repository or a private scratch directory.
- Do not force-push, rewrite unrelated history, delete owner work, or claim a push succeeded without verifying the remote commit.
- Do not commit downloaded subscription credentials, live runtime profiles, local databases, access tokens, private keys, machine-specific routes, unredacted packet captures, or private diagnostic bundles.
- Put release binaries in GitHub Releases/artifacts, not ordinary Git history. Commit checksums and build manifests.
- A failed push is a delivery limitation, not permission to conceal the failure. Preserve local work and report the exact status in Russian.

### 1.3 Honest completion

Distinguish `IMPLEMENTED`, `TESTED`, `NOT_RUN`, `FAILED`, and `BLOCKED`. Compiling on Linux or passing unit tests does not establish that Windows TUN, WFP, DNS restoration, UAC, an installer, or crash recovery works.

Do not fabricate Windows screenshots, packet captures, speed results, public-node availability, test counts, signed binaries, or release artifacts. When Windows execution is unavailable, complete the implementable work, set up the Windows test path, and mark the Windows-dependent release gate as not passed. Do not call the product production-ready before that gate passes.

No implementation deadline is prescribed. Work in short, verifiable vertical slices rather than creating an oversized framework in advance.

---

## 2. Product contract and terminology

### 2.1 Required user outcome

The normal flow is:

1. Install and launch one ordinary desktop application.
2. Let it fetch the built-in sources and verify candidates automatically.
3. Press **«Подключить»** or select a country/server and connect.
4. Use normal Windows applications through TUN without configuring browser proxy settings.
5. Leave AutoVPN in the tray. Source refresh, lightweight monitoring, and eligible-server failover continue without supervision.
6. Disconnect or exit explicitly and return to the previous network configuration.

The main interface must remain simple even though the implementation is careful.

### 2.2 Terminology: do not model a subscription as a single server

| Term | Meaning |
|---|---|
| Upstream repository | The source project publishing subscription data. |
| Source family / subscription | A logical collection, such as `BLACK_VLESS_RUS`. It contains multiple candidates. |
| Representation | TXT, Base64, Clash proxies-only YAML, or a client-specific export of a family. |
| Mirror | Another location for the same source; not automatically a new independent source. |
| Node / configuration | One complete connection configuration, including authentication, transport, TLS/Reality settings, and other connection-affecting fields. |
| Endpoint | A host/IP and port. Multiple genuinely different configurations can share it. |
| Usable pool | Nodes with sufficiently fresh, successful local tests and compatible security/capability policy. |
| Connected node | The one configuration currently selected for ordinary internet traffic. |
| Retained node | A previously discovered node kept even if missing from a newer source snapshot, because it still passes local checks. |
| Quarantine | Stored but non-selectable malformed, failed, unsupported, policy-blocked, or stale candidates. |

User-facing source selection restricts the pool; connection ultimately uses a node in that pool. The default source scope is **all enabled built-in VPN families**, deduplicated.

### 2.3 Required v1 features

The first usable release includes Windows TUN, one-click automatic selection, manual server selection, automatic/manual subscription refresh, locally verified import, retention of working older nodes, automatic failover, real latency and traffic statistics, bounded speed testing, country/source/protocol information, favorites, source coverage reporting, tray operation, recoverable installation, and network restoration.

The main **«Рабочие»** list must not contain a newly imported node that has never passed local end-to-end validation. It can later become stale or fail; timestamps and monitoring must make that transition honest.

### 2.4 Non-promises

Do not claim anonymity, trustworthiness of public server operators, guaranteed circumvention, continuous uptime, accurate geographical location from a flag, or seamless migration of arbitrary TCP/UDP sessions. A passing test is time-, network-, endpoint-, and capability-specific.

This product is a client for public configurations, not a VPN provider. It does not operate the upstream servers and cannot guarantee their quality.

---

## 3. Verified upstream observations

These observations were made on **2026-10-02**. They are inputs to the design, not assumptions that may remain hard-coded forever.

### 3.1 Source organization

The source README describes publication after availability/latency/speed checks approximately every **2–4 hours**, depending on subscription type. This is the upstream maintainer's description, not a locally verified service-level guarantee. One sampled TXT file also contained `profile-update-interval: 1`; treat such metadata as advisory rather than a reason to run an aggressive global loop. [S01] [S03]

The inspected root contains these seed VPN families: [S01] [S02]

| Family ID | Root file | Initial category label |
|---|---|---|
| `black-mixed` | `BLACK_SS+All_RUS.txt` | Mixed protocols / BLACK |
| `black-ss-weak-dpi` | `BLACK_SS_WEAK_DPI_RUS.txt` | Shadowsocks / weak-DPI variant |
| `black-vless` | `BLACK_VLESS_RUS.txt` | VLESS / BLACK |
| `black-vless-mobile` | `BLACK_VLESS_RUS_mobile.txt` | VLESS / mobile |
| `white-reality-mobile` | `Vless-Reality-White-Lists-Rus-Mobile.txt` | Reality / mobile / WHITE |
| `white-cidr-all` | `WHITE-CIDR-RU-all.txt` | WHITE / CIDR / all |
| `white-cidr-checked` | `WHITE-CIDR-RU-checked.txt` | WHITE / CIDR / checked subset |
| `white-sni-all` | `WHITE-SNI-RU-all.txt` | WHITE / SNI |

These are seed paths, **not an exhaustive future filename allowlist**. The source also has `Export/`, `TOR-BRIDGES/`, and QR-code assets. The inspected export directories include Base64, Clash, Happ, Streisand, V2Box, v2RayTun, v2rayNG, and v2rayN variants. [S02]

### 3.2 Reuse opportunity and an important trap

There are ready-made `Export/Clash/PROXIES_ONLY/` files. Prefer these when available and semantically adequate because they already express proxy objects in the chosen core's native configuration family. Do not import someone else's complete routing policy just to obtain its proxy list. [S04]

A sampled proxies-only file contained multiple nodes with the **same address, port, and credentials but different `client-fingerprint` values**. Those are distinct configurations. Deduplicating only by address/port, UUID, or display name would destroy useful variants. The YAML also used quoted `\xNN` escapes; parsing must use a real YAML parser rather than string extraction. [S04]

A sampled mixed-protocol file included configurations requesting insecure certificate handling. Such requests must be represented and surfaced, never silently converted into a global TLS-verification bypass. [S03]

### 3.3 Mirror reality

The repository publishes `MIRRORS.md` and discusses alternate fetch locations. Mirror availability, synchronization, and trust can change. Ship a reviewed transport registry, verify the actual raw URL shape, and report stale/unavailable mirrors. Do not execute HTML or indiscriminately trust every link embedded in README text. [S01] [S05]

### 3.4 Karing inspiration

Karing describes itself as a Flutter GUI around a modified sing-box core, with subscriptions, groups, routing, and a simpler beginner experience. Use the concept of a clear connection screen and useful node management, not its branding or entire feature surface. Its license file includes a naming/association restriction in addition to its GPL notice. Keep original branding and retain attribution for any legitimately reused code. [S06] [S07]

### 3.5 No live reliability claims from this preparation

The preparation for this specification inspected repository structure, sample data, and official documentation. It did **not** connect through the public nodes or measure their speed. Grok must produce live Windows evidence separately.

---

## 4. Technology decisions

### 4.1 Primary stack

| Layer | Decision |
|---|---|
| Desktop UI | C# + WPF + the built-in modern Fluent theme, MVVM. |
| Runtime | .NET 10 LTS, pinned SDK and current supported patch at implementation time. |
| Proxy/TUN engine | Official stable **MetaCubeX/Mihomo**, distributed as a separate, pinned executable. |
| Windows networking privilege | A small C# Windows Service broker; native Windows APIs through narrow, audited interop. |
| Persistence | SQLite via `Microsoft.Data.Sqlite`, explicit migrations and transactions. |
| Import | A bounded YAML parser such as YamlDotNet, `System.Text.Json`, and tested URI parsers. |
| MVVM support | CommunityToolkit.Mvvm or an equivalently small, maintained dependency. |
| Logging | Structured .NET logging with mandatory redaction and bounded retention. |
| Packaging | A conventional per-machine Windows installer, self-contained application runtime, bundled approved core/driver; choose a maintained packaging tool and pin it. |
| Tests | .NET unit/integration tests, Windows UI automation, controlled local-network protocol fixtures, and a Windows administrator-run acceptance suite. |

.NET 10 is an LTS release and WPF has a modern Fluent theme. Mihomo documents TUN, proxy selection/testing APIs, and the relevant proxy formats. These support the choice; they do not eliminate implementation-specific testing. [S08] [S09] [S10] [S11] [S12] [S13] [S14]

At preparation time, Mihomo's `releases/latest` resolved to `v1.19.32`. Treat this as a dated observation, not an instruction to fetch an unpinned `latest` binary at runtime. Select and record the exact tested release, asset, SHA-256, source commit/tag, and license in the repository. [S12]

### 4.2 Why this stack

This is initially a Windows utility, not a cross-platform product. WPF gives direct access to Windows integration without shipping a browser engine. Mihomo avoids inventing networking protocols and has a direct path from the source's Clash exports to runtime proxy objects. A separate core process allows version pinning, controlled restarts, and replacement without embedding a network engine into the UI.

These are design decisions, not claimed benchmark results. Do not spend the first iteration comparing half a dozen GUI frameworks.

### 4.3 Engine compatibility gate

Before building the full UI, create a capability report from a current source snapshot:

- Protocol and transport inventory, including all connection-affecting option combinations.
- The exact core version and architecture used.
- Which combinations parse, pass native configuration validation, and pass controlled handshake tests.
- Unsupported and ambiguous combinations with counts and redacted examples.
- Whether source representations differ semantically or omit unique candidates.

Mihomo remains the default. Do not silently drop unsupported options because the core happens to accept the rest of the profile. In particular, validate VLESS encryption, Reality, Vision, fingerprints, WebSocket, gRPC, XHTTP, Shadowsocks plugin variants, Hysteria2 and TUIC against the **pinned binary**, not merely the latest documentation. [S13]

If a material current source family cannot work with this core, document the smallest justified remedy. Prefer a compatible stable version or a correct importer over a second networking engine. An optional second engine is a later extension unless the capability audit proves it indispensable. Do not ship a misleading claim of complete connection support; complete discovery/accounting and executable support are different metrics.

### 4.4 Platform scope

Primary release target: Windows 11 x64 on a currently supported build, with administrator authorization for installation/network effects. No external .NET installation should be required for end users.

Windows 10 x64 may be a compatibility target only after runtime/platform support is checked and actual tests pass; do not label it supported automatically. ARM64, Linux, macOS, Android, and iOS are out of the initial release scope. Keep domain logic OS-independent for testing, but do not build a speculative cross-platform abstraction framework.

### 4.5 Licensing and provenance

Choose a GPL-compatible open-source license for the new repository after checking existing owner files; for an otherwise empty repository, GPL-3.0-or-later is the intended default. Verify the exact licenses of the pinned core, Wintun distribution, UI dependencies, icons, and any copied parser code. Include notices and the corresponding-source arrangements required for the distributed combination. Do not assume that calling a separate executable resolves every redistribution obligation. [S07] [S17]

No Karing name, logo, paid services, advertising placements, copied marketing, or implied affiliation. Reuse small, compatible, attributed components when that saves real work; do not fork an entire unrelated client and then spend the project deleting features.

---

## 5. Architecture and ownership

### 5.1 Process boundary

Use a small installed application, not a collection of independently deployed microservices:

```text
AutoVpn.Desktop.exe — ordinary user token; remains in tray
  ├─ WPF screens / view models
  ├─ Source discovery + download + bounded parsing
  ├─ User SQLite catalogue + credential protection
  ├─ Refresh / validation / ranking coordinator
  └─ Typed authenticated IPC client
             │ Windows named pipe; local and ACL-restricted
             ▼
AutoVpn.Service.exe — minimal privileged network broker
  ├─ Single serialized connection state machine
  ├─ Revalidation of typed network-effect requests
  ├─ Core lifecycle / configuration generation / health watchdog
  ├─ TUN, DNS, firewall/WFP effect journal and recovery
  └─ Fixed approved core executable + immutable runtime generations
             │ private authenticated control channel
             ▼
Mihomo primary process — the only TUN owner

Bounded probe worker/core processes — no TUN; isolated candidate testing
```

The desktop can include its background coordinator in-process. Closing its window minimizes it to the tray, rather than terminating the coordinator. Do not add a permanent extra agent daemon merely to keep an invisible window running.

### 5.2 Ownership rules

- Exactly one service instance owns machine-wide TUN/network changes.
- Exactly one authorized interactive owner controls that instance at a time. A second Windows session must not silently take it over.
- The desktop owns subscriptions, parsing, user filters, and historical ranking data.
- The broker owns network effects, the currently applied configuration generation, and the authoritative connection/protection state.
- Probe workers own no system routes, DNS settings, or TUN devices.
- A refresh never directly mutates global routes or the active selection.
- A UI state is a projection of authoritative state, not a separate source of truth.

### 5.3 UI crash and service survival

A closed/minimized window does not interrupt normal operation. A crashed desktop must not cause a direct-traffic leak or orphaned network settings. The service retains the existing valid tunnel and its protection state, or enters protected recovery if the core fails.

The service may perform bounded emergency failover using a small, previously validated standby set supplied by the owner session. It must revalidate a standby before use and apply the same freshness/epoch restrictions. It does not fetch subscriptions or interpret arbitrary remote YAML while elevated.

When the desktop reconnects, it queries authoritative state and resumes coordination. Do not start a second core or repeat network changes based on stale UI memory. Full catalogue updates resume when the desktop coordinator is running; do not claim they continue after an explicit full application exit.

### 5.4 Concurrency and cancellation

Use one connection actor/serialized command queue, one refresh coordinator, and a bounded probe scheduler. Carry these identifiers through commands and results:

`operationId`, `catalogueGeneration`, `networkEpoch`, `coreGeneration`, `nodeId`, `configHash`.

Network changes, stop commands, imports, and setting changes invalidate relevant old work. Cancellation must cascade into HTTP requests, probe sockets, queued jobs, and child processes. A canceled callback may not later publish a node, switch a selection, or restore an obsolete connection request.

Use monotonic elapsed time for deadlines and UTC for stored timestamps. Inject clock and network-epoch providers into domain logic for deterministic tests.

---

## 6. Source discovery and coverage

### 6.1 All relevant subscriptions, without duplicating equivalent exports

Ship the eight seed families from section 3 and a source adapter for the repository. At startup and periodically, inspect the current repository tree for new or renamed supported data files. Discovery must cover the repository, not only README links.

Classify each discovered artifact as:

- A VPN source family or new representation of an existing family.
- A supported client export containing extractable nodes.
- A mirror reference.
- A Tor bridge list.
- QR/image/documentation/license/workflow or other non-subscription content.
- An unknown potentially relevant format requiring compatibility accounting.

Ignore executable workflow content and images as connection data. Never execute source-repository scripts.

**All relevant files must be accounted for. Not every equivalent serialization needs to be downloaded and tested repeatedly.** Fetch each primary source and any non-equivalent/uncertain representation necessary to establish union coverage. An established equivalent representation can be skipped when its content identity and equivalence evidence have not changed. The UI/coverage report must distinguish fetched, equivalent-skipped, unsupported, excluded-non-VPN, unavailable, and pending.

### 6.2 Representation preference

For a known family:

1. Prefer current `Export/Clash/PROXIES_ONLY/` objects.
2. Parse root TXT/URI and Base64 representations as complementary/fallback inputs.
3. Extract only allowlisted node objects from complete Clash or client-specific JSON exports when needed for coverage.
4. Union semantically distinct configurations; preserve all provenance.
5. Do not import external DNS, routes, proxy groups, rule providers, listeners, scripts, update policies, certificates, or dashboard settings.

An export may lag the root file or omit unsupported transports. Do not assume it is equivalent because the basename resembles a known family. Track publication/content dates separately, compare normalized identities, and expose discrepancies.

### 6.3 Tor and externally referenced material

`TOR-BRIDGES/` is not an ordinary set of Mihomo VPN outbounds. Discover and explicitly label it **«Tor-мосты — отдельный тип, не используется в этой версии»**. Do not mislabel Tor as a dead VPN subscription or silently omit it from coverage. Implementing a Tor client and bridge transport lifecycle is a separate future feature.

A notice directing users to Telegram or another external platform is not a downloadable subscription. Report that material is not present in the inspected repository. Do not scrape accounts, request the owner's Telegram login, or invent missing keys. The built-in adapter operates on public repository data and reviewed mirrors only.

### 6.4 Source controls

All built-in VPN families are discovered and enabled initially, subject to node-level security rules. Each family can be disabled; this removes its sole-provenance nodes from eligibility without destroying favorites/history. A node shared with another enabled family remains eligible.

Show a family-level state independently of node health: `Fresh`, `Unchanged`, `PartiallyUpdated`, `Stale`, `FetchFailed`, `Disabled`, `UnsupportedFormat`, `RemovedUpstream`, or `DiscoveryIncomplete`.

A reachable feed with zero usable nodes is **not** an active server. A failed feed download can coexist with working retained nodes.

---

## 7. Fetching, mirrors, and scheduling

### 7.1 Update schedule

Default subscription refresh: every **120 minutes**, with bounded per-installation jitter of ±10 minutes. The setting is editable; minimum normal interval is 15 minutes. Apply advisory upstream update metadata only within local bounds and never in a way that creates per-file request storms.

Run a due update on startup and after a missed interval; do not replay every missed interval after sleep. On first launch, begin immediately after the brief privacy disclosure. A manual **«Обновить»** request runs immediately unless the same operation is already running; repeated clicks join/coalesce with that operation.

Full tree discovery: initially, then approximately every 12 hours, and on explicit **«Перепроверить источники»**. A regular data refresh can use the cached manifest without rediscovering the entire tree.

### 7.2 Fetch mechanics

Use HTTPS, certificate validation, explicit timeouts, response-size limits, and conditional requests (`ETag` / `Last-Modified`) where supported. An HTTP 304 reuses the last verified content snapshot; it does **not** refresh a node's health timestamp.

Use immutable commit-addressed content when practicable: resolve a repository revision, enumerate the tree for that revision, and fetch associated blobs/files. Record the source revision and content hash. If fetching a branch head directly or a mirror without commit identity, record the weaker provenance rather than pretending it is a coherent Git snapshot.

Git blob identity and transport TLS are not a publisher signature. A hash identifies content; it does not make a compromised upstream trustworthy.

Do not require a GitHub account/token for normal public-source use. Respect API rate-limit headers, `Retry-After`, pagination, and a tree's `truncated` flag. Use cached seed paths and direct file fetches when discovery API access is exhausted. A partial tree response must never be interpreted as proof that missing families were removed.

### 7.3 Mirror transport registry

The built-in registry contains a reviewed primary origin plus reviewed alternate raw-content URL templates. Seed mirror choices from the upstream mirror documentation, but verify each exact URL and content type during implementation. Never construct mirrors by arbitrary string replacement across incompatible URL schemes.

Registry entries include transport ID, approved host, path template, repository identity, allowed redirect destinations, last verification, and trust/provenance level. Ordinary subscription content cannot enlarge the registry.

Failover sequence:

1. Use the current working VPN route when already connected.
2. On a transport failure, try another reviewed location for that source.
3. Use an explicitly permitted narrow bootstrap/direct path only according to the connection/protection policy.
4. Fall back to cached source content and retained verified candidates.
5. Report an update failure without clearing the catalogue.

A mirror is an additional supply-chain trust surface. A matching verified immutable content hash permits equivalence; a different or unattributed snapshot is stored separately with provenance. Never let an older mirror overwrite newer source metadata silently. A mirror mismatch is a warning, not a license to disable TLS verification.

### 7.4 Boundaries and defaults

Starting limits: 3 concurrent source downloads; 20 seconds total per attempt; 2 bounded retries with jitter; 8 MiB decompressed per artifact; 64 MiB per refresh; 64 KiB per logical URI line; 50,000 candidates per cycle. All limits are product safeguards, not facts about current source size. Measure real size and tune through configuration with visible warnings.

Reject HTTP 200 HTML login/challenge/error pages as subscription content. Reject binary files, unsupported encodings, decompression bombs, invalid content, off-registry redirects, local/metadata destinations, and infinite redirect chains. Maximum redirect chain: 3, revalidating each destination.

Never treat an exceeded size/candidate limit, canceled download, timeout, rate limit, or invalid snapshot as a legitimate empty source.

### 7.5 Offline, captive portal, and metered connections

Keep the catalogue and UI usable offline. Mark the network condition separately. An unavailable internet connection or captive portal must not create a mass permanent node-failure event.

On a metered network, pause automatic bandwidth tests and reduce opportunistic validation; essential active-tunnel health checks and explicitly requested operations remain available with a visible traffic budget. Do not open captive-portal pages automatically while protection is active. Offer a clear explicit disconnect/unblock action when needed.

---

## 8. Safe import and protocol normalization

### 8.1 Import is a compiler, not a file copy

Implement this pipeline:

```text
Bounded bytes → content classification → safe parser → typed node candidates
→ semantic validation → canonical identity → security/capability classification
→ generated isolated core profile → native core validation → live candidate checks
→ eligible catalogue membership
```

The source is untrusted even when downloaded from a known repository. Never hand its complete configuration to an elevated core.

### 8.2 Required formats

Support newline-separated share URIs, Base64-wrapped URI lists, and Clash/Mihomo YAML `proxies` collections. Add bounded extraction adapters for node-bearing JSON exports present in the source. Extract supported node data from full client configurations, not their executable behavior or routing policies.

At minimum, inventory and implement the protocols used by the fetched families: VLESS, VMess, Trojan, Shadowsocks, Hysteria2/`hy2`, and TUIC where present. Additional core-supported protocols are admitted only through an explicit typed adapter and test fixture. Recognizing a URI scheme alone is not support.

### 8.3 Parser hardening

- Enforce UTF-8/BOM handling, CRLF/LF normalization, per-document depth and scalar limits, bounded Base64 decoding, and cancellation.
- Use a real YAML parser, including legitimate quoted escapes. Reject custom tags, object construction, aliases/anchor expansion beyond a small safe bound, duplicate mapping keys, and unexpected root shapes.
- Use bounded JSON depth; reject duplicate security-relevant keys and ambiguous interpretation.
- Distinguish standard and URL-safe Base64; allow missing padding only when unambiguous; never repeatedly decode arbitrary layers. Maximum wrapper depth: 2.
- Parse URI components rather than splitting every string on `:` or `@`. Handle bracketed IPv6, percent encoding, encoded credentials, and fragments correctly.
- Preserve semantically meaningful absence versus explicit values. An absent SNI is not automatically equivalent to a guessed SNI.
- Do not turn `+` into a space indiscriminately; path/fragment and form-encoded query semantics differ.
- Keep ordered ALPN and other ordered lists ordered. Do not sort data whose order changes negotiation.
- Strip control characters, bidi controls, and markup from display labels, while preserving the real connection data separately.
- Never use a node name or imported path as a filename, shell argument template, class name, or core-control identifier.
- No network includes, recursive provider downloads, arbitrary plugins, user-supplied DLLs, scripts, or remote rule execution.

### 8.4 Security policy

Public entries that request `insecure`, `allowInsecure`, or `skip-cert-verify` must receive `PolicyBlocked` by default. An advanced, explicit **«Разрешить серверы без проверки сертификата»** action may enable those candidates with persistent warnings and fresh tests. Prefer scoped exceptions; never auto-enable the option to make an empty list look healthy.

This policy concerns the relevant protocol's server authentication. Reality/pinned-key authentication and authenticated Shadowsocks encryption must be evaluated correctly; lack of a conventional TLS certificate does not by itself mean a protocol is unprotected. Conversely, an unencrypted VLESS transport without an authenticated encryption layer must not be labeled secure simply because its protocol is VLESS.

Do not silently replace fingerprints, disable certificate checks, alter encryption modes, or drop mandatory transport options to force compatibility. An unsupported field that can change connectivity/security produces an explicit compatibility result.

### 8.5 Destination safeguards

Public-source proxy destinations must not resolve to loopback, unspecified, multicast, link-local, private/internal-only, or cloud metadata addresses. Check A and AAAA results, IPv4-mapped IPv6, literals, and effective remote addresses after DNS changes. Do not rely only on the display hostname.

Reject hostname/port tricks and dangerous SNI/host values that would escape the typed serializer. A reviewed later manual-LAN source feature may relax private-address restrictions only through explicit user authorization; it is not part of automatic public ingestion.

Source-fetch destinations, proxy-server destinations, and probe-target destinations are different allowlists. Passing one does not authorize arbitrary use in another.

### 8.6 Capability report

For every rejected or non-usable row preserve a stable reason code and sanitized explanation. Examples:

`INVALID_URI`, `INVALID_PORT`, `INVALID_UUID`, `YAML_LIMIT`, `AMBIGUOUS_FIELD`, `UNSUPPORTED_PROTOCOL`, `UNSUPPORTED_TRANSPORT`, `UNSUPPORTED_SECURITY_OPTION`, `CERT_VERIFICATION_DISABLED`, `PLAINTEXT_TRANSPORT`, `NON_PUBLIC_ENDPOINT`, `CORE_CONFIG_REJECTED`.

Coverage counters must balance: every discovered/parsed record is accounted for as a unique candidate, duplicate, invalid, unsupported, policy-blocked, or pending. Never silently skip records just because conversion is inconvenient.

---
## 9. Identity and deduplication

### 9.1 Canonical identity

Build a versioned, deterministic canonical representation of **connection semantics**, not of the original text. Include protocol, canonical host, port, authentication, encryption, transport, flow, TLS/Reality options, fingerprint, SNI, ALPN order, WebSocket/gRPC/XHTTP options, obfuscation, plugin options, UDP options, and every supported field that can change connection behavior. Exclude display labels, flags, source filenames, update timestamps, and measured statistics.

Normalize only proven equivalences: hostname case and IDNA representation, IP-literal representation, integer port representation, and parser-defined equivalent Boolean values. Preserve case-sensitive paths, passwords, IDs where applicable, ordered lists, and meaningful absent/default distinctions. A URI that omits an option and YAML that spells out a default are equivalent only when the importer has an explicit, tested rule for that protocol/version.

Store the full internal digest with a unique constraint; assign a separate opaque `nodeId` for UI/IPC identifiers. Do not expose credential-derived canonical data or digests in public diagnostics. Never use an upstream display name as the primary key. Keep the canonicalizer version for future migrations.

### 9.2 Merge rules

An exactly equivalent node appearing in several families is one node with multiple provenance memberships. Preserve all source memberships and useful label aliases. Different credentials, paths, SNI, encryption settings, or fingerprints mean different nodes, even on the same host/port.

A renamed node keeps history. A credential or security-option change creates a new identity and requires new tests. Do not transfer a green status to a different configuration just because the name or IP is unchanged.

Retain endpoint-level grouping separately for UI grouping and probe rate limits. It must never replace configuration-level identity. Fixtures must cover equivalent TXT/YAML/Base64 entries and intentionally non-equivalent near-duplicates.

---

## 10. Persistent data model

Use one user catalogue database and a separate broker-owned recovery journal. Do not share a writable SQLite file between privilege levels.

### 10.1 Minimum entities

| Entity | Required information |
|---|---|
| `SourceFamily` | Stable ID, label/category, enabled state, discovered paths, expected formats, last discovery, retirement state. |
| `SourceArtifact` | Family, representation, origin/mirror, source revision, ETag, Last-Modified, content hash, fetch/parse status, bounded counts. |
| `SourceSnapshot` | Immutable generation, fetch time, completeness, schema version, validation summary, previous snapshot. |
| `Node` | Opaque ID, canonicalizer version/digest, typed protected connection data, sanitized labels, first/last seen. |
| `NodeMembership` | Node, family, artifact, first/last observed revision, current/historical membership, provenance confidence. |
| `NodeAssessment` | Node/config identity, network epoch, health state, last success/failure, latency summary, consecutive results, retry time. |
| `ProbeSample` | Probe kind, target ID/version, elapsed time, status, bytes, outcome/reason, node/config, epoch, core version, timestamp. |
| `SpeedSample` | Direction, payload size, duration, throughput, target, freshness, network epoch, incomplete/budget-limited flag. |
| `NodePreference` | Favorite, exclusion, owner label, country/source policy, exception authorization. |
| `RefreshRun` | Operation/generation, per-stage counters, resumable cursor, cancellation, final outcome. |
| `ConnectionSession` | Desired state, active node, switches/reasons, core generation, connection/disconnection times; no browsing history. |
| `Settings` | Versioned typed settings, defaults, validation, safe migration. |
| Broker journal | Owner SID, intended connection/protection state, owned effect IDs, preconditions, apply/rollback phase, recovery version. |

Use foreign keys, explicit transactions, unique constraints, and indexes on eligibility, source membership, retry time, and last success. Use WAL where appropriate; back up with SQLite's supported backup mechanism, not by copying only the live main file and losing WAL contents.

### 10.2 Durability and retention

A partial download or crash must not replace the last good snapshot. Commit catalogue changes in short transactions; do not hold database locks while downloading or probing. Publish small verified batches so the first usable candidates appear before a large full scan finishes.

Store credentials using Windows user-scoped protection and restrict filesystem ACLs. Redacted normalized fixtures belong in Git; actual subscription payloads and the owner's database do not. The service receives the necessary typed connection data over authorized IPC and stores only its restricted runtime material. User-scoped DPAPI data must not be assumed decryptable by the service account. [S18]

Starting limits: 10,000 retained candidate records, 20 recent probe samples per node, 30 days of aggregated quality history, and a bounded 7-day diagnostic log. Favorites retain their metadata even after obsolete credentials are removed. Storage pressure evicts obsolete failed records first; it must not delete the active node or silently erase favorites.

Schema migration is versioned and backed up. An unsupported newer database must produce a clear message, not destructive downgrade. A corrupt database can be quarantined and restored/rebuilt with explicit reporting; the broker must still be able to disconnect and recover networking without that database.

---

## 11. Status, freshness, and network identity

### 11.1 Separate status dimensions

Do not collapse every condition into a Boolean `active`:

| Dimension | Examples |
|---|---|
| Import/support | Parsed, invalid, unsupported, security-policy blocked. |
| Measured health | Pending, checking, healthy, degraded, stale, failed, environment unknown. |
| Connection | Not selected, selected, connecting, connected, reconnecting. |
| Source | Current upstream member, retained historical member, disabled source. |

Default Russian labels: **«Работает»**, **«Проверяется»**, **«Нужна проверка»**, **«Недоступен»**, **«Не поддерживается»**, **«Заблокирован настройками безопасности»**, **«Подключён»**, **«Сохранён из предыдущего обновления»**. Display text/icons as well as color.

An active downloaded subscription means the source is enabled/reachable; it does not mean every server works. Show a source summary such as **«Рабочих 18 / проверяется 7 / недоступно 12»**. The connected state must refer to the actual broker/core selection, not the row currently highlighted in the list.

### 11.2 Eligibility contract

Define one tested eligibility function used by publication, selection, failover, and the UI:

```text
eligible(node, context) =
    parsed_and_supported(node)
    AND security_policy_allows(node)
    AND not_owner_excluded(node)
    AND source_and_country_scope_allows(node)
    AND capabilities_satisfy_requested_mode(node)
    AND successful_assessment_matches_exact_config(node)
    AND assessment.network_epoch == context.network_epoch
    AND assessment.age <= context.allowed_age
    AND assessment.latency <= context.max_acceptable_latency
    AND no_later_conclusive_failure(node)
    AND not_in_failure_cooldown(node)
```

Default catalogue freshness: 30 minutes. Pre-connect validation is stricter: within 60 seconds, including the current network epoch. A stale node is not silently considered healthy; clicking it in a historical/diagnostic view schedules a fresh check before selection. A failed new node never enters **«Рабочие»**.

A node can fail immediately after any successful check. Product wording must say **«Проверено 2 мин назад»**, not **«Гарантированно работает»**. A source refresh cannot guarantee that a server remains alive after publication.

### 11.3 Network epochs

Increment a `networkEpoch` on relevant underlying network changes: Wi-Fi/Ethernet transition, changed default uplink/gateway/DNS, resume from sleep, material address changes, or a detected competing VPN. Distinguish changes caused by AutoVPN's own TUN setup from a real uplink change, otherwise the client invalidates itself indefinitely.

Old samples remain historical but cannot authorize a connection on the new epoch. Cancel outdated work and prioritize the current node plus a few standbys for retesting. Use a short debounce for bursts of Windows network notifications. Avoid uploading SSIDs, MAC addresses, gateway details, or network fingerprints anywhere.

Network-offline and probe-service-failure are **environment outcomes**, not proof that every public node died. Clock rollback does not make an old sample fresh; use monotonic deadlines within a run and conservative stored-age handling across restarts.

---

## 12. Candidate testing and measurement

### 12.1 What a passing test must establish

A TCP port accepting connections is not enough. ICMP is optional diagnostic information and must never be the admission gate. Test the actual protocol through the actual candidate, using the pinned core and a valid authenticated HTTPS destination.

Required stages:

1. Bounded parsing and security/capability checks.
2. Native core configuration validation, with a timeout and captured/redacted diagnostics.
3. Actual proxy/protocol handshake and TLS-validated application request through that node.
4. A second independent, approved target for new-node admission.
5. Capability-specific checks where the product claims UDP or IPv6 support.

Require successful requests to two approved targets for initial general-web admission. Retry a failed target once after a small delay or replace a diagnosed unhealthy target from the reviewed target registry. Do not pass a node merely because an arbitrary HTTP response arrived: validate the expected status/body, reject authentication pages and captive-portal HTML, and do not disable HTTPS verification.

Do not claim that success against two sites proves universal internet access or particular streaming-service access. A valid but target-blocked node can be classified separately and revisited, not mislabeled as a universally dead server.

### 12.2 Isolated candidate path: no accidental double tunnel

Candidate validation **must not** inherit the desktop's default HTTP proxy or send traffic through the currently connected node. Otherwise a broken candidate can appear healthy and its latency can be measured through the wrong route.

Use a bounded non-TUN probe core/profile and an authenticated loopback SOCKS/HTTP entry point bound to the candidate, or a verified per-node native delay path. The worker's upstream transport must leave through the selected physical uplink, with necessary tightly scoped broker authorization when protection is armed. Resolve the candidate endpoint through the dedicated bootstrap path; resolve the final test hostname through the tested proxy where applicable.

Every probe is bound to its own candidate identity, resolved endpoint, core generation, and network epoch. Keep a worker lease so two simultaneous probes cannot race a shared mutable selector. Never change the production global selector just to test another server.

Evidence must include this negative control: production node A works, candidate B is deliberately broken, Windows TUN remains connected via A, and B still fails its check. Also test the inverse and concurrent candidates. Confirm physical egress and no recursive A→B chaining using controlled endpoints and packet capture.

### 12.3 Target registry and provider health

Ship a small reviewed target registry with at least two independent HTTPS operators and documented expected responses, request size, timeout, and rate budget. Ordinary subscriptions cannot set test URLs. Avoid ad/tracker URLs, authenticated services, third-party speed-test protocols requiring credentials, and arbitrary user-provided URLs in privileged requests.

During development use owned/local test targets with known contents. During release preparation verify the current availability and acceptable use of each public target; record that date. Common 204 connectivity endpoints can be evaluated, but do not assume they provide an unlimited testing service. Do not turn every installation into an aggressive public speed-testing bot.

Maintain per-target circuit breakers. One target failing across previously reliable candidates should suspend that target and trigger alternate-target checks before a mass node penalty. The default disconnected bootstrap can test connectivity directly, but while protection is armed it may not bypass the policy just to perform a public connectivity check.

### 12.4 Latency

The main column is **«Задержка»** in milliseconds: measured time for a specified HTTPS request through the proxy. Tooltip: **«Проверка HTTPS через сервер; это не ICMP-пинг»**. Record the method, target, TLS/handshake inclusion, and sample count.

Display a median of comparable recent samples; keep individual target results in details. Do not combine an ICMP RTT, cold handshake time, and warmed application latency into an unexplained number. A value supplied in an upstream node name is an unverified label, never a local measurement.

Suggested visual bands: up to 150 ms excellent, 151–400 ms good, 401–800 ms acceptable, 801–1,500 ms slow. The default admission ceiling is **1,500 ms**, editable by the owner. These are product defaults, not promises about Russian networks. Jitter can be shown after enough comparable samples; use a clearly defined statistic such as p95 minus p50 with a minimum of 10 samples.

### 12.5 Throughput and live traffic are different metrics

Show all three separately:

- **«Скорость теста ↓ / ↑»**: measured application-payload throughput from a bounded test, with time/bytes/target.
- **«Сейчас ↓ / ↑»**: current core-observed traffic rate, normally refreshed once a second.
- **«За сеанс ↓ / ↑»**: cumulative counted traffic with clear scope.

Do not derive throughput from latency. Do not show unmeasured speed as zero or as a made-up estimate. Use **«Не измерена»**, **«Устарела»**, or a budget-limited label. Unknown throughput does not make a newly verified node unusable.

For throughput testing, request incompressible HTTPS payloads through the isolated candidate, bypass caches, disable automatic decompression, count actual received payload bytes with a monotonic clock, and stop on either byte or time budget. Record whether setup/warm-up is excluded. Formula: `Mbps = payload_bytes × 8 / measured_seconds / 1_000_000`. Do not label Mbps as MB/s.

Default manual test: at most 16 MiB download or 8 seconds per node, one node at a time. Optional upload uses only generated random bytes, an approved endpoint, explicit user action, and at most 4 MiB or 5 seconds. No personal file uploads. A short/capped test is an indicative sample, not a link-capacity guarantee.

Automatic speed testing is **off by default**. Offer an opt-in lightweight mode for the top 5 eligible nodes, at most 2 MiB each, 64 MiB per day total, never on metered connections without consent. Mass manual testing shows an estimated upper bound and allows cancellation. Bandwidth-test results expire for ranking after 2 hours or an epoch change; details retain their historical timestamp.

### 12.6 Probe scheduling and budgets

Start with 8 concurrent lightweight candidate checks, at most 2 simultaneous requests to the same endpoint, and 2 independent non-TUN worker-core processes. A worker may process a bounded batch of candidate definitions; do not launch an unbounded process per node. Actual concurrency must stay within limits even when requests time out or cancellation races completion.

Use 5-second request deadlines and a 20-second total new-candidate admission budget, excluding explicitly rate-limited waits. Bound response payloads to 8 KiB for ordinary health checks. Respect target- and endpoint-level pacing and maintain a daily traffic counter; suspend background scanning with a visible reason when its budget is exhausted. Default automatic validation payload budget: 128 MiB/day, separate from subscription downloads and optional speed tests. Account for protocol overhead separately where measurable; do not present payload accounting as exact physical-wire traffic.

Priority: user connect request → active-node health → emergency standby → previously good retained nodes → new candidates → routine refresh → old quarantined retries. Within background work, enforce round-robin source fairness and age-based priority so one large family cannot starve the others.

Refresh performs bounded local checks on old retained and new candidates. A very large catalogue may require resumable batches; report **«Проверено X из Y»**, not a false fully completed update. Keep pending candidates out of the usable list. Pause/cancel must close sockets, revoke temporary probe permits, and stop child work promptly; operation cancellation is not node failure.

### 12.7 UDP, IPv6, and geographic metadata

Basic web usability and optional capability flags are separate. A TCP-only node may be usable for TCP but must not cause UDP to fall through directly. Verify UDP with a controlled DNS/UDP fixture before claiming it; a successful HTTPS test says nothing about arbitrary UDP. Preserve **unknown**, **supported**, and **failed** states for each capability.

For geography, initially parse source labels into **«Страна по подписке»** with unknown/conflict handling. Prefer a verified exit-IP observation for the connected node, using a reviewed small HTTPS endpoint and privacy disclosure. Server-host geolocation, advertised country, and actual exit country are different facts; keep them separate. A CDN/fronting address is not necessarily the exit location.

Do not perform an online GeoIP request for every candidate. An optional offline, properly licensed country database can enrich results later. A failed exit-IP lookup must not disconnect an otherwise working tunnel. Flags are decorative companions to country text, never the sole identifier.

---

## 13. Refresh, merge, retention, and quarantine

### 13.1 Fundamental rule

The owner explicitly wants **new usable candidates PLUS old candidates that still work with acceptable latency**. Do not replace the catalogue wholesale with the latest file.

Conceptually:

```text
new_usable_pool = locally_verified_and_policy_allowed(
    deduplicate(fresh_candidates UNION retained_candidates)
)
```

This describes the result, not a permission to mark the entire union healthy before verification. Exact-config existing tests may be reused only inside their current freshness and network-epoch window.

### 13.2 Refresh transaction

```text
Refresh(trigger):
    coalesce_or_start_single_refresh()
    capture operation_id, network_epoch, settings_revision
    resolve approved source artifacts and fetch into staging
    for each complete, valid artifact:
        parse and account for every record
        normalize/deduplicate without losing provenance
        persist immutable snapshot and pending candidates
    preserve previous snapshots for any unsuccessful artifact
    enqueue current/retained/new candidates with fair priorities
    as current-generation assessments succeed:
        atomically publish eligible rows and progress counters
    as assessments conclusively fail:
        quarantine, record reason, schedule bounded retry
    if operation/epoch/settings changed:
        do not publish obsolete results
    finish with complete/partial/cancelled status and balanced counts
```

A progress screen can expose stages **«Загрузка → Разбор → Проверка → Готово»**. While a large scan continues, already verified nodes are available. An update never reconnects a healthy active session just because the file order changed.

### 13.3 Retaining old nodes

A node missing from a new complete snapshot becomes historically sourced, not immediately dead. Retain it in the working pool only while local health and current policies allow it. Recheck active/standby/retained candidates before less valuable historical records.

Suggested retention: keep a missing node while it continues passing; after 7 days without any successful check, evict its obsolete credentials unless protected by a current session or an explicit favorite-retention preference. Keep favorite metadata and failure history in a lightweight archived record. There is no seven-day expiry for a node that still passes every day.

A source being disabled is different from a source temporarily failing to download. Disabling removes that source's exclusive nodes from automatic selection immediately; shared nodes remain eligible through other enabled memberships. Removing one source must not delete a node still used by another.

On a suspected harmful source or an owner-requested purge, offer to remove its retained nodes as well. Missing-from-upstream retention is availability behavior, not proof of trust. Security exclusions and explicit deletion override historical success.

### 13.4 Quarantine and recovery

Suggested retry schedule after conclusive node failure: 5 minutes, 15 minutes, 1 hour, 6 hours, then 24 hours, with jitter and a maximum budget. A new appearance or a manual recheck can advance the retry, but it must not create duplicate jobs. Protocol/configuration errors wait for changed data or an importer/core upgrade rather than wasting network probes.

Keep failed/unverified items in a secondary **«Все / Недоступные»** view, not mixed with green usable rows. Recovery requires a fresh admission test. Favorites do not bypass quarantine. Global offline/captive-portal incidents pause retries without multiplying failure counters.

### 13.5 Empty results and partial coverage

A valid empty source can update source membership but cannot, by itself, prove old nodes are dead. A completely unusable new batch preserves still-eligible retained nodes and reports the empty result.

When no currently eligible node exists, display that fact. Try bounded retained-node revalidation and reviewed mirrors. Never connect `DIRECT`, auto-allow insecure TLS, relax a strict country filter, or fabricate a green row to make the connect button appear successful.

---

## 14. Selection, ranking, and automatic failover

### 14.1 Selection modes

Default mode: **«Автоматически — стабильное соединение»** across enabled source families. The user can restrict by subscription family and country, choose a favorite, or manually select one node.

A manually chosen node remains selected while healthy. By default, automatic recovery on its failure is enabled **within the user's explicit source/country constraints**. Provide a visible **«Закрепить сервер: не переключать»** option; when pinned and failed, remain blocked/reconnecting rather than picking a different node. Do not widen constraints silently.

A country chosen as a strict filter is a hard constraint. A separate preference can mean **«Предпочитать страну, разрешать другие»**, but it must be explicit. Automatic mode with no country constraint may use any eligible country, with a notification showing a change.

### 14.2 Ranking policy

Start with an explainable deterministic ranking, not machine learning. Apply eligibility first, then rank by recent success/reliability, comparable median latency, stability, and only then fresh throughput evidence. Keep current connection stickiness as a decision rule rather than disguising it as test data.

An initial cost model may be:

```text
cost = median_https_latency_ms
     + 300 × recent_failure_fraction
     + 100 × recent_flaps
     + 100 × low_sample_confidence
```

Define windows and bounds in code: at most 20 comparable samples in 24 hours; flaps capped at 5 in one hour; confidence penalty 0..1. With sparse data, use a conservative prior rather than calling one success 100% reliability. For low sample counts show **«2 успешных проверки из 3»**, not a scientific-looking availability percentage.

Treat this formula as a tunable product baseline validated against deterministic fixtures. Throughput can break ties or drive an explicitly chosen **«Быстрее загрузка»** mode, but unknown throughput is not zero. No active speed test is required for basic connection eligibility.

### 14.3 Hysteresis and standby policy

Maintain up to 5 validated standbys, with diversity across endpoint/provider/source where data allows. The same physical endpoint under five fingerprints is not a robust five-server reserve.

Do not rotate on every refresh, every new fastest sample, or a tiny latency difference. By default switch only on failure or sustained unacceptable performance. An optional automatic optimization mode can switch after at least 10 minutes of dwell time and 3 consistent measurements showing an improvement of at least 25% and 100 ms. Hard failure bypasses dwell time.

Existing TCP/UDP sessions may break or keep using their previous outbound depending on core behavior. State this explicitly. Do not market a selector change as seamless session migration. Never force an IP change on a healthy session just to equalize traffic across free servers.

### 14.4 Failure detection and recovery

Active health interval: 30 seconds when connected, with jitter. A healthy traffic counter is useful evidence but does not prove arbitrary new connections work; use small periodic application-level checks. Require 3 failed health cycles involving alternate approved targets before treating an ordinary timeout as a node outage. A core exit or explicit transport failure is an immediate technical incident, but must still be distinguished from an offline uplink.

Recovery sequence:

1. Keep the protection guard armed; set **«Переподключение»**.
2. Diagnose core/uplink/target conditions before poisoning every node's score.
3. Fresh-check the best allowed standby on the same network epoch.
4. Apply a selector change or a controlled core-generation transition.
5. Verify new internet requests through the production TUN path, not only via the control API.
6. Commit the new active state and notify once, with previous/new country and reason.
7. On failure, try the next eligible standby within bounded attempts; then back off and expose **«Нет рабочего сервера»**.

Maximum 3 switch attempts in one minute before a 60-second cooldown, except an explicit owner retry. Backoff increases when the whole network is offline. A click on **«Отключить»** always cancels this process and prevents later callbacks from reconnecting.

A target-readiness improvement objective for controlled tests is recovery within 30 seconds of a confirmed outage when a prevalidated standby exists. Detection time is additional and depends on the health interval. Report measured timing; do not claim a universal public-network guarantee.

---
## 15. TUN, routing, DNS, and IPv6

### 15.1 System-wide connection contract

Use a real Windows layer-3 TUN adapter through the approved core/driver combination. A browser proxy, WinHTTP proxy setting, or only a localhost SOCKS listener does **not** satisfy this requirement. Ordinary applications must work without individual proxy configuration.

Default routing mode: all ordinary public internet traffic through the selected node. Support IPv4 and handle IPv6 explicitly. Do not silently apply an upstream region-based bypass such as “Russian domains direct.” Do not enable system proxy settings in addition to TUN; preserve existing WinINET/WinHTTP settings and explain conflicts instead of overwriting them.

Use one core-owned TUN adapter with a stable application-specific identity. Detect address/prefix collisions with Docker, Hyper-V, WSL, other VPNs, and the owner's LAN. Select a non-conflicting virtual prefix; do not assume one hard-coded subnet is always free. Route the core's own transport over a real uplink to avoid a routing loop.

### 15.2 Core settings baseline

Generate an application-owned profile. Starting choices are `tun.enable=true`, `auto-route=true`, `auto-detect-interface=true` when there is an unambiguous physical uplink, and an evaluated Windows-compatible stack. Start with `mixed`, then retain it only if Windows firewall and compatibility tests pass. Expose stack/MTU overrides only under advanced settings, with validated values.

Mihomo documents Windows-specific limitations for DNS hijacking and describes `strict-route` as adding DNS-leak-prevention firewall rules on Windows. It is **not sufficient evidence of a full kill switch**. Linux-only knobs such as `auto-redirect` and GSO must not be enabled merely by copying a documentation example. [S10]

The generated ordinary-traffic routing policy ends in the application-controlled selection group, with no direct fallback. Empty selection must reject/block traffic. Disable remote rule providers, external dashboards, arbitrary includes, automatic core updates, and imported rules. Use only the explicitly reviewed local rules necessary for TUN, DNS, and optional LAN access.

### 15.3 LAN access

Expose **«Разрешить доступ к локальной сети»**, default on for the initial desktop utility. Explain that this permits local-network traffic outside the VPN. This is not the same as sharing the VPN proxy listener with other devices: `allow-lan` on proxy/control listeners remains false. [S14]

Restrict exemptions to actual local/private scopes and necessary local discovery behavior; never infer “local” from a country or a public CIDR in a subscription title. When LAN access is off, still permit the minimum physical network control traffic required to maintain the uplink and reach the gateway at the link layer. Do not disable the entire NIC.

DNS to a local router must not accidentally bypass the selected DNS policy because the LAN exclusion is evaluated first. Test this explicitly. Do not add arbitrary network shares or remote desktop exceptions automatically.

### 15.4 DNS policy and bootstrap

Separate three purposes:

1. Resolving the proxy server and approved subscription/bootstrap hosts before a tunnel exists.
2. Resolving normal application destinations while connected.
3. Resolving test destinations through the candidate being tested.

For connected application traffic, route DNS through the core and selected outbound using a generated, reviewed resolver configuration. Prefer encrypted upstream resolution where compatible and tested. No unconditional direct fallback for ordinary application DNS. Cover UDP and TCP port 53, Windows multi-homed behavior, IPv6 resolvers, and DNS settings on all relevant adapters.

Bootstrap must avoid a circular dependency: resolving the proxy server cannot require an already-working proxy server. Use approved bootstrap resolvers or previously verified, TTL-bounded endpoint addresses, with minimum scoped exceptions. Document any direct bootstrap DNS exposure honestly. Do not leak every user destination through that exception. For a hostname endpoint, validate every effective A/AAAA address before authorizing core egress; retain the original TLS/transport identity when using a validated IP. [S19]

Prefer a minimal real-IP DNS mode for v1 if it passes requirements without additional fake-IP complexity. A fake-IP mode is acceptable after collision, mapping persistence, and compatibility tests. The chosen mode must be an explicit architecture decision, not an accidental upstream default.

An application's own HTTPS DNS traffic should traverse TUN like other HTTPS traffic; AutoVPN must not claim it can transparently inspect or override every encrypted resolver. Test common application behavior without promising absolute control over privileged third-party networking software.

### 15.5 IPv6 policy

Default policy: **IPv6 through the tunnel where supported; otherwise block public IPv6 while protection is active**. Merely setting `ipv6: false` inside the core does not prove Windows has stopped using its physical IPv6 route.

Do not permanently disable IPv6 on the owner's physical interfaces. Any required network-setting change must be reversible and journaled. A successful IPv4 check cannot be used to label IPv6 as tested. Cover dual-stack, IPv6-only/NAT64 environments where test infrastructure permits, and clear unsupported-state reporting where they cannot be supported in v1.

### 15.6 Switching and existing flows

Use in-place selector changes where the tested core supports them without rebuilding the TUN adapter. New connections must use the newly selected node after commit. Existing flows are not guaranteed to migrate; retain them only if the core does so safely. Do not close all application connections on every subscription refresh.

A required profile/core restart must be performed behind the protection guard with a known-good rollback profile. Keep current connection data alive independently of source membership until the session ends or a deliberate switch commits.

---

## 16. Traffic protection and recovery

### 16.1 Protection contract

Implement a real **«Блокировать интернет при обрыве VPN»** feature, recommended on and enabled after the first-run explanation. It protects ordinary public application traffic during connecting, failover, and core failure. Turning it off requires an explicit action and displays a persistent warning while disabled.

The feature is session protection, not an advertised enterprise always-on/pre-login VPN. Explain the allowed LAN and narrow bootstrap exceptions. Do not claim protection against a malicious administrator, kernel compromise, or other privileged software deliberately changing the same policy.

State transitions:

```text
Disconnected → PreparingProtection → Connecting → Connected
                                     │              │
                                     └→ Blocked ← Reconnecting
                                            │
                       explicit Disconnect/Release
                                            ↓
                              RestoringNetwork → Disconnected
```

The implementation may add internal states, but **«Подключён»** requires both the intended core selection and successful production-path validation. If protection setup fails, do not silently connect in a weaker mode. Report the failure and require an explicit choice of the unprotected mode.

### 16.2 Windows filtering implementation

Use a small audited WFP policy or an equally demonstrably correct Windows mechanism. Register application-owned provider/sublayer/filter identifiers. Do not disable Windows Firewall, replace global firewall policy, or create broad permanent direct-access exclusions.

Permit only necessary traffic categories: loopback IPC, selected TUN traffic, the approved core/worker processes to validated proxy endpoints, approved tightly scoped bootstrap, necessary uplink control traffic, and optional LAN access. An allow rule for the core executable must not mean that every arbitrary remote destination and port becomes a permanent direct exception.

Specify and test the actual WFP layers, filter arbitration, application identity, interface identity, IPv4/IPv6 coverage, and existing-flow behavior. A naïve block-all firewall rule plus an “allow VPN” rule is not an acceptable unverified design. Perform changes transactionally where supported. [S15]

Critically, protection must survive a core crash and the broker crash scenario that the UI claims to protect against. A dynamic WFP session that deletes the guard when its process exits does not meet that claim. Choose appropriate persistent/session lifetimes and prove behavior; persist only the application's own filters and recovery metadata. Do not claim full fail-closed behavior while leaving this case untested.

### 16.3 Effect journal

Before applying a mutation, record its intended effect, the current value/precondition, application ownership, and inverse operation. Persist journal phases around side effects so power loss between steps is recoverable. Record interface GUID/LUID rather than relying on a recycled interface index or display name.

Use compare-and-restore semantics: remove only application-owned routes/filters/adapters and restore only settings still matching the application's last written state. If another VPN or an administrator changed a setting meanwhile, preserve that newer state and report a reconciliation conflict. Never run a global “reset all network adapters/firewall/Winsock” as ordinary cleanup.

The service must recover or clearly report incomplete effects after restart even if the desktop database is missing. Repeated recovery must be idempotent. Corrupt/unrecognized recovery state must not trigger broad destructive cleanup.

### 16.4 Disconnect, exit, and reboot

**«Отключить»** means the owner permits ordinary direct networking again. Serialize cancellation, stop/reconcile core activity, restore app-owned network changes, then release app-owned guard filters last. A disconnection failure remains visible; do not show a clean disconnected state while networking is still stranded.

Closing the window goes to tray. **«Выйти»** explicitly disconnects and exits, with a confirmation only when this would end an active session. A process crash is not an explicit exit and must not silently authorize direct traffic.

Define a documented clean-shutdown/reboot policy and test it. The intended v1 behavior is cleanup on a clean shutdown; following an unclean crash/reboot, conservative recovery may keep the previous guard armed until the service recovers the requested session or the owner explicitly releases it. Surface this at next login. Do not promise boot-time leak protection without separate early-boot evidence.

Automatic startup/reconnect is opt-in. Starting the installed service for recovery does not grant permission to connect arbitrarily before the user's chosen policy permits it.

### 16.5 Recovery tool and competing software

Ship an offline recovery entry point, for example:

```powershell
AutoVpn.Recovery.exe status
AutoVpn.Recovery.exe disconnect
AutoVpn.Recovery.exe repair --owned-only
```

These are required product commands to implement, not claims that executables already exist. Administrative operations invoke UAC when necessary. Provide a Russian recovery guide that remains readable without internet access. Show owned effects before destructive repair and never touch another VPN's objects.

Detect another active TUN/VPN or ambiguous uplinks before applying conflicting changes. Explain the conflict rather than killing unrelated processes or resetting their adapters. Support safe coexistence where tested; otherwise offer to stop AutoVPN, not the owner's other software. Test with Docker/Hyper-V/WSL networking and normal LAN access.

---

## 17. Core control and configuration application

### 17.1 Adapter interface

Keep the core behind a narrow `ICoreController`/adapter with operations such as `ValidateConfig`, `Start`, `GetStatus`, `SelectNode`, `GetTraffic`, `ProbeNode`, and `Stop`. It must be mockable for deterministic tests, but production cannot use fake health or traffic values.

Mihomo's documented API includes version/status, proxy selection and node-delay testing, configuration operations, and traffic information. Verify paths, response shapes, and side effects against the pinned executable. In particular, some group test operations can affect selection behavior; prefer isolated per-node testing rather than invoking broad production-group operations casually. [S11]

### 17.2 Generated profiles only

The service accepts typed allowlisted connection data and application settings, revalidates them, and generates the runtime profile itself. It does not accept a raw imported YAML file, arbitrary profile path, raw command line, executable path, shell script, or unrestricted core API URL from the UI.

Represent each runtime generation in a private directory containing a validated profile and manifest. The manifest includes approved binary digest/version, schema version, node identities, policy revision, and generation. Keep one last-known-good generation for rollback. Credentials and control secrets are never printed in logs or included in command-line arguments.

Validate a candidate profile before it becomes active. Bound validation time and isolate it from network effects. One malformed imported node must not prevent a valid batch from loading: validate individual offenders or split failed batches while preserving accounting.

### 17.3 Control-channel security

Default core control: random loopback-only port with a cryptographically random high-entropy bearer secret, no external UI, no external DoH endpoint, no wildcard CORS/private-network access. Restrict access further by process/OS policy where feasible. A second local user must not control the privileged core.

Mihomo documents that its Windows named-pipe controller does **not** validate the configured API secret. Do not assume that enabling a pipe is inherently secure. A pipe-based core channel is acceptable only after its effective DACL and peer-access behavior are verified and restricted to the service; otherwise keep the authenticated loopback design. [S14]

The UI talks to the broker, not directly to the privileged core API. Do not expose unrestricted `/configs`, upgrade, restart, filesystem, debugging, or arbitrary URL-testing operations through the broker. Encode generated node identifiers properly in API paths and validate response types/sizes.

### 17.4 Lifecycle

Use absolute approved executable paths, safe argument construction without a shell, a restrictive working directory, and an explicit environment. Reject writable/untrusted binary directories, symlink/junction/reparse redirection, and DLL search-path hijacking. Verify the pinned core/driver before use.

Use Windows Job Objects or equivalent controlled child lifecycle as appropriate, while recognizing that the traffic guard must have an independent survival policy. Capture bounded stdout/stderr asynchronously and redact before persistence. Clean process exit, hard kill after timeout, and restart budgets are required. An old PID must not be mistaken for a restarted child; verify process identity/generation.

Default restart budget: at most 3 automatic core restarts in 5 minutes, then a blocked diagnostic state. No infinite high-CPU restart loop, duplicate TUN owner, orphaned probes, or invisible background updater.

### 17.5 Safe apply and rollback

Configuration application uses prepare/validate → arm protection → apply/select → verify effective state → commit. If any stage fails, retain protection and return to a still-valid previous generation or a clearly blocked state. Rollback cannot restore an owner-cancelled connection intention.

Do not hot-reload the entire catalogue on every successful probe. Keep the active node and a bounded useful standby set loaded; stage new definitions only when needed using a tested core mechanism. Catalogue refresh and runtime configuration refresh are separate transactions.

---

## 18. Windows privilege boundary and IPC

### 18.1 Least privilege

The desktop always runs unelevated in normal operation. Installation and narrowly scoped recovery use UAC. Use the least service-account rights compatible with the tested TUN/filter implementation; if LocalSystem is necessary, document why and keep the attack surface correspondingly small.

Install binaries and service configuration in protected locations. User-writable data, download caches, and logs must never become executable search paths. Do not give all local users service reconfiguration/start-parameter permissions merely to make UI control convenient.

### 18.2 IPC contract

Use a local named pipe with an explicit restrictive security descriptor, remote-client rejection, peer token/SID validation, and a single authorized owner lease. Default Windows pipe permissions are not an authorization design. Do not trust a `userId`, PID, or administrator flag sent inside JSON. Validate the actual caller context. [S16]

Suggested envelope:

```json
{
  "protocolVersion": 1,
  "requestId": "opaque-unique-id",
  "expectedStateRevision": 42,
  "operation": "SelectValidatedNode",
  "payload": {
    "nodeId": "opaque-node-id",
    "configRevision": "opaque-revision",
    "networkEpoch": 7
  }
}
```

Define schema, maximum frame size, timeout, request idempotency, operation-specific authorization, and rate limits. Larger typed profile data must still fit a documented bound; do not support arbitrary binary/file transfer. Unknown operations and incompatible protocol versions fail closed. Do not send raw subscription content to the service.

State-changing calls return a new authoritative state revision and an operation result. Replayed/stale requests cannot undo a newer disconnect. The event stream includes monotonic sequence numbers; after a reconnect/gap, the UI fetches a full snapshot instead of inventing missing state.

### 18.3 Threat-model boundaries

Protect against untrusted subscriptions, malformed metadata, hostile ordinary local users, browser-to-localhost requests, stale callbacks, and accidental owner mistakes. Same-user malware and administrator compromise are not fully preventable by this architecture; do not advertise otherwise.

Even an authorized desktop cannot ask the service to execute arbitrary code or make arbitrary network changes. Source URLs, probe URLs, firewall destinations, filesystem paths, and executables must remain separate typed policy-controlled inputs.

### 18.4 Required negative tests

Attempt control from another user/session; pipe squatting and reconnect; invalid SID claims; oversized frames; unknown operations; mismatched protocol version; replayed commands; stale expected-state revisions; raw YAML/path injection; malicious node names; wrong core bearer secret; public/LAN controller access; and a replaced writable core binary. All must fail safely without changing machine networking.

---
## 19. Desktop experience and visual design

### 19.1 Visual direction

Create an original, restrained Windows application: modern Fluent styling, generous spacing, clear typography, soft surfaces, and one primary action. Do not reproduce Karing's branding or turn the main page into a networking control panel.

Suggested design baseline: 1,040 × 720 logical pixels, minimum 860 × 620, resizable; 8-pixel spacing rhythm; 12–16-pixel card corner radii; system Segoe UI/Segoe UI Variable fallback; 14-pixel body text and a strong 24–32-pixel primary status. Use an original small application icon with a legible monochrome tray variant.

Support system/light/dark themes, Windows accent color, high-contrast mode, keyboard navigation, focus indicators, screen-reader labels, and 100/125/150/200% scaling. Motion is subtle and obeys reduced-motion settings. Do not ship blank, unstyled WPF controls and call the UI finished. Actual screenshots at more than one scale/theme are part of acceptance.

### 19.2 Navigation

Four primary destinations are enough: **«Подключение»**, **«Серверы»**, **«Подписки»**, **«Настройки»**. Diagnostics is an advanced page or drawer, not a permanent fifth dashboard. Show a compact tray-oriented experience; avoid a separate embedded web application.

### 19.3 Connection page

The screen should answer, at a glance: am I connected, through what country/server, is protection enabled, and what happens when I press the main button?

```text
AutoVPN                                  Подписки обновлены 12 мин назад

                 [ Подключён ]
                Германия · Frankfurt
        Сервер по подписке / подтверждённый выход — явно различать

                    [ Отключить ]

     Выбор: Автоматически               [ Изменить ]
     Задержка 82 мс                      Проверено 20 сек назад
     Сейчас ↓ 3,2 МБ/с   ↑ 180 КБ/с      За сеанс 1,4 ГБ

     Защита при обрыве: включена         Резерв: 4 рабочих сервера

     Рабочих 126    Проверяется 18       [ Обновить подписки ]
```

Numbers above are illustrative layout text, **never production defaults or test evidence**. Empty, first-run, checking, offline, reconnecting, protection-failed, no-eligible-nodes, and update-failed states must be designed explicitly. A loading animation must have a useful phase description and cancellation behavior.

A subscription update may fail while the tunnel works. Display a secondary warning without incorrectly changing **«Подключён»** to disconnected. Conversely, an available source does not justify a green connection badge when the tunnel failed.

### 19.4 Server page

Default tab: **«Рабочие»**. Secondary views: **«Избранное»** and **«Все»**, where failed/stale/policy-blocked states can be inspected without cluttering the main list. Use a virtualized list/table suitable for thousands of records.

Default columns: country/name, status, measured latency, measured download speed or **«Не измерена»**, protocol, and last check. Keep source family, transport/security details, endpoint, advertised/observed geography, retained status, upload sample, and health history in a side drawer or optional columns.

Controls: search by sanitized name/country/source, country filter, source-family filter, protocol filter, favorites, sort by latency/status/speed, **«Проверить»**, **«Проверить скорость»**, and **«Подключиться»**. Sorting by speed places unknown values explicitly at the end rather than treating them as zero. A selected row does not become the active node until the connection operation succeeds.

Row actions: favorite, exclude from auto-selection, retry, view details, and copy a redacted diagnostic summary. Copying an actual configuration is an advanced explicit action with a credentials warning, not the default right-click operation. Never expose secrets in hover tooltips.

Keep selection and scrolling stable while background results arrive. Batch UI updates; do not resort the list under the user's pointer on every millisecond change. Show progress for the current batch and a stop button.

### 19.5 Source page

Display **every discovered logical VPN family** with enabled status, last successful fetch, next scheduled refresh, transport/mirror used, current/retained usable counts, and parse/support warnings. Expand a family to inspect its representations and coverage.

Offer **«Обновить все»**, per-source refresh, and **«Перепроверить источники»**. Distinguish an unchanged 304, failed fetch, partial discovery, empty valid source, and unsupported export. Show current commit/content provenance in details, not in the ordinary connection page.

Do not show a fabricated single ping/country/speed for a multi-country subscription. A family can show **«Лучший проверенный сервер: 82 мс; 4 страны; скорость измерена у 3 из 18»** with an explicit aggregation definition. Selection of a subscription means automatic/manual use of its eligible member nodes.

### 19.6 Tray and lifecycle UX

Tray menu: show window, connect/disconnect, automatic/manual mode, a small favorites subset, refresh subscriptions, protection status, and exit. Tooltips show connection and country without credentials. Double-click opens the window. Start minimized and launch at login are opt-in settings.

Show one notification for a meaningful automatic switch, source-wide failure, or protection incident. Deduplicate repetitive errors; do not emit one toast per failed public server. Clicking a notification opens the relevant detail panel.

### 19.7 Russian language quality

Russian is the initial complete UI language, not a half-translated overlay. Put strings in resources. Keep protocol names, URLs, package names, commands, versions, and core identifiers unchanged. Use appropriate plural forms, local number formatting, and recognizable units: `мс`, `Мбит/с`, `МБ/с`.

Write error messages as “what happened → impact → available action.” Example: **«Не удалось обновить подписки. Подключение работает; сохранённые серверы не удалены. Повторить»**. Technical details can be expanded. Never show a raw stack trace as the only explanation.

---

## 20. Privacy and diagnostics

### 20.1 First-run disclosure

Before automatic public-node probing, show a brief Russian disclosure: the source contains third-party public servers; their operators are not vetted; subscription providers see requests; candidate tests contact public endpoints; and the client cannot guarantee anonymity or uninterrupted access. Explain normal HTTPS protections without claiming that a VPN makes unencrypted application traffic safe.

No account, cloud backend, telemetry, advertising, analytics, or mandatory API key is required. Do not install a root CA, intercept application TLS, disable certificate validation globally, or change browser security settings.

### 20.2 Logs

Keep useful structured events: import counts, update outcomes, reasons for quarantine, selection changes, operation IDs, network-epoch transitions, core lifecycle, and recovery outcomes. Default logs exclude destination browsing domains, full URLs, credentials, tokens, authorization headers, subscription bodies, and packet payloads.

Redact before writing to disk, not merely before displaying. Core stdout/stderr and parser exceptions can contain secrets; sanitize them too. Test redaction using synthetic canary secrets in all common fields, malformed URIs, crash messages, and exported reports.

Bound logs by both time and bytes. Debug logging is opt-in, automatically time-limited, and accompanied by a warning. A diagnostic export is user-initiated and previewable; it has no hidden upload step.

### 20.3 Diagnostics page

Show app/core/driver versions, service state, tunnel/protection state, last update summary, active operation/progress, recent sanitized errors, and a **«Проверить подключение»** action. Distinguish control-channel health, physical uplink, proxy handshake, DNS, IPv4/IPv6, and optional UDP results.

A **«Восстановить сеть»** action invokes the same restricted recovery mechanism as the offline tool. It must not be an opaque script that resets unrelated network configuration.

---

## 21. Settings and default values

These are starting product decisions, not empirically measured optima. Centralize them in one typed settings model; avoid scattered magic constants. Validate bounds and handle changed settings as a revision that invalidates incompatible pending work.

| Setting | Initial default / behavior |
|---|---|
| Source scope | All discovered supported built-in VPN families enabled. |
| Source refresh | 120 minutes, jitter ±10 minutes; ordinary minimum 15 minutes. |
| Tree rediscovery | First run, every 12 hours, and explicit request. |
| Manual refresh | Immediate, coalesced with an existing run. |
| First connection | Manual; no surprise automatic connection on install. |
| Selection | Automatic, favor stability; no proactive IP rotation. |
| Manual selection fallback | On within explicit filters; optional strict pin. |
| Country fallback | Never bypass a strict country filter. |
| Acceptable latency | 1,500 ms median comparable HTTPS latency. |
| Catalogue health freshness | 30 minutes. |
| Pre-connect assessment | Current epoch, at most 60 seconds old. |
| Active health interval | 30 seconds; ordinary outage after 3 failed cycles. |
| Warm standbys | Up to 5, refreshed about every 5 minutes with budget limits. |
| Background health sweep | Continuous bounded queue; prioritize fresh eligible/top 200 every 15 minutes, then fair remainder. |
| Lightweight concurrency | 8 total, at most 2 per endpoint, 2 worker-core processes. |
| Source download concurrency | 3. |
| Automatic speed testing | Off. |
| Optional automatic speed mode | Top 5, 2 MiB each, max 64 MiB/day. |
| Manual download test | Max 16 MiB or 8 seconds, one at a time. |
| Manual upload test | Explicit opt-in, max 4 MiB or 5 seconds. |
| Automatic health payload budget | 128 MiB/day; actual overhead shown separately if measurable. |
| Metered network | Automatic speed tests paused; background health work reduced. |
| Missing-node retention | While passing; eligible history does not expire merely by disappearing upstream. |
| Failed obsolete credential retention | 7 days since last success, subject to active/favorite exceptions and storage cap. |
| Stored candidate cap | 10,000; explicit visible limit handling and safe eviction. |
| Insecure certificate exceptions | Off, scoped opt-in with warnings. |
| LAN access | On, disclosed; proxy/controller LAN listening remains off. |
| IPv6 | Tunnel when supported, otherwise block public IPv6 while protected. |
| Protection on connection | Recommended on, after first-run explanation. |
| Start at login / auto-connect | Off / off; separately configurable. |
| Window close | Minimize to tray. |
| Explicit exit | Disconnect/restore and exit. |
| Theme | Follow Windows; manual dark/light overrides. |
| Traffic display refresh | About 1 second while visible, reduced background UI activity. |
| Core auto-upgrade | Disabled; app-controlled pinned release upgrades only. |
| Telemetry / remote diagnostics upload | None. |

Basic settings should contain only a few useful switches. Timeouts, MTU, stack, concurrency, probe registry details, and certificate exceptions belong in an advanced section. Unsupported combinations cannot be saved silently.

---

## 22. Performance and resource budgets

The application should remain a light desktop utility. These are **targets to measure**, not conditions to fake or reasons to omit correctness:

- On a documented ordinary Windows 11 x64 test machine, cached main window responsive within 3 seconds and input-to-feedback usually below 100 ms.
- With 5,000 synthetic candidates, filtering/sorting typically within 200 ms and no whole-list redraw for every probe event.
- Idle disconnected CPU close to zero; target below 1% of one logical CPU over a 60-second sample. Connected-idle CPU must also be measured separately from real traffic.
- Aim for desktop + broker below 200 MiB steady working set and core/probe memory reported separately; set a measured total budget after the first vertical slice. Avoid loading a 50,000-node runtime profile just because the import limit permits it.
- Cancel network operations within 2 seconds where possible, with a bounded hard-stop deadline for stuck child processes. Verify all workers and temporary allowances are gone afterward.
- No unbounded task queues, event logs, sample history, retry loops, API connections, or process counts.

Report hardware, dataset, build mode, measurement window, and workload with any performance result. TUN throughput depends on protocol, server, network, core stack, and CPU; do not present a synthetic UI benchmark as VPN throughput.

Use async I/O, bounded channels, virtualized rows, cancellable operations, batched events, and incremental data queries. Do not prematurely add a database server, message broker, container runtime, or background cloud service.

---

## 23. Packaging, installation, and updates

### 23.1 Installer deliverable

Deliver a conventional Windows x64 installer containing the self-contained desktop/runtime, broker, recovery entry point, approved core, and required approved driver components. Choose and pin a maintained installer tool after checking its current distribution/license terms. Installation must not depend on Python, Node.js, Docker, a developer SDK, or a separate client.

Installer responsibilities: elevated installation into protected paths, service registration with restrictive permissions, clear publisher/product information, shortcuts, offline recovery access, versioned uninstall, and transactional rollback on failure. Do not alter networking during installation unless necessary and explicitly documented; the first connection performs the actual tunnel setup.

A portable ZIP is optional and not the primary path, because service/TUN setup still needs authorization. Do not describe an archive as fully portable when it installs machine-wide components behind the user's back.

### 23.2 Dependency supply chain

Pin SDK/NuGet versions and use lock files where supported. Record the core release, architecture, exact asset URL, binary SHA-256, upstream source tag/commit, license, and retrieval date in a machine-readable manifest. Verify the actual Wintun/driver distribution route used by the selected core; do not assume every build requires or bundles the same DLL. Use only approved signed driver packages, never require disabling driver-signature enforcement. [S12] [S17]

Do not download binaries from subscription mirrors or user-entered update URLs. Hashes must come from an independently reviewed/pinned build manifest, not merely an untrusted adjacent `checksums.txt`. Record which upstream signatures/attestations are actually available and verified; do not invent them.

Publish a dependency/license inventory and SBOM. Reused parser code must have attribution and a compatible license. Live subscription lists are runtime data and must not be republished in the application repository just because their source is publicly readable.

### 23.3 Updating the application/core

For v1, a manual **«Проверить обновления»** action and explicit installer download are sufficient. Do not build a silent self-updater before safe networking works. An optional periodic release notification must not replace an active core automatically.

Core upgrades occur as tested application releases or explicit staged maintenance operations: fetch/verify → validate representative configs → retain previous binary/profile → switch behind protection → verify → commit or roll back. Do not call the core's own unrestricted upgrade API.

An unsigned personal/test installer must be labeled as such. Include a normal code-signing path for future releases, but never claim Authenticode signing without a real valid signature. Do not instruct the owner to disable antivirus or Windows security protections globally. Unsigned private-test status and public-release readiness are different.

### 23.4 Upgrade and uninstall tests

Test clean install, upgrade with database migration, upgrade rollback, reboot, service restart, cancellation, uninstall while disconnected, and uninstall while connected. Uninstall must remove app-owned service/network effects and ask whether to preserve user preferences/history; do not delete unrelated configuration.

No orphaned WFP filters, routes, DNS overrides, adapters, worker processes, services, startup entries, or private plaintext profiles may remain unintentionally. A shared driver must not be removed in a way that breaks another application.

---

## 24. Repository layout and development conventions

A suggested layout follows. Small justified adjustments are allowed; do not create empty projects merely to match a diagram.

```text
vpn/
  AGENTS.md
  README.md                         # Russian owner-facing quick start
  LICENSE
  THIRD_PARTY_NOTICES.md
  global.json
  Directory.Build.props
  Directory.Packages.props
  AutoVpn.sln
  src/
    AutoVpn.Desktop/                 # WPF views, resources, view models
    AutoVpn.Domain/                  # identity, eligibility, ranking, merge policy
    AutoVpn.Application/             # refresh/probe coordination, use cases
    AutoVpn.Infrastructure/          # HTTP, SQLite, import, core adapter
    AutoVpn.Contracts/              # typed IPC and versioned public DTOs
    AutoVpn.Service/                 # privileged broker and recovery journal
    AutoVpn.Windows/                 # minimal Windows interop/recovery logic
  tests/
    AutoVpn.UnitTests/
    AutoVpn.IntegrationTests/
    AutoVpn.WindowsTests/
    AutoVpn.UiTests/
    fixtures/                       # synthetic/redacted; no live credentials
  packaging/
  scripts/
    bootstrap-dev.ps1
    build.ps1
    test.ps1
    test-windows-admin.ps1
    package.ps1
    verify-release.ps1
  config/
    source-manifest.json
    probe-targets.json
    core-manifest.json
  docs/
    IMPLEMENTATION_DIRECTIVE.md
    ARCHITECTURE.md
    SECURITY_MODEL.md
    SOURCE_COVERAGE.md
    PROTOCOL_COMPATIBILITY.md
    WINDOWS_TEST_PLAN.md
    USER_GUIDE.ru.md
    RECOVERY.ru.md
    IMPLEMENTATION_STATUS.md
    HANDOFF.md
    adr/
    evidence/                       # sanitized reproducible summaries only
  .github/workflows/
```

Enable nullable reference types, analyzers, deterministic domain tests, and consistent formatting. Keep the Windows interop surface small, well-commented, and ownership-safe. UI code must not call elevated networking operations directly.

Use dependency injection only where it improves testability; avoid a custom plugin system, generic workflow engine, event-sourcing platform, or enterprise framework. Prefer mature maintained libraries to bespoke YAML/crypto/network-protocol implementations.

`AGENTS.md` must include Russian owner communication, the delivery repository, preservation of owner work, secrets rules, no false Windows claims, and the requirement to commit/push verified increments. `HANDOFF.md` records the last verified commit, active milestone, exact commands/results, remaining blockers, and next concrete step so compaction/resume does not restart the design discussion.

---

## 25. Implementation sequence

### Milestone 0 — Inspect, pin, and establish safe execution

Inspect the current target repository, preserve existing work, read this directive in full, and save it as `docs/IMPLEMENTATION_DIRECTIVE.md`. Record the environment and whether real Windows execution/admin access is available. Confirm the current upstream tree and core compatibility, pin dependencies, and create a compact tracked milestone list.

Before any live networking mutation on the owner's machine, determine whether it would interrupt Grok's own SSH/RDP/control path. Establish a tested local or out-of-band recovery path first. Do not perform destructive failover/firewall tests through the only connection that those tests may sever. Use a disposable Windows VM/test machine for destructive scenarios. Without a safe path, complete non-disruptive work and mark the hazardous live tests `NOT_RUN`.

Deliver an initial repository commit, reproducible build/test scripts, and the first sanitized source/capability inventory. Do not spend this milestone creating dozens of architecture documents instead of executable code.

### Milestone 1 — First real vertical slice

Build the smallest honest end-to-end path: one known valid synthetic/authorized profile → typed generation → installed broker → Windows TUN → verified browser/HTTP traffic → disconnect → clean network restoration. Show it in a simple but styled WPF connection screen.

Prove the protected-connect/disconnect foundation before adding thousands of public candidates. UI mocks may help design, but they cannot satisfy this milestone's runtime gate. Keep public candidate testing separate from the controlled transport fixture.

### Milestone 2 — Complete source ingestion and useful catalogue

Implement tree discovery, source manifest, all required family/representation adapters, safe parsing, identity, SQLite, coverage accounting, download caching/mirrors, and resumable local verification. Show only locally verified candidates in the working view. Add source controls and node details.

Deliver fixtures proving representation equivalence and important non-equivalences. Report current unsupported unique nodes; do not hide them to get a green summary.

### Milestone 3 — Durable pool and automatic recovery

Implement retention/merge, generation safety, network epochs, quarantine/retry, ranking, manual pinning, source/country restrictions, standby management, and bounded failover. Test a refresh while connected, failed new feeds, removed-but-working old nodes, and disconnect racing reconnect.

### Milestone 4 — Metrics and finished desktop UX

Add honest latency, live rates/session totals, bounded manual download/upload tests, optional budgeted automatic speed sampling, country/protocol metadata, favorites, tray, themes, accessible layouts, and useful Russian diagnostics. Inspect actual screenshots and fix clipping, spacing, incomplete translations, and broken controls.

### Milestone 5 — Windows hardening and distributable build

Finish persistent protection/crash recovery, privilege/IPC abuse tests, installer/upgrades/uninstall, DNS/IPv6 leak checks, sleep/resume, competing-VPN handling, secret redaction, supply-chain verification, and performance measurements. Produce the installer and evidence bundle tied to the same commit.

### Milestone 6 — Owner-ready handoff

Run the acceptance battery below, commit/push the final verified changes, verify remote HEAD, and provide the owner with Russian installation/use/recovery instructions, the release artifact/checksum, a concise feature summary, and honest remaining limitations.

Milestones are dependency order, not a request to pause after each one for generic approval. Continue until complete or genuinely blocked by unavailable access/credentials/hardware. Preserve tested work at every handoff; never turn a missing Windows machine into permission to assert a Windows pass.

---
## 26. Acceptance journeys

Each journey needs a reproducible setup, command/actions, expected outcome, actual outcome, and evidence linked to the tested commit. Use controlled fixtures for deterministic conclusions; public-node results are additional time-stamped evidence.

### J01 — Clean install and first real connection

On a clean supported Windows x64 system, install without developer tools. Launch unelevated, accept the brief disclosure, fetch built-in sources, and observe verification progress. Connect to a verified node. Prove ordinary IPv4 application traffic traverses TUN, not an application proxy setting. Show the actual node/country/status and a successful protected disconnect. No manual YAML editing is required.

### J02 — Complete source-family accounting

Discover every relevant source artifact in a frozen upstream tree. Account for VPN families, duplicate/equivalent representations, malformed entries, unsupported unique entries, Tor bridges, and non-data assets. Add a synthetic new family outside the initial seed list and confirm discovery. A truncated tree or unavailable directory must produce incomplete coverage, not a false complete result.

### J03 — No untested new nodes in the usable list

Import a batch containing valid working nodes, a reachable TCP listener with an invalid proxy handshake, wrong credentials, a TLS failure, an HTTP captive portal, unsupported transport, and an ordinary timeout. Only nodes passing the real candidate-path admission policy become selectable in **«Рабочие»**. Others have precise reasons. Upstream “active” labels do not bypass the gate.

### J04 — Refresh retains old working nodes

Start with A and B working. Next source snapshot removes A, retains B, adds working C and broken D. After local validation, the usable pool contains A, B, C, with A labeled retained; D never appears as working. B's history survives; the current A session is not interrupted. Later make A fail and confirm removal from eligibility and quarantine.

### J05 — Identity survives renames without collapsing variants

Import the same configuration through TXT, Base64, and YAML under different names and sources. It yields one node with multiple memberships. Then change only the TLS fingerprint, password, SNI, or WebSocket path; each relevant difference yields a separately tested identity. Verify the same-endpoint fingerprint case seen in the real upstream.

### J06 — Update failure is non-destructive

Simulate 304, 404, 429/Retry-After, timeout, HTML 200, malformed YAML, valid empty content, decompression/size limit, off-registry redirect, mirror lag, and canceled download. Existing good snapshots and eligible retained nodes are not wiped. All outcomes are correctly distinguished in Russian. No source error causes a healthy tunnel restart.

### J07 — Candidate tests do not ride the active VPN

Keep production A working and set candidate B to fail. Probe B while TUN uses A. B must fail. Verify expected physical egress and absence of chained routing. Repeat concurrently with multiple candidates and with a country-filtered active session. No probe changes the production selection or creates its own TUN adapter.

### J08 — Automatic failover and pinning

Connect to A with B prevalidated. Break A, leaving the physical network and test targets available. Confirm outage detection, protected switching to B, production-path verification, one useful notification, and a recorded reason/timing. Repeat with a strict country filter and then a pinned A: do not select an out-of-scope node or silently unpin A.

### J09 — All nodes fail or the physical network disappears

Make every candidate fail, then separately unplug the underlying network. Distinguish those conditions. Stay blocked while protection is armed; do not use `DIRECT`, create a retry storm, or mark the whole catalogue permanently dead. Restore the network and verify bounded revalidation/recovery.

### J10 — DNS and IPv6 leak tests

Use packet capture and controlled external endpoints to test DNS over UDP/TCP, dual-stack traffic, IPv6 fallback, local-router DNS, application-provided HTTPS DNS, and multiple adapters. Verify that protected public traffic follows the advertised policy. A browser visiting an IP-check page alone is not sufficient evidence of DNS/IPv6 safety.

### J11 — Core and broker crash protection

During sustained ordinary application traffic, terminate the core; then run a separate test terminating the broker. Capture the interval before failure, during failure, and after recovery. The claimed traffic guard must remain effective. Ensure no accidental dynamic-filter cleanup opens direct traffic. Verify bounded restarts and recovery without a second core.

### J12 — Desktop crash and reconnection

Terminate only the desktop coordinator while a tunnel is active. The broker maintains protection and the active tunnel or a clearly defined bounded recovery state. Reopen the desktop; it reflects the existing authoritative session without duplicating effects. Confirm that full subscription refresh does not falsely claim to have continued while its coordinator was absent.

### J13 — Disconnect wins every race

Issue disconnect during connect, failover, speed test, subscription publication, and service reconnect. Delay old callbacks deliberately. After disconnect commits, none of those callbacks may reconnect or re-arm an obsolete session. A subsequent explicit connect is a new operation with a new generation.

### J14 — Sleep, resume, and uplink changes

Test sleep/resume and Ethernet↔Wi-Fi switching, including overlapping Windows network notifications. Old-epoch samples cannot authorize a new connection. TUN's own route changes do not create an infinite epoch loop. Revalidate selected/standby nodes and restore service gracefully.

### J15 — Honest performance information

Use a controlled proxy/target with known delay and rate limits. Verify measured milliseconds and Mbps/MB/s conversion, sample sizes, timestamp freshness, correct candidate attribution, live versus test rate, session-counter resets, and speed-budget cancellation. Unknown speed remains unknown. No synthetic value appears in a production build.

### J16 — Recovery without internet or catalogue

Corrupt/remove the desktop database in a disposable test system, interrupt a network-effect apply, and restart the broker. The offline recovery tool can display and remove only AutoVPN-owned effects. Foreign DNS/routes/firewall changes remain intact. Repeated repair is idempotent and does not need GitHub access.

### J17 — Installation lifecycle

Test clean installation, UAC cancellation, upgrade, migration rollback, normal reboot, unclean restart recovery, and uninstall while connected. Verify expected service startup policy and no unintentional leftover adapters, filters, routes, or credentials. Run on a machine without a .NET SDK.

### J18 — UI and Russian usability

Use all primary actions with mouse and keyboard. Inspect light/dark/high-contrast and 125/150/200% scaling, long Russian labels, unknown country, empty/large lists, tray, and notification navigation. No dead buttons, clipping of primary actions, frozen window during checks, untranslated owner-facing errors, or inaccessible status indicators.

### J19 — Security boundaries and malicious data

Run parser, filesystem, controller, and named-pipe abuse tests from section 27. Verify no unintended networking mutation, process execution, file write outside private paths, leaked credentials, or access by an unauthorized Windows user.

### J20 — Interrupted development/release resume

From a clean checkout of the pushed commit, follow the documented build/test/package commands. Confirm `AGENTS.md` and `HANDOFF.md` preserve Russian communication and implementation state. Release hashes correspond to actual artifacts from that commit; stale screenshots or tests from a different binary do not satisfy acceptance.

---

## 27. Adversarial and regression test matrix

### 27.1 Import and identity

Include fixtures for BOM/CRLF, Unicode, encoded separators, bracketed IPv6, missing/ambiguous Base64 padding, nested wrapper limits, oversized lines, invalid UUID/ports, duplicate JSON/YAML keys, YAML aliases/custom tags, escaped `\xNN` fields, null/empty/default distinctions, mixed schemes, malformed fragments, display-name collisions, and bidi/control characters.

Protocol fixtures must include SS userinfo and full-URI variants, supported plugin option mappings, VMess JSON, VLESS Reality/Vision and TLS options, Hysteria2/`hy2` aliases, Trojan, and TUIC when present. Prove that an unsupported plugin/transport is reported rather than dropped. Use synthetic credentials and documentation/test IP ranges only.

Property-based tests: canonical identity is stable under supported serialization changes; normalization is idempotent; different connection semantics are not collapsed; serialization followed by parsing preserves supported semantics; and parser resource use stays bounded for generated hostile inputs.

### 27.2 Fetch/discovery boundaries

Test pagination, truncated tree, cached manifest fallback, new/deleted/renamed paths, URL encoding of `+`, non-UTF-8/binary data, short reads, slow responses, gzip expansion, content-length lies, SHA/ETag mismatches, rate-limited mirrors, invalid redirects, and credentials embedded in a URL.

SSRF defenses must cover localhost aliases, private IP literals, IPv4-mapped IPv6, DNS rebinding, A/AAAA inconsistency, local proxy environment variables, cloud metadata endpoints, and a public hostname that changes to an internal address between validation and use. Test the effective connection target, not just initial string validation.

A remote full profile requesting `DIRECT`, extra listeners, local file paths, dashboard downloads, custom providers, certificate paths, scripts, update URLs, or new test targets must not gain those capabilities.

### 27.3 Catalogue state and time

Test partial transaction failure, process death between staging/commit/publication, schema upgrades/downgrades, WAL backup/restore, corrupted records, duplicate concurrent imports, a late result for changed credentials, removed family with shared nodes, disabled source, excluded favorite, and storage pressure.

Use a fake clock for TTL, retry, hysteresis, missed schedules, DST, UTC/local-time formatting, clock rollback, and sleep. A 304 does not refresh health; a name change does not erase health; a security-field change does invalidate it.

### 27.4 Network conditions and probe attribution

Test no internet, captive portal, blocked single target, two failed targets, known-good alternate target, TLS certificate error, protocol auth failure, TCP-only/UDP-only conditions, IPv4-only/dual-stack, packet loss, long latency, and throughput throttling. Provide evidence that failures are attributed to the correct layer.

Test shared endpoint pacing, large-source fairness, active-node priority, budget exhaustion, cancellation of every stage, worker crash, ports already in use, and resource cleanup. Exhausting a background budget must not silently disable essential active-session safety checks; reserve a small documented health-check budget and expose any exceptional extra traffic.

### 27.5 Windows effects and interop

Test privilege denial, absent/invalid driver, non-conflicting and conflicting TUN prefixes, firewall disabled/managed by policy, existing app connections before guard arming, DNS settings changed by another program, interface-index reuse, permission changes, corrupted effect journal, service startup races, and repeated disconnect/recovery.

Validate IPv4/IPv6 filtering on new and already-established sockets. Do not assume protecting only new connection authorization events covers every preexisting flow. Test filtered protocols beyond browser TCP where supported; unsupported public traffic must be blocked, not bypassed.

### 27.6 Secrets and delivery

Canary-secret tests cover normal/error/debug logs, filenames, process arguments, crash handlers, SQLite backups, Git diffs, screenshots, and diagnostic bundles. No live subscription keys are required in automated test fixtures.

Check package contents, executable/driver hashes, dependency licenses, service path quoting, DACLs, reparse-point attacks, malicious working directories, and untrusted DLL loading. Run static analysis and dependency/security checks available for the selected stack; report actual findings rather than treating a scanner's green status as proof of safety.

### 27.7 Tests versus infrastructure

Ordinary CI should be deterministic and not require live public VPNs or administrator network changes. Public integration tests are opt-in, rate-limited, and time-stamped. A public endpoint outage must not make all ordinary unit tests nondeterministic.

Privileged Windows tests run only on an authorized disposable runner/VM with a recovery route, not automatically on an arbitrary machine merely because a pull request arrives. Protect self-hosted runners from untrusted fork code. Separate build, synthetic integration, UI, Windows-admin, and public-network jobs in reports.

---

## 28. Release evidence and completion criteria

### 28.1 Evidence bundle

For a candidate release produce a sanitized manifest containing:

```json
{
  "schemaVersion": 1,
  "applicationVersion": "actual-version",
  "gitCommit": "actual-full-commit",
  "workingTreeClean": true,
  "build": {
    "sdk": "actual-pinned-sdk",
    "configuration": "Release",
    "target": "win-x64"
  },
  "core": {
    "version": "actual-version",
    "sha256": "actual-binary-digest"
  },
  "windows": {
    "editionBuild": "actual-tested-build-or-NOT_RUN",
    "adminTests": "PASS-or-FAIL-or-NOT_RUN"
  },
  "tests": [],
  "artifacts": [],
  "limitations": []
}
```

The example is a schema sketch, not evidence. Fill values from real commands. Each test entry records command/suite, environment, timestamp, exit code, pass/fail/skip counts, and the evidence path/hash. A skipped test does not become a pass. Group repeated runs correctly; do not double-count the same cases to inflate totals.

Required supporting artifacts include reproducible build logs, source-coverage and protocol reports, controlled TUN/failover measurements, sanitized DNS/IPv6/crash-protection evidence, actual UI screenshots, installer lifecycle results, release binary hashes, dependency notices, and Russian user/recovery documentation.

Packet captures can contain private traffic. Prefer isolated synthetic traffic, redact before sharing, and do not commit raw owner captures to the public repository. Evidence can state a locally retained sensitive capture with its hash and a sanitized analysis, without making it public.

### 28.2 Completion gates

**Code-complete** means implemented and automated tests pass for the claimed scope. **Windows-validated** means actual Windows TUN/network/recovery tests passed. **Owner-ready test release** means an installable artifact exists with known limitations and safe recovery. **Public release** additionally requires appropriate distribution/signing decisions and all advertised safety/compatibility claims to be supported.

A first v1 is accepted only when:

- The owner can install, see locally tested source-backed nodes, connect through TUN, refresh without unnecessary disconnects, and disconnect cleanly.
- New broken/untested configurations are excluded from the working pool; old missing-but-working configurations are retained and rechecked.
- Source discovery and unsupported/excluded material are accounted for honestly; no relevant family is silently lost.
- Automatic failover respects health, security, source, and country constraints, with no direct fallback while protected.
- Latency, test throughput, live traffic, country provenance, and status are correctly distinguished.
- DNS/IPv6/protection, privilege boundaries, crashes, and installation lifecycle have the required real Windows evidence.
- No secrets, downloaded live configuration database, or machine-specific sensitive artifacts were pushed to Git.
- The final checked code is committed and pushed to the specified repository; the remote commit and release/artifact identity are verified.

A clean compiler output, several screenshots, or a fake local server alone cannot replace these gates. An incomplete gate must be reported as incomplete, with the smallest next action required to finish it.

### 28.3 Final Russian report format

Give the owner a concise Russian report with: what is implemented; where to get the installer; the exact pushed commit; what was actually tested on Windows; how to connect/update/recover; and remaining limitations. Include no invented date, unsupported “100% secure” claim, or claim that every public server works.

Do not dump the entire internal backlog into routine chat updates. Communicate important milestones, blockers, risky environment limitations, and the final result. Keep all owner-facing discussion Russian even when code/documents are English.

---

## 29. Explicitly deferred features

Do not expand v1 into another Karing-sized client. Defer mobile/macOS/Linux ports, account/cloud sync, subscriptions marketplace, paid-provider onboarding, advertising, server deployment, Tor bridge integration, multi-hop/cascades, multi-engine orchestration, advanced per-process/domain routing editors, remote LAN control, arbitrary plugins/scripts, and a silent self-updater.

Also defer proactive periodic IP rotation, “AI best-server prediction,” continuous all-node speed tests, visual maps, and streaming-service unlock guarantees. These do not improve the requested basic connect/refresh/retain/failover experience enough to justify delaying it.

Small useful enhancements already included—favorites, strict country pinning, tray operation, honest measurements, reviewed mirror fallback, offline cache, privacy controls, and recoverable network changes—are part of making the simple application dependable, not excuses to add an enterprise platform.

---

## 30. Final instruction to Grok

Build the application in **https://github.com/alinescafs3mp-afk/vpn**. Follow the milestone order, keep the window simple and attractive, use the source repository as untrusted data rather than executable configuration, and make locally proven usability the basis of every green state.

Start implementing after the initial inspection. Do not answer this directive with another generic plan and then stop. Keep tested increments committed/pushed, preserve owner work, and leave a precise resumable handoff whenever execution must pause.

All communication with the owner is **Russian**. The finished deliverable is a working Windows client, its source/tests/installer/recovery documentation, and truthful evidence—not a promise that a Windows application should work.

---

## 31. Reference sources

The following primary sources were consulted on **2026-10-02**. Repository contents, dependencies, documentation, mirror availability, and release versions may change; recheck before implementation and pin the actual tested revision. This document's numeric budgets, UI choices, architecture, algorithms, and acceptance criteria are proposed engineering requirements, not measurements copied from upstream.

| ID | Source and relevance |
|---|---|
| [S01] | Igareck source README: subscription purpose, publication interval, formats, mirrors, categories. |
| [S02] | Igareck recursive repository tree: discovery input. Observed tree object SHA: `2292ca9d5cc7a9405cd08f603d3aee3ce3fae286`; this is a Git tree identifier, not a claimed release or commit SHA. |
| [S03] | Sample `BLACK_SS+All_RUS.txt`: metadata, URI forms, mixed protocols, explicit insecure-option cases. Public credentials were intentionally not copied into this directive. |
| [S04] | Sample Clash proxies-only export: typed YAML, escaped scalars, same-endpoint variants with different fingerprints. |
| [S05] | Upstream `MIRRORS.md`: mirror/raw-link inventory, requiring implementation-time validation. |
| [S06] | Karing README: beginner-oriented GUI, subscriptions/groups, Flutter and modified sing-box background. |
| [S07] | Karing license file: licensing and naming/association notice; review before any code reuse. |
| [S08] | Microsoft .NET support policy: .NET 10 LTS and supported-patch policy. |
| [S09] | Microsoft WPF .NET 10 changes: Fluent styling and desktop framework context. |
| [S10] | Mihomo TUN documentation: Windows behavior, stack choices, DNS limitations, platform-specific knobs. |
| [S11] | Mihomo API documentation: selection, per-node testing, traffic/configuration/control operations. |
| [S12] | Official Mihomo release page; `latest` resolved to `v1.19.32` during preparation. Pin a tested release; never treat the dynamic URL as a runtime trust policy. |
| [S13] | Mihomo VLESS documentation: an example of option-level compatibility that must be checked against the chosen binary. Other protocol/transport adapters require their corresponding official documentation and fixtures. |
| [S14] | Mihomo general configuration: controller authentication, named-pipe warning, listener scope and application-owned settings. |
| [S15] | Microsoft WFP operation: filtering-engine architecture; implementation needs layer/lifetime/arbitration-specific validation. |
| [S16] | Microsoft named-pipe security and access rights: explicit DACL and caller-access design. |
| [S17] | Official Wintun distribution/source page: Windows layer-3 driver, approved binaries and license/provenance review. |
| [S18] | Microsoft data-protection guidance: Windows protected data and scope considerations. |
| [S19] | Mihomo DNS configuration documentation: separate resolution roles and generated DNS policy. |

[S01]: https://github.com/igareck/vpn-configs-for-russia/blob/main/README.md
[S02]: https://api.github.com/repos/igareck/vpn-configs-for-russia/git/trees/main?recursive=1
[S03]: https://github.com/igareck/vpn-configs-for-russia/blob/main/BLACK_SS%2BAll_RUS.txt
[S04]: https://github.com/igareck/vpn-configs-for-russia/blob/main/Export/Clash/PROXIES_ONLY/WHITE-CIDR-RU-checked-clash-proxies.yaml
[S05]: https://github.com/igareck/vpn-configs-for-russia/blob/main/MIRRORS.md
[S06]: https://github.com/KaringX/karing/blob/main/README.md
[S07]: https://github.com/KaringX/karing/blob/main/LICENSE.md
[S08]: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
[S09]: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100
[S10]: https://wiki.metacubex.one/en/config/inbound/tun/
[S11]: https://wiki.metacubex.one/en/api/
[S12]: https://github.com/MetaCubeX/mihomo/releases/tag/v1.19.32
[S13]: https://wiki.metacubex.one/en/config/proxies/vless/
[S14]: https://wiki.metacubex.one/en/config/general/
[S15]: https://learn.microsoft.com/en-us/windows/win32/fwp/basic-operation
[S16]: https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights
[S17]: https://www.wintun.net/
[S18]: https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection
[S19]: https://wiki.metacubex.one/en/config/dns/

---

**End of implementation directive.**
