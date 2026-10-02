# Handoff

## Для владельца

Выпуска нет. Установщика нет. Туннель Windows не проверялся. Публичные узлы не измерялись. Пакет A директивы из `for_fix/` записан в `2b46431693fe58eb02c40a36a0b192ce000b6fed`. Статус каждой находки F01–F34: `docs/AUDIT_FIX_STATUS.md`. Архив и его сумма описаны в `docs/IMPLEMENTATION_STATUS.md`; архив не содержит пакет A. Этот коммит только именует тот SHA.

## Last verified commit

Package A commit: `2b46431693fe58eb02c40a36a0b192ce000b6fed`

This handoff edit is a child of that commit. `origin/main` after the push is the child. Verify with:

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

Results after package A: build 0 warnings; unit tests 47 passed, 0 failed, 1 skipped with Mihomo unset. The skip is native `mihomo -t`, not a pass. The earlier filtered core test is not re-run for this commit. The archive was not rebuilt. Details are in `docs/evidence/build-manifest.json` and `docs/AUDIT_FIX_STATUS.md`.

`scripts/test-windows-admin.ps1` and `scripts/verify-release.ps1` exit 2 on purpose. They were not used as a green gate. `scripts/package.ps1` was not executed.

## Do not

- Start Mihomo with `tun.enable: true` on this host.
- Commit `artifacts/`, subscription bodies, or databases.
- Force-push.
- Report Connected, a country, or a latency that a probe did not measure.
- Treat `PipeOptions.CurrentUserOnly` as a completed Windows ACL test. On Linux `SO_PEERCRED` matches the service uid. On Windows the service still stamps `windows-user`.
- Put Astra directives anywhere except `for_fix/`.

## Pins

- SDK 10.0.112
- Mihomo `v1.19.32` commit `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`
- Source tree `20c38289c29e4dba6b8f01ddd3273ec9ec169b46`
- Archive SHA-256 `a4c142f9d88c85849278c6e7b0e13cdfc7a26975bdd3bf565d944a3ecd0c64cf`
- Archive bytes 134272704, packed 2026-10-02T19:58:42Z
