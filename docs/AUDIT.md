# Audit 2026-10-02

Read of the source on the Linux build host. No Windows run. Findings below are either fixed in this tree or left open on purpose.

## Fixed here

- Profile scalars with control characters are rejected before any guard runs. `skip-cert-verify` is not emitted unless the owner allowed insecure certificates. Plugin options that were parsed are written back as a map. `spider-x` is emitted. Duplicate core names are rejected.
- A connect that loses the race with disconnect stops the core and does not report success. A failed start after a successful arm disarms that same generation. A second connect is refused while protection is still armed.
- Disconnect disarms the generation that was armed, not the generation created by the disconnect itself.
- Failover no longer treats a cooldown or a target outage as a blocked tunnel. Switches are counted on a one-minute window and then held for 60 seconds. A switch starts the core for a catalogue node that is eligible now; an invented standby id is dropped. The reported cost is recomputed.
- Automatic selection uses the existing rank cost. Unknown latency is not treated as zero.
- The probe loop no longer stops after eight nodes. It stays sequential so an uplink failure cannot be overtaken, and a time budget leaves the rest `Pending`.
- A non-zero `ExpectedStateRevision` that does not match is `STALE_REVISION` and does not change the session.
- Forbidden IPC fields are detected inside nested objects. A dispatcher exception becomes a closed error response.
- On Linux the pipe server checks `SO_PEERCRED` and rejects a different uid. The Windows SID is still not read.
- JSON that is simply broken is `INVALID_URI`. Only a duplicate key is `DUPLICATE_KEY`.
- A policy block is cleared when the same node is imported again under the owner allow.
- Memory settings use the same validation rules as the SQLite store.
- An effect journal whose schema is not version 1 is left in place. Recovery exits 2 and does not claim the rules were removed.
- Fetch rejects userinfo on an otherwise allowed HTTPS URL. Git tree entries that are not blobs are not inventoried.
- The connect button offers disconnect while the phase is busy or protection is still armed. That is not a Connected label.
- `scripts/package.ps1` publishes into `desktop`, `service`, `recovery`, and `inventory`. It was not executed. The hashed tar was packed separately and already uses those names.
- `docs/RECOVERY.ru.md` exit 2 covers an unsupported schema left in place, not only a quarantined file.

## Left open

- Windows TUN, WFP, DNS, IPv6, crash recovery, installer, Authenticode, UI clicks, and a second Windows user's pipe ACL are `NOT_RUN`.
- The service on Windows still stamps `windows-user`. It does not impersonate the pipe client.
- The desktop does not refresh subscriptions and does not list measured nodes.
- No public node was probed. `config/*.json` is not loaded by the process.
- `header-type`, hysteria bandwidth hints, and a real Mihomo acceptance of every protocol are not covered by `mihomo -t`. The pinned check is still one synthetic non-TUN VLESS profile.
- Failover updates the in-memory phase only after the refusing or test core returns. There is no live tunnel to switch.
- Astra's later directives belong in `for_fix/`. This audit does not put tasks there.
- Two starts of `dotnet test AutoVpn.slnx` aborted with `Internal CLR error (0x80131506)` before a result. A later run of the same command passed 36/0. That abort was not reproduced as a failing test.
