# Handoff

## Для владельца

Срез 0.1.0 лежит в этом дереве. Выпуска нет. Установщика нет. Туннель Windows не проверялся. Публичные узлы не измерялись. Архив и его сумма описаны в `docs/IMPLEMENTATION_STATUS.md`. Идентификатор коммита после отправки — в отчёте и, следующим коммитом, в поле `gitCommit` манифеста.

## Last verified commit

`FILL_AFTER_COMMIT`

That value is the implementation commit. A later docs-only commit may record it. `origin/main` after the push is the commit to verify with:

```bash
git fetch origin
git rev-parse HEAD
git ls-remote origin refs/heads/main
```

The two published SHAs must match each other. Do not treat a Linux test log as a Windows pass.

## Active milestone

Milestone 1 is not closed. The next concrete Windows step is W4 in `docs/WINDOWS_TEST_PLAN.md` on an authorized disposable machine: hash-check the pinned Mihomo exe and Wintun DLL, replace `UnavailableNetworkGuard` only after a recovery path can remove the effects it creates, and connect one synthetic profile. Do not do that on the Linux build host.

Before that, the useful code gap is wiring the desktop to the catalogue and the refresh loop, still without claiming Connected.

## Commands already run

```text
dotnet build AutoVpn.slnx -c Release
dotnet test AutoVpn.slnx -c Release
AUTOVPN_MIHOMO_PATH=<linux mihomo v1.19.32> dotnet test tests/AutoVpn.UnitTests -c Release --filter FullyQualifiedName~PinnedLinuxCore
dotnet publish (Service, Recovery, Inventory, Desktop) -c Release -r win-x64 --self-contained true
```

Results: build 0 warnings; unit tests 29 passed and 0 failed with Mihomo unset; filtered core test 1 passed (`mihomo -t`, non-TUN, documentation address); publish produced PE32+ executables. Details and the archive hash are in `docs/evidence/build-manifest.json`.

`scripts/test-windows-admin.ps1` and `scripts/verify-release.ps1` exit 2 on purpose. They were not used as a green gate.

## Do not

- Start Mihomo with `tun.enable: true` on this host.
- Commit `artifacts/`, subscription bodies, or databases.
- Force-push.
- Report Connected, a country, or a latency that a probe did not measure.
- Treat `PipeOptions.CurrentUserOnly` as a completed Windows ACL test. The service still stamps a fixed caller id.

## Pins

- SDK 10.0.112
- Mihomo `v1.19.32` commit `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`
- Source tree `20c38289c29e4dba6b8f01ddd3273ec9ec169b46`
- Archive SHA-256 `bd314673d6947e7d14a385d1f5bdbe624e541a096053cd79fb9aa7ad2948baee`
