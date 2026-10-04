# Grok build handoff: AutoVPN V2

Communicate with the owner entirely in Russian. This is BUILD/VERIFY/PACKAGE work only. Remaining implementation stays with Astra unless the owner explicitly reassigns it. Preserve the original product specification and all audit gates. Do not turn a development checkpoint into a claimed working VPN by removing refusal/safety controls.

## What you are receiving

A complete cumulative source checkpoint based on V1, with V2 continuous two-target maintenance and its tests. Source, tests, configuration, scripts and historical directives are included. V2 is not an installed TUN/WFP product; compiling an EXE does not implement those missing functions. Read `docs/checkpoints/V2.md` and the outer validation report first.

Use the exact delivery commit from the owner's message. For ZIP delivery, extract into a NEW empty directory and use `source/` as the repository root. Do not overlay arbitrary work or delete user data. The outer `SOURCE_MANIFEST.json`, per-file Git/SHA-256 records and validation report identify the bytes. ZIP source does not contain `.git`, SDK/NuGet caches, live subscription bodies, credentials, databases, native binary caches or generated build directories.

## Requirements and safety

Use an authorized Windows x64 machine, PowerShell 7 and the .NET SDK selected by `global.json`. The current SDK pin is 10.0.112. Internet is required for NuGet and optional pinned core download. Native controlled tests use loopback peers, not public subscriptions and not TUN. Do not install a global test CA or relax target HTTPS verification. Use a fresh Windows profile or privately back up `%LOCALAPPDATA%\AutoVPN`; never commit that backup or its secrets.

The published EXEs are self-contained and do not need a separate .NET runtime. Native library/DLL directories must remain beside the corresponding EXE. An installer, driver installation, SCM registration and Authenticode signature are not supplied by this checkpoint. Do not use `sc create` on the current console broker as a substitute for implementing a Windows service.

## Commands, from source/

```powershell
dotnet --info
pwsh -NoProfile -File .\scripts\test.ps1
$core = pwsh -NoProfile -File .\scripts\fetch-core.ps1
pwsh -NoProfile -File .\scripts\test.ps1 -Native -CorePath $core
pwsh -NoProfile -File .\scripts\package-v2.ps1 -IncludePinnedCore
pwsh -NoProfile -File .\scripts\verify-release.ps1 -PackageDirectory .\artifacts\AutoVPN-V2-win-x64-DEVELOPMENT
```

Normal tests deliberately exclude explicitly provisioned native suites and the unresolved stronger S603 download-policy assertion. Optional native and Linux-only facts report skips where appropriate; a skip is not a pass. Preserve TRX before a subsequent command overwrites `artifacts/test-results/results.trx`. Native tests require exact official archive/executable SHA-256 from `config/core-manifest.json`; no arbitrary replacement binary or latest tag is accepted.

`package-v2.ps1` runs normal tests itself and produces:

```text
artifacts/AutoVPN-V2-win-x64-DEVELOPMENT.zip
artifacts/AutoVPN-V2-win-x64-DEVELOPMENT.zip.sha256
```

Omit `-IncludePinnedCore` to build without the core. Without it, provide the pinned executable via `AUTOVPN_MIHOMO_PATH` for candidate checks; otherwise the application must show core unavailable, not invent successful tests. With it the desktop finds `core/mihomo.exe` beside its folder and the existing hash verification still applies. Core inclusion is not TUN implementation or driver installation.

## Inspect and run safely

Extract the result into a fresh directory, verify its file manifest, read `READ-ME-FIRST.txt`, then launch `desktop\AutoVpn.Desktop.exe`. A same-owner development console broker can be started with `service\AutoVpn.Service.exe`; both EXEs must be from the same build/IPC v2 protocol. This console still refuses real TUN. Recovery and inventory binaries are included for their implemented development functions, not as a completed Windows network recovery service.

For a UI-only check, keep the public-network disclosure unchecked. Verify navigation, the maintenance consent-required state, automatic-check pause/resume and settings reopen. Giving consent enables source downloads and repeated candidate checks; only do so when explicitly authorized. Revoking consent stops new public-network work and cancels existing operations. The pause button pauses automatic checks, not a separate explicit foreground request. Closing the window hides it in the tray; explicit Exit waits for owned operations and broker safety state. A stuck operation is a failure to investigate, not permission to kill arbitrary processes or reset all networking.

## Known limits and reporting

The six-protocol native test fixture permits its own self-signed PROXY certificate explicitly, but the final HTTPS target still uses fixture-local validated trust. Do not describe that as strict proxy-certificate validation or Windows packet isolation. Prior intermittent Windows TLS_REJECTED evidence is retained with no established root cause; a successful rerun does not erase it. Report every run, exact failed test and environment. Do not blanket-skip tests, change assertions or suppress TLS failures.

In Russian, deliver the exact source commit/archive hash, SDK/OS/core versions, actual commands and passed/failed/skipped counts, ZIP path/size/SHA-256 and the DEVELOPMENT warning. State clearly that the build is not yet a usable computer-wide VPN. Return build blockers to the owner; do not start another unsolicited implementation or audit campaign.
