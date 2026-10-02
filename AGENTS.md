# AutoVPN agent instructions

## Language

The owner communicates in Russian. Talk to the owner entirely in Russian, including progress, limitations, installation, recovery, and the final report.

Keep code, identifiers, commands, protocol names, commit hashes, and upstream names in English. English technical documentation is acceptable. The application's default user interface is Russian.

## Repository

Delivery repository: https://github.com/alinescafs3mp-afk/vpn

Subscription data source (untrusted input, never an executable profile): https://github.com/igareck/vpn-configs-for-russia

Preserve existing owner work. Do not force-push, overwrite unrelated history, or create `main` when the remote already has commits.

Commit coherent tested increments and push them to the default branch. Verify the remote commit after pushing. `git push` is not a Windows deploy and is not release readiness.

## Secrets and generated state

Do not commit live subscription bodies, credentials, runtime profiles, private diagnostics, packet captures, or databases. `artifacts/`, `third_party/`, and `*.sqlite` are gitignored. Hashes of pinned binaries may be recorded. The binaries themselves stay outside git.

## Windows claims

A Linux build, a unit-test pass, or `mihomo -t` on a synthetic non-TUN profile is not proof of Windows TUN, DNS, IPv6 protection, crash recovery, pipe ACL, tray behavior, or the installer.

Mark those gates `NOT_RUN` until they run on an authorized disposable Windows machine. Do not invent availability, latency, speed, geography, or a green Connected state. Public nodes in the subscription have not been measured.

## This machine

The implementation host is Linux. Do not change its routes, firewall, TUN, or DNS. Do not start Mihomo with `tun.enable: true` here. The default network guard does not install filters. Recovery must not report rules as removed when it did not remove them.

Destructive network tests belong on an authorized disposable Windows environment, and only after a recovery path exists.

## Product boundary

Baseline: C# / .NET 10, WPF, pinned official Mihomo, SQLite, and a minimal privileged Windows broker. Change a major choice only with a short ADR that records a demonstrated incompatibility.

Do not expand v1 into a general networking platform. Do not add telemetry, a silent updater, Karing branding, or a DIRECT fallback for protected traffic.
