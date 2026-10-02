# Protocol compatibility

This matrix is the behavior of the current importer and `MihomoProfileGenerator`. It is not a claim that Mihomo accepted every row, and it is not a handshake result. The only native check recorded earlier is `mihomo -t` on one synthetic non-TUN VLESS profile. That binary was not re-run for this matrix.

Canonicalizer version is 2. Version 1 still trims the opaque fields below so a stored v1 digest can be recognized. A v1 match keeps the assessment and rewrites the digest. Any other stored digest drops the assessment. Favorites stay on the node either way.

## Closed security

| Protocol | Accepted import values | Plaintext (policy block unless noted) | Anything else |
|---|---|---|---|
| VLESS, VMess | empty/`none` (plaintext), `tls`, `reality` | empty/`none` | `Unsupported`, reason `UNSUPPORTED_SECURITY_OPTION`. Not emitted |
| Trojan | empty becomes `tls`; explicit `none` is plaintext; `tls`, `reality` | `none` | same unsupported reason |
| Shadowsocks | empty or `aead` on the security field. The cipher is separate | ciphers `none`, `plain`, `dummy`, or a missing cipher | unknown security is unsupported |
| Hysteria2, TUIC | empty becomes `tls`; `tls` | not classified as plaintext | unknown security is unsupported |

Clash Trojan without a `tls` key imports as `tls`. Explicit `tls: false`, `0`, or `no` imports as `none` and is policy-blocked. The generator writes `tls: true` only for `tls` and `reality`, and refuses any other value with `CORE_CONFIG_REJECTED` before a process starts.

`skip-cert-verify` is emitted only when the owner allowed insecure certificates. The generator does not turn certificate checking off to make a row pass.

## Emitter keys

| Field | Emitted key |
|---|---|
| VLESS encryption | `encryption` |
| VMess cipher (`scy`) | `cipher` |
| Shadowsocks cipher | `cipher` |
| Trojan, Hysteria2, TUIC SNI | `sni` |
| VLESS, VMess, Shadowsocks SNI | `servername` |
| `packetEncoding` | `packet-encoding` |
| Hysteria2 `up`, `down` | `up`, `down` |
| Hysteria2 `mport` / Clash `ports` | `ports` on `hysteria2` only |
| Shadowsocks `obfs-local` or `obfs` | plugin `obfs`; options `obfs`→`mode`, `obfs-host`→`host` |

## Transport

| Transport | Emitted | Rejected at generation |
|---|---|---|
| `tcp` / `raw` (raw becomes `tcp`) with no path and no gRPC name | `network` only | a path, host header, gRPC service name, or header type |
| `ws` | `ws-opts.path` and `ws-opts.headers.Host` | a gRPC service name on the same node |
| `grpc` | `grpc-opts.grpc-service-name` | a path |
| `http`, `h2` | `http-opts` | a gRPC service name; header type is allowed only here |

A path is not rewritten into WebSocket options for a different transport. Header type on `tcp` fails closed.

## Opaque bytes (canonicalizer v2)

Kept as imported, including leading and trailing spaces: path, gRPC service name, plugin options, obfuscation password, Reality `spx` (`spiderX`), Hysteria `up`, `down`, and hop ports. Empty strings become null. Hostnames, enumerations, ALPN tokens, and the host header are still trimmed. The password field was never trimmed.

## Not in this matrix

Xray import still reads the first `vnext` entry and the first user. Client routing fields (`freedom`, `blackhole`, `dns`, `dialerProxy`, TCP keepalive sockopts) are stripped, not stored as node identity. Unknown Clash keys and unknown share-link query keys are unsupported records, not silently ignored. No row above has a per-protocol `mihomo -t` or a live handshake in this increment.
