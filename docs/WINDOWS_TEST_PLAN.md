# Windows test plan

Status of every row: `NOT_RUN`. The build host is Linux. These tests are allowed only on an authorized disposable Windows x64 machine, after the recovery tool can remove effects it actually created.

Do not run them on a machine whose only control path is the network under test.

| Id | Action | Pass when |
|---|---|---|
| W1 | Install the unsigned archive on a clean x64 system that has no .NET SDK | The desktop starts unelevated. Record the Windows build |
| W2 | Confirm the pipe ACL with a second local user | The second user cannot connect. Today's `CurrentUserOnly` flag is not that evidence |
| W3 | Point the broker at the pinned `mihomo-windows-amd64.exe` and `wintun.dll` after hash checks | Hashes match `config/core-manifest.json` |
| W4 | Connect one synthetic authorized profile with the real guard | Ordinary IPv4 traffic uses TUN. The UI says Подключено only after the production check |
| W5 | Disconnect | Routes, DNS, and filters owned by AutoVPN are gone. Foreign rules remain |
| W6 | Kill Mihomo, then kill the broker, as separate runs | Traffic does not fall through to DIRECT while protection is claimed |
| W7 | DNS UDP/TCP and IPv6 from a capture | Matches the profile policy. A public IP page is not enough |
| W8 | Crash the desktop only | The broker keeps its phase. Reopening the window does not add a second tunnel |
| W9 | Uninstall while connected and while disconnected | No orphan adapter, filter, route, service, or plaintext profile |
| W10 | Sleep, resume, and a NIC change | Old-epoch samples cannot authorize the new epoch |

Until W4–W7 pass, do not mark the release Windows-validated and do not call the archive an installer.
