# Grok: V3B build and verification only

Communicate in Russian. Implementation remains with Astra. Read `README_V3B_RU.md`, `docs/checkpoints/V3B.md` and the delivery source manifest first. Use a new extraction or a clean exact-commit checkout; preserve user data. No forced branch updates.

V3B has a locally executed Linux build/native suite, unlike the historical V3 candidate. Do not reuse V2 CI, old V3 NOT_BUILT text, or a passing workflow title as evidence of a different source. Retain raw command logs/TRX, exact commit, SDK/core hashes, failures and skips.

With SDK selected by `global.json` and PowerShell 7:

```powershell
$core = ./scripts/fetch-core.ps1
./scripts/test-v3.ps1 -Native -CorePath $core
# Windows only, fresh unconsented UI profile:
dotnet run --project tests/AutoVpn.AuditRound6Wpf -c Release -- artifacts/v3b-wpf
./scripts/package-v3.ps1 -IncludePinnedCore
```

The original Linux-only native supervisor case remains an explicit Windows skip. Report it. WPF includes a real DPAPI/read-only catalogue connection check using synthetic data; it does not test SCM, TUN, WFP, different users or a privileged broker. Never grant public consent just to obtain a green smoke test. Never weaken TLS, hash checks, runtime refusals, or the test assertions.

The development package has no installer/signature and still refuses system-wide VPN activation. Do not use `sc create` on the console host or register it as SYSTEM. A native core/controller readiness success is not proof of protected traffic. Return implementation failures with their evidence; do not silently finish the remaining Windows stack.
