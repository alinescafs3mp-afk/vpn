# Source coverage

Pinned tree: `igareck/vpn-configs-for-russia` commit `20c38289c29e4dba6b8f01ddd3273ec9ec169b46` (2026-10-02T19:30:30Z, message «Черные Списки VLESS / Все конфиги [v2rayN]»).

The Git trees API with `recursive=1` returned `truncated: false`. `GithubTreeParser` treats a truncated or missing tree as `DISCOVERY_INCOMPLETE` and returns no paths, so a partial tree cannot delete the catalogue.

Inventory at `docs/evidence/source-inventory.json`, retrieved 2026-10-02T19:37:25Z. Counts are path hits, not nodes.

| Class | Paths |
|---|---|
| Workflow | 7 |
| VpnUriList | 15 |
| Base64Export | 7 |
| ClashFull | 10 |
| ClashProxiesOnly | 7 |
| JsonExport | 60 |
| Documentation | 5 |
| MirrorReference | 1 |
| Image | 133 |
| TorBridgeList | 5 |

Subscription-shaped paths: 99. Blob paths classified: 250. Directories are skipped.

| Family | Path hits |
|---|---|
| black-mixed | 39 |
| black-ss-weak-dpi | 1 |
| black-vless | 39 |
| black-vless-mobile | 39 |
| white-reality-mobile | 28 |
| white-cidr-all | 25 |
| white-cidr-checked | 25 |
| white-sni-all | 25 |

`MatchFamily` compares longer root names first, so `BLACK_VLESS_RUS_mobile` is `black-vless-mobile` and not `black-vless`.

Tor bridge lists and images are classified and are not imported as proxies. A comment-only TXT must not wipe nodes that still exist in that family's Clash file; membership is per artifact. That rule is unit-tested with fixtures, not by downloading the live files into git.

No public node from this tree was probed. Nothing in the table is a working-server count.

The advisory `profile-update-interval: 1` is clamped. The local minimum refresh is 15 minutes. The default is 120 minutes with 10 minutes of jitter. A missed interval replays once.
