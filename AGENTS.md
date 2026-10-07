# AutoVPN / Throne fork instructions

## Owner and approved scope

Communicate with the owner entirely in Russian. Keep code identifiers and commands in English. The default application language is Russian.

On 7 October 2026 the owner explicitly approved replacing the former C#/.NET/WPF/Mihomo implementation with a fork of EXACT Throne 1.3.2, adding protected built-in subscription groups and an Update button to the right of TUN. See docs/adr/0001-throne-base.md. Do not resume the superseded .NET implementation as the active product.

Delivery repository: https://github.com/alinescafs3mp-afk/vpn. Work on main, preserve owner history, and never force-push. The complete former implementation is preserved at commit 2199f7ebafae3b6b8009b227f590f521863749cd and tag autovpn-pre-throne-2026-10-07. Check the current remote head before publishing and reconcile concurrent owner changes.

## Product baseline

Upstream: https://github.com/throneproj/Throne, tag 1.3.2, commit 9dd4fe9606185c147e46f218cf038b254fbcdc9c. Keep upstream networking/core behavior unless a specific change is approved and verified. The fork uses C++/Qt and the upstream Go core; it does not use Mihomo or the former privileged broker.

The first increment contains Default and four built-in groups: igareck, zieng2-wl, openproxylist, proxycollector. Treat subscription data as untrusted. Do not add telemetry, a silent application updater, unrelated branding, or a new direct-traffic fallback. Preserve GPL and third-party notices.

## Source and state

Do not commit live subscription bodies, node credentials, runtime profiles, databases, private diagnostics, packet captures, downloaded executables or build artifacts. Synthetic loopback-only test fixtures are permitted. Preserve user data and custom groups. Built-in identity must not depend on a translated name, tab position or an assumed free numeric ID.

## Verification boundaries

Builds and tests do not by themselves prove Windows TUN, DNS/IPv6 protection, crash recovery, installer behavior, or actual connectivity from the owner's network. Report observed results and mark unexecuted gates NOT_RUN. Never fabricate working nodes, speed, latency, geography or a Connected state.

The implementation host is Linux. Do not change its routes, firewall, TUN or DNS and do not start a real proxy/TUN core here. Use disposable test directories and synthetic localhost data for automation. Privileged networking tests require an authorized disposable Windows environment and a recovery path. Normal GUI smoke tests must disable automatic elevation, network downloads and automatic connection.

Commit coherent verified increments, publish through an authorized GitHub path, and verify the remote commit. A pushed commit is not deployment or release readiness. Do not repeat unchanged tests solely to accumulate successful runs.
