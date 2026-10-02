# ADR 0002 — Wintun is not inside the pinned Mihomo zip

Date: 2026-10-02

`mihomo-windows-amd64-v1.19.32.zip` was opened on the build host. It does not contain `wintun.dll`. The Windows executable hash is in `config/core-manifest.json`.

Wintun 0.14.1 was downloaded separately. `wintun/bin/amd64/wintun.dll` is 427552 bytes, SHA-256 `e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce`. Its license is the WireGuard LLC prebuilt-binary license, not GPL-3.0.

The DLL is not committed and is not in the application archive. A future installer must verify this hash and must not tell the user to disable driver signature enforcement. Windows loading of the DLL is `NOT_RUN`.
