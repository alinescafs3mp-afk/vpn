# Handoff

## Для владельца

Выпуска нет. Установщика нет. Туннель Windows не проверялся. Публичные узлы не измерялись и не стали `Healthy`. Пакет A — `2b46431693fe58eb02c40a36a0b192ce000b6fed`. Пакет B — `1ba3cf78dd242141d8605286ac2a45101ad737c0`. Пакет C — `b532631694f81b438a3902da27d0a4e97f998ae6`. Статус каждой находки F01–F34: `docs/AUDIT_FIX_STATUS.md`. Архив и его сумма описаны в `docs/IMPLEMENTATION_STATUS.md`; архив не содержит эти коммиты. Этот коммит только именует SHA пакета C.

## Last verified commit

Package C code commit: `b532631694f81b438a3902da27d0a4e97f998ae6`

Package B remains `1ba3cf78dd242141d8605286ac2a45101ad737c0`. Package A remains `2b46431693fe58eb02c40a36a0b192ce000b6fed`. This handoff edit is a child of the package C commit. `origin/main` after the push is the child. Verify with:

```bash
git fetch origin
git rev-parse HEAD
git ls-remote origin refs/heads/main
```

The two published SHAs must match each other. Do not treat a Linux test log as a Windows pass.

## Active milestone

Milestone 1 is not closed. Package C is on the Linux pipe: a second client can cancel a blocked start, and confirmation is bound to one attempt. Windows identity (F05), TUN, DNS, recovery of real OS effects, and the installer stay blocked without an authorized disposable Windows machine. W4 in `docs/WINDOWS_TEST_PLAN.md` still requires that machine. Do not start it on the Linux build host.

The desktop now calls the unelevated coordinator. That process was not executed. The service still refuses the network and keeps its own catalogue. Do not mark a node `Connected` from this wiring.

## Commands already run

```text
dotnet build AutoVpn.slnx -c Release
dotnet test AutoVpn.slnx -c Release
AUTOVPN_MIHOMO_PATH=<linux mihomo v1.19.32> dotnet test tests/AutoVpn.UnitTests -c Release --filter FullyQualifiedName~PinnedLinuxCore
dotnet publish (Service, Recovery, Inventory, Desktop) -c Release -r win-x64 --self-contained true
```

Results after package C, 2026-10-03: solution build 0 warnings; `dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release` was 63 passed, 0 failed, 1 skipped with Mihomo unset. The skip is native `mihomo -t`, not a pass. The earlier filtered core test is not re-run for this commit. The archive was not rebuilt. Details are in `docs/evidence/build-manifest.json` and `docs/AUDIT_FIX_STATUS.md`.

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
