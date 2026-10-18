#!/usr/bin/env python3
"""Run local manifest checks and write a precise, machine-readable validation record."""
import hashlib
import json
import pathlib
import subprocess
import sys
import tarfile
import urllib.request
from datetime import datetime, timezone

BASE = pathlib.Path(__file__).resolve().parent
REPO = BASE.parent.parent
OUT = REPO / "artifacts/k3s"
schema_dir = OUT / "schema"
schema_dir.mkdir(parents=True, exist_ok=True)
for name in ("api__v1_openapi.json", "apis__apps__v1_openapi.json"):
    path = schema_dir / name
    if not path.is_file():
        path.write_bytes(urllib.request.urlopen("https://raw.githubusercontent.com/kubernetes/kubernetes/v1.34.1/api/openapi-spec/v3/" + name, timeout=60).read())
subprocess.run([sys.executable, str(BASE / "test-manifests.py"), "--schema-dir", str(schema_dir)], check=True)
subprocess.run([sys.executable, str(BASE / "test-upgrade.py")], check=True)
required = [
    "ARM64 supervisor, OpenVPN TUN and loopback listeners: passed",
    "ARM64 runtime inspection, secret whitelist and private backup: passed",
    "ARM64 add/reload and real HY2/XHTTP-REALITY authentication: passed",
    "ARM64 outer target restrictions: passed",
    "ARM64 readiness with one failed outer path: passed",
    "ARM64 revocation and outer reload: passed",
    "ARM64 graceful shutdown and persistent restart: passed",
    "ARM64 container smoke passed.",
    "ARM64 device RPC and operation-ID recovery: passed"
]
smoke_log = (OUT / "arm64-smoke.log").read_text()
if any(message not in smoke_log for message in required):
    raise SystemExit("ARM64 runtime smoke is incomplete; no validated package will be produced")
with tarfile.open(OUT / "eqlink-server-arm64.tar") as image:
    manifest = json.load(image.extractfile("manifest.json"))[0]
    config = json.load(image.extractfile(manifest["Config"]))
    assert config["architecture"] == "arm64" and config["os"] == "linux"
    assert config["config"]["Entrypoint"] == ["/usr/local/bin/EqLink.Server"]
    layer_hash = hashlib.sha256()
    with image.extractfile(manifest["Layers"][0]) as layer:
        while chunk := layer.read(4 * 1024 * 1024):
            layer_hash.update(chunk)
    assert config["rootfs"]["diff_ids"] == ["sha256:" + layer_hash.hexdigest()]
record = {
    "verifiedUtc": datetime.now(timezone.utc).isoformat(),
    "environment": "ARM64 binaries under QEMU in isolated Linux network/PID/mount namespaces, read-only image root with writable test volumes",
    "officialKubernetesOpenApiVersion": "v1.34.1",
    "officialKubernetesResourceSchemasPassed": 6,
    "dockerArchiveArchitectureAndLayerDigest": True,
    "containerdImageImportTested": False,
    "runtimeSmoke": required,
    "targetK3sApplied": False,
    "twoSitePublicNetworkTested": False,
    "windowsOpenVpnTunTested": False,
    "openVpnClientHandshakeTested": False,
    "peerInterconnectTested": False,
    "physicalNicNoLeakCaptureAccepted": False,
    "openVpnActiveSessionRevocationTested": False,
    "sixtySecondRecoveryAccepted": False
}
(OUT / "VALIDATION.json").write_text(json.dumps(record, indent=2))
print("Image archive and validation record: passed")
