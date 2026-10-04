# Grok build handoff: AutoVPN / Astra R1

Communicate with the owner entirely in Russian. This is a build/verification handoff, not a new assignment to implement the remaining VPN product. Preserve this source, run the commands, report actual outcomes and deliver artifacts. Do not quietly modify acceptance criteria or introduce networking changes on your Linux control machine.

## Delivery boundary

This cumulative source contains Astra's first implementation checkpoint after audit round 6. It is **not the final Windows VPN release**. It fixes important publication/snapshot/lifecycle/replay defects and implements same-owner Windows pipe identity/ACL handling, but the existing production core/guard composition still refuses TUN. SCM installation, production TUN/WFP, narrow privileged runtime handoff, maintained two-target admission, complete UI metrics and installation/recovery acceptance remain unfinished. Compiling the executable does not add those features.

Do not call the resulting files a working computer-wide VPN. The package is marked DEVELOPMENT and contains an explicit warning. Never disable the refusing adapters or return synthetic success to satisfy the owner. Remaining implementation stays with Astra unless the owner explicitly reassigns it.

## Prerequisites

Use an authorized Windows x64 machine with PowerShell 7, Git (for repository checkout) and the .NET SDK selected by `global.json` (10.0.112 at this checkpoint). The self-contained output needs no separately installed .NET runtime. Internet access is needed for NuGet restore, optional official core download and native tests. Native tests use loopback test peers only and do not enable TUN, install a CA, change firewall rules or install a driver.

Keep an untouched copy of existing `%LOCALAPPDATA%\AutoVPN` user data before testing new code. Never put that copy, live subscription bodies, generated runtime profiles, logs containing credentials or databases in Git or a public artifact.

## Get and verify source

For a repository checkout, use the exact delivery commit supplied with the handoff, not an assumed latest branch. Run `git status --short` and keep owner changes intact. For the cumulative source ZIP, extract into a NEW empty directory; do not overlay arbitrary user work. The outer archive SHA-256 and inner `SOURCE_MANIFEST.json` identify delivered bytes. Source archives do not include `.git`, the SDK/NuGet cache, credentials, databases or build output.

From the repository root in PowerShell 7:

```powershell
dotnet --info
pwsh -NoProfile -File .\scripts\test.ps1
```

The normal suite excludes explicitly provisioned native suites and the exploratory S603 download-policy assertion. Optional native facts report skipped without the core. Linux-shell fixtures report skipped on Windows; the managed child process test runs on both OSs. A skip is not a pass. Preserve the TRX under `artifacts/test-results`.

## Real native tests, without TUN

```powershell
$core = pwsh -NoProfile -File .\scripts\fetch-core.ps1
pwsh -NoProfile -File .\scripts\test.ps1 -Native -CorePath $core
```

Archive and executable SHA-256 must match `config/core-manifest.json` before running Mihomo. The script sets platform-correct R5/R6 native test environment variables. The fixture's proxy-only self-signed-certificate allowance never grants trust to an invalid target HTTPS certificate. This is not a Windows TUN or all-protocol acceptance test.

A download/hash failure is a blocker for native tests, not permission to substitute a different unreviewed binary. Do not use development/latest tags. No core or driver binary belongs in Git.

## Build a self-contained Windows EXE bundle

```powershell
pwsh -NoProfile -File .\scripts\package.ps1
```

This executes normal tests and publishes `desktop`, `service`, `recovery` and `inventory` folders for win-x64. Optional `-IncludePinnedCore` adds the verified official core and its licensing/source notices. `-SkipTests` exists only for explicitly marked diagnostic packaging; never hide that flag in a claimed tested release.

Output:

```text
artifacts/AutoVPN-astra-r1-win-x64-development.zip
artifacts/AutoVPN-astra-r1-win-x64-development.zip.sha256
```

Run the integrity verifier after extraction:

```powershell
pwsh -NoProfile -File .\scripts\verify-release.ps1 -PackageDirectory .\artifacts\AutoVPN-astra-r1-win-x64-development
```

The EXE entry points are `desktop\AutoVpn.Desktop.exe`, `service\AutoVpn.Service.exe`, `recovery\autovpn-recovery.exe` and `inventory\autovpn-inventory.exe`. The service executable is still a current-user CONSOLE broker, not an installed SCM service. Starting it does not make TUN work. Do not register this console program with `sc create` or request administrator rights as a substitute for implementation. Neither an installer nor an Authenticode signature is claimed.

## Safe UI smoke and shutdown

Use a new Windows test profile or an explicitly backed-up profile. Launch the console broker under the SAME user as the desktop, then the desktop EXE. Without public-network consent, inspect navigation and settings, close-to-tray and explicit Exit. Verify settings survive a normal reopen. Stop the console with Ctrl+C after the UI exits. The GUI must distinguish an unreachable broker from a verified disconnected state.

Do not perform destructive networking, route, DNS, WFP or recovery experiments on the host carrying your only control connection. Those tests belong to a later authorized isolated environment with an independent recovery path and an actually implemented service/guard.

## Diagnostics

IPC protocol is now version 2. Both processes must come from the same build. Stop old console instances before starting the new pair. `SESSION_REQUIRED` means a new lease handshake is needed; never strip sequence/lease fields to bypass it. Long-running requests must retain their original sealed envelope when retried; repeating a command with a new identity can repeat its effects.

A stale catalogue writer returns `CATALOGUE_CONFLICT`; do not delete the owner's database. Stop the conflicting test instance and reopen from durable state. Failed or malformed source updates preserve existing valid snapshots; a successful empty subscription has its own committed record.

Never claim that a passed unit test demonstrates Windows packet containment. Report problems by exact command, exit code, test name, OS/SDK/core version and the source commit. Keep private diagnostics local and redact before sharing.

## Required report to the owner

In Russian, provide the exact source commit or source-archive hash; SDK/OS/core identities; executed commands; passed/failed/skipped counts; ZIP size and SHA-256; whether signatures/installer/TUN are present; and remaining limitations. Include the DEVELOPMENT warning prominently. Do not present the partial checkpoint as release-ready, and do not restart another implementation/audit loop without the owner's explicit instruction.
