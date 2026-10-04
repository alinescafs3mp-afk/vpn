> Historical V3 candidate record. Current continuation: `docs/checkpoints/V3B.md` and `README_V3B_RU.md`. The original NOT_BUILT status describes the earlier delivery only.

# Grok build/verification handoff: V3 source candidate

Communicate with the owner in Russian. Scope: BUILD / VERIFY / PACKAGE only. Implementation remains with Astra. The new source has NOT been compiled or executed in its authoring environment. Do not describe it as a finished V3 or a usable VPN.

## Identity and safe starting point

Read the outer `README_RU.md`, `VALIDATION.json`, `SOURCE_MANIFEST.json` and `source/docs/checkpoints/V3.md` first. Run outer `verify_source.py` with Python 3. Use `source/` as the repository root. Extract into a NEW directory, never overlay user data or an arbitrary worktree. This package preserves the 171-file canonical V2 source base at `70f9546d81ac58845c174d5dd4669dcd1fe27a5f`; exact modifications/new paths are in the manifest and patch.

The local authoring commit in the outer manifest is NOT a GitHub remote commit. No V3 source was pushed from this environment. For a Git-based build, use a clean worktree at the exact canonical V2 commit and run `git apply --check` before applying `changes-from-v2.patch`. Do not force-push or replace unrelated changes. A patch application or a build does not authorize implementing the unfinished Windows stack.

## Requirements

.NET SDK selected by `global.json`, currently 10.0.112, plus PowerShell 7. The ordinary solution includes cross-targeted Windows/WPF projects. Use an authorized Windows x64 machine for WPF and packaging. Native Linux runtime smoke requires the pinned official Linux core, not the Windows executable. Preserve all test failures, including prior unresolved Windows TLS_REJECTED behavior.

Use a fresh development profile or privately back up `%LOCALAPPDATA%\AutoVPN` first. Never commit the backup, SQLite files, runtime profiles, subscription content, credentials, dumps or packet captures. Do not install a test root CA globally. Do not enable TUN, alter host routes/DNS/firewall, weaken TLS/hash checks, or run the new runtime elevated to get a green result.

## Commands from source/

```powershell
dotnet --info
pwsh -NoProfile -File .\scripts\test-v3.ps1
$core = pwsh -NoProfile -File .\scripts\fetch-core.ps1
pwsh -NoProfile -File .\scripts\test-v3.ps1 -Native -CorePath $core
```

The wrapper runs the original `test.ps1` filters unchanged and retains per-stage logs/TRX in a unique `artifacts/v3-verification/<run>/` directory. Missing results never inherit a previous run's pass. Ordinary exclusions and explicit native/platform skips must be reported, not counted as passes.

`AstraV3ProfileTests.NativeRuntimeOwnsLocalPortsThenStopsItsExactProcess` is explicitly Linux-only for this checkpoint and requires core provisioning. Its Windows skip is NOT evidence of Windows ACL/process correctness. The other new suites are cross-platform controlled tests. Existing full native suites remain in place; do not broaden their skips.

On Windows, after the build and applicable tests pass:

```powershell
dotnet run --project tests/AutoVpn.AuditRound6Wpf -c Release -- artifacts/v3-wpf
pwsh -NoProfile -File .\scripts\package-v3.ps1 -IncludePinnedCore
pwsh -NoProfile -File .\scripts\verify-release.ps1 -PackageDirectory .\artifacts\AutoVPN-V3-win-x64-DEVELOPMENT
```

Packaging runs normal tests again and embeds their raw TRX. It does not run native/WPF/networking tests itself. Keep the earlier per-stage evidence. Successful compilation produces a DEVELOPMENT binary package only, not an installer, an SCM service or a computer-wide VPN.

## Inspection boundary

The desktop and console broker must come from the same build. The broker now constructs an actual supervisor, but the actual desktop Connect path still produces TUN and remains refused. Do not add SOCKS/direct fallback or remove the refusal. A console process is not a Windows service; do not register it with `sc create`.

For UI navigation, keep public-network consent unchecked. Public downloads/checks require explicit owner authorization and are not required for this handoff. Closing, reopening settings, pause/resume and explicit Exit can be inspected without contacting public nodes. Ctrl+C in the console attempts owned cleanup; forced termination/crash recovery is not proven.

## Return to the owner

Report in Russian the exact source manifest/commit identity, OS/SDK/core version/hash, actual commands, build result, passed/failed/skipped counts, all failed test names and untouched logs/TRX, and package size/SHA-256 if a package was actually created. Keep every failure; no assertion weakening, retry-until-green summaries or relabeling skipped checks.

A build/test blocker returns to Astra with evidence. Do not implement missing catalogue handoff, Windows hosting, TUN/WFP, recovery or runtime failover on your own. The next implementation belongs to Astra unless the owner explicitly changes that assignment.
