# Third-party notices

This is a written inventory, not a generated CycloneDX SBOM. Versions match the project files on the day they were recorded (2026-10-02).

| Component | Version | Role | License |
|---|---|---|---|
| .NET SDK / runtime | 10.0.112 | Build and host | .NET library license (MIT for the runtime libraries used here) |
| Microsoft.Data.Sqlite | 10.0.12 | Catalogue and effect journal | MIT. Bundles SQLite, public domain |
| System.Security.Cryptography.ProtectedData | 10.0.12 | DPAPI protector on Windows | MIT |
| YamlDotNet | 16.3.0 | YAML import | MIT |
| xunit | 2.9.3 | Unit tests | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Test runner | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | Test host | MIT |
| Mihomo (MetaCubeX/mihomo) | tag `v1.19.32`, commit `88dcbf7f1614a67c3b36b848ee3592dfa92ada36` | External core process. Not committed | The upstream `LICENSE` file at that commit was re-read on 2026-10-03 and is the GNU GPL version 3 text, not MIT. No SPDX `-only` or `-or-later` suffix is asserted from that generic text. The v1.19.32 GitHub release has no signature, attestation, or checksum asset. The SHA-256 values in `config/core-manifest.json` were computed locally from the downloaded archives and extracted binaries |
| Wintun 0.14.1 | `wintun/bin/amd64/wintun.dll` | Not linked and not shipped. Required later for a Windows TUN driver | WireGuard LLC prebuilt binary license, inside `wintun.zip`. Not GPL. Do not commit the DLL |

AutoVPN source is GPL-3.0-or-later. No Karing code or branding is included. Parser code in this repository was written here; it is not a copied upstream parser.

The subscription repository is runtime data. Its lists are not vendored.
