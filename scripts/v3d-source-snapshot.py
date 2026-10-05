#!/usr/bin/env python3
"""Retain exact tracked source and verify the intentionally unchanged TLS policy.

This script runs in CI. It never commits, pushes, installs or changes networking.
A source snapshot is not an assertion that tests have passed.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
import zipfile

BASE = "120bc69508340bbc1da2666a2a6ab05d5d20755e"
ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "artifacts/v3d-source"


def git(*arguments: str) -> bytes:
    return subprocess.run(["git", *arguments], cwd=ROOT, check=True, capture_output=True).stdout


def tls_segment(data: bytes) -> bytes:
    # Normalize checkout line endings only; do not normalize any code or policy.
    text = data.replace(b"\r\n", b"\n")
    return text.split(b"public readonly record struct TlsProbeExchange", 1)[1].split(b"public sealed class ProbeWorker", 1)[0]


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    path = "src/AutoVpn.Infrastructure/Probe/NonTunCoreProbeTransport.cs"
    assert tls_segment(git("show", f"{BASE}:{path}")) == tls_segment(git("show", f"HEAD:{path}")), "TLS/HTTP implementation changed"
    unchanged = ["config/core-manifest.json", "global.json", "src/AutoVpn.Infrastructure/Core/MihomoProfileGenerator.cs"]
    for item in unchanged:
        assert git("show", f"{BASE}:{item}") == git("show", f"HEAD:{item}"), f"Unexpected change: {item}"
    head = git("rev-parse", "HEAD").decode().strip()
    tree = git("rev-parse", "HEAD^{tree}").decode().strip()
    archive = OUT / "AutoVPN-V3D-source.zip"
    subprocess.run(["git", "archive", "--format=zip", "--prefix=source/", "-o", str(archive), "HEAD"], cwd=ROOT, check=True)
    files = {}
    with zipfile.ZipFile(archive) as bundle:
        assert bundle.testzip() is None, "Source ZIP CRC failure"
        for entry in bundle.infolist():
            if entry.is_dir():
                continue
            relative = entry.filename.removeprefix("source/")
            data = bundle.read(entry)
            assert data == git("show", f"HEAD:{relative}"), f"Archive mismatch: {relative}"
            files[relative] = {"size": len(data), "sha256": hashlib.sha256(data).hexdigest()}
    base_paths = set(git("ls-tree", "-r", "--name-only", BASE).decode().splitlines())
    assert base_paths <= files.keys(), "Base source paths were lost"
    patch = git("diff", "--binary", "--full-index", BASE, "HEAD")
    (OUT / "AutoVPN-V3D-from-V3C.patch").write_bytes(patch)
    index = {"schemaVersion": 1, "status": "SOURCE_SNAPSHOT_TEST_OUTCOMES_ARE_SEPARATE", "base": BASE,
             "commit": head, "tree": tree, "fileCount": len(files), "allBasePathsRetained": True,
             "unchangedTlsHttpImplementation": True, "unchangedFiles": unchanged,
             "sourceZipSha256": hashlib.sha256(archive.read_bytes()).hexdigest(), "files": files}
    (OUT / "SOURCE-MANIFEST.json").write_text(json.dumps(index, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in index.items() if key != "files"}, ensure_ascii=False))


if __name__ == "__main__":
    main()
