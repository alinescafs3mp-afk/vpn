# Protocol compatibility

Import accepts share links, Clash proxy maps, and Xray outbound JSON for:

- VLESS, including Reality fields and `streamSettings.network: raw` mapped to `tcp`
- VMess
- Trojan
- Shadowsocks, userinfo and legacy base64
- Hysteria2 and the `hy2` alias
- TUIC

Unit tests cover a synthetic sample of each of those, plus insecure and plaintext policy blocks, private and metadata destinations, bracketed IPv6, an unsupported plugin report, YAML escapes, aliases, custom tags, duplicate keys, bidi stripping, and Xray policy fields that must be dropped (`freedom`, `blackhole`, `dns`, `sockopt.dialerProxy`).

Fingerprint variants of the same endpoint stay distinct digests. A URI and Clash `udp: true` share a digest. Explicit `udp: false` does not.

The profile generator writes those protocol names for Mihomo (`shadowsocks` becomes `ss`). The only core acceptance check that has run is `mihomo -t` on one synthetic non-TUN VLESS profile to `203.0.113.10`, with the Linux binary hash-checked. Exit 0. That does not accept Hysteria2, TUIC, TUN, or any public endpoint.

Unsupported plugins are reported. They are not silently dropped and not turned into a supported transport.

`dialerProxy` and TCP keepalive sockopts are client routing policy. They are stripped and accounted, not stored as node identity.
