# Security model

## Trust

The subscription repository is data. Import does not copy inbound listeners, routing, DNS, scripts, or a remote `DIRECT` policy into the generated profile. YAML aliases are rejected. Non-global tags outside the `tag:yaml.org,2002` core schema (str, map, seq, int, float, bool, null, binary) are rejected. Duplicate keys fail the import. Anchors are capped.

`skip-cert-verify` and plaintext VLESS, VMess, and Trojan are `PolicyBlocked` unless the user opts in. Reality and Shadowsocks AEAD are not treated as insecure merely because they have no web certificate. The default leaves insecure certificates off. The desktop copy says that turning protection off does not enable an unvalidated tunnel.

## Destinations

Public proxy hosts reject loopback, unspecified, multicast, link-local, RFC1918, CGNAT `100.64.0.0/10`, ULA, and cloud-metadata names. IPv4-mapped IPv6 is unmapped before the check. Documentation ranges `192.0.2.0/24`, `198.51.100.0/24`, `203.0.113.0/24`, and `2001:db8::/32` are allowed for fixtures.

## Process split

The desktop manifest is `asInvoker`. It cannot install a driver. The broker is the only component allowed to ask for a tunnel, and in this build it asks a guard that refuses.

The controller listens on loopback with a random secret of 24 bytes when a profile is built. The refusing controller never starts Mihomo, so that profile is not written by the default service path.

## IPC abuse

The dispatcher tests cover a remote pipe, a replayed request id, a forbidden `yaml` field, and an unknown operation. They fail closed. A second sequential snapshot on the local pipe succeeds for the same configured caller. That is not a Windows ACL test.

Payloads are not logged by the pipe server. SQLite tests use an XOR stand-in and check that the raw file does not contain the fixture password. DPAPI itself is `NOT_RUN`.

## Network effects

No filter, route, or DNS change is installed by this build. Recovery of a journal that still has open rows returns not completed and does not mark them removed. A corrupt journal is quarantined and reported as unreadable, not as an empty success.

## Supply chain

Mihomo and Wintun hashes are local SHA-256 computations. Upstream did not publish a signature or checksum asset for `v1.19.32`. Absence of a signature is recorded. It is not treated as a verification.

The application archive is unsigned. It is not Authenticode-signed. Do not disable antivirus or driver signature checks.
