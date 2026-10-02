# Handoff

## Для владельца

Аудит 2026-10-02 лежит в этом дереве. Выпуска нет. Установщика нет. Туннель Windows не проверялся. Публичные узлы не измерялись. Папка `for_fix/` пустая и ждёт директивы Астры. Архив и его сумма описаны в `docs/IMPLEMENTATION_STATUS.md`. SHA коммита аудита записывает следующий коммит. `origin/main` после отправки — тот записывающий коммит.

## Last verified commit

Parent before the audit: `4425ce4a6fa93589ff8e2974fc1f84b3b156d3a0`

This handoff does not name the audit commit yet. The next commit records that SHA. Verify with:

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

Results: build 0 warnings; unit tests 36 passed and 0 failed with Mihomo unset; filtered core test 1 passed (`mihomo -t`, non-TUN, documentation address); publish produced PE32+ executables. Details and the archive hash are in `docs/evidence/build-manifest.json`.

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
