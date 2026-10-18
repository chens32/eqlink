#!/usr/bin/env python3
"""Assemble a Docker archive from the verified Debian ARM64 rootfs, without a daemon."""
import argparse
import hashlib
import io
import json
import os
import pathlib
import shutil
import struct
import subprocess
import tarfile
from datetime import datetime, timezone

from common import IMAGE


def digest(path):
    value = hashlib.sha256()
    with pathlib.Path(path).open("rb") as stream:
        while data := stream.read(4 * 1024 * 1024):
            value.update(data)
    return value.hexdigest()


def entry(archive, name, data):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mode = 0o644
    archive.addfile(info, io.BytesIO(data))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True, type=pathlib.Path)
    parser.add_argument("--rootfs", required=True, type=pathlib.Path)
    args = parser.parse_args()
    repo, rootfs = args.repo.resolve(), args.rootfs.resolve()
    if rootfs == pathlib.Path("/") or not str(rootfs).startswith("/tmp/eqlink-k3s-build."):
        raise ValueError("Expected an isolated EqLink build rootfs")
    for folder in ("etc/eqlink", "run/eqlink", "run/eqlink-bootstrap", "run/eqlink-tls"):
        path = rootfs / folder
        if path.is_dir() and any(path.iterdir()):
            raise ValueError("Refusing to bake runtime state or private material into the image: " + folder)
    source = repo / "artifacts/server/eqlink-server-arm64"
    target = rootfs / "usr/local/bin"
    target.mkdir(parents=True, exist_ok=True)
    hashes = {}
    for name in ("EqLink.Server", "hysteria", "xray"):
        content = (source / name).read_bytes()[:20]
        if content[:4] != b"\x7fELF" or struct.unpack("<H", content[18:20])[0] != 183:
            raise ValueError(name + " is not an ARM64 ELF executable")
        shutil.copy2(source / name, target / name)
        os.chmod(target / name, 0o755)
        hashes[name] = digest(target / name)
    scripts = rootfs / "usr/local/lib/eqlink"
    scripts.mkdir(parents=True, exist_ok=True)
    shutil.copy2(repo / "packaging/k3s/device-request.py", scripts / "device-request.py")
    licenses = rootfs / "usr/local/share/eqlink/licenses"
    licenses.mkdir(parents=True, exist_ok=True)
    for name in ("hysteria-LICENSE.md", "xray-LICENSE"):
        shutil.copy2(source / name, licenses / name)
    for folder in ("etc/eqlink", "run/eqlink", "run/eqlink-bootstrap", "run/eqlink-tls", "tmp"):
        (rootfs / folder).mkdir(parents=True, exist_ok=True)
    os.chmod(rootfs / "etc/eqlink", 0o700)
    os.chmod(rootfs / "tmp", 0o1777)
    (rootfs / "etc/apt/sources.list").write_text("deb https://deb.debian.org/debian bookworm main\ndeb https://security.debian.org/debian-security bookworm-security main\n")
    (rootfs / "etc/machine-id").write_text("")
    output = repo / "artifacts/k3s"
    layer = output / "rootfs-layer.tar"
    subprocess.run(["tar", "--numeric-owner", "--one-file-system", "--exclude=./proc/*", "--exclude=./sys/*", "--exclude=./dev/*", "-C", str(rootfs), "-cf", str(layer), "."], check=True)
    created = datetime.now(timezone.utc).isoformat()
    source_hashes = {}
    for folder in (repo / "src/EqLink.Server", repo / "src/EqLink.Core", repo / "packaging/k3s"):
        for path in sorted(folder.rglob("*")):
            if path.is_file() and not any(p in ("bin", "obj", "__pycache__") for p in path.parts) and path.suffix in (".cs", ".csproj", ".py", ".sh", ".json", ".md"):
                source_hashes[str(path.relative_to(repo))] = digest(path)
    provenance = {"createdUtc": created, "image": IMAGE, "architecture": "arm64", "builder": "debootstrap bookworm + verified ARM64 ELF files", "binarySha256": hashes, "sourceSha256": source_hashes, "debianPackagesSha256": digest(output / "debian-packages-arm64.txt")}
    config = {
        "created": created, "architecture": "arm64", "os": "linux",
        "config": {"User": "0:0", "Env": ["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", "LANG=C.UTF-8", "EQLINK_RUNTIME=container", "DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net"], "Entrypoint": ["/usr/local/bin/EqLink.Server"], "Cmd": ["serve", "--settings", "/run/eqlink-bootstrap/server-settings.json"], "WorkingDir": "/etc/eqlink", "StopSignal": "SIGTERM", "ExposedPorts": {"30443/tcp": {}, "30444/udp": {}}, "Labels": {"org.opencontainers.image.title": "EqLink ARM64 k3s test server", "org.opencontainers.image.version": "0.2.0-k3s-test", "org.opencontainers.image.description": "Single-instance OpenVPN over Hysteria2 or XHTTP+REALITY"}},
        "rootfs": {"type": "layers", "diff_ids": ["sha256:" + digest(layer)]},
        "history": [{"created": created, "created_by": "EqLink make-image.py: Debian packages and pinned transport executables"}]
    }
    config_data = json.dumps(config, separators=(",", ":")).encode()
    config_name = hashlib.sha256(config_data).hexdigest() + ".json"
    image = output / "eqlink-server-arm64.tar"
    with tarfile.open(image, "w") as archive:
        entry(archive, config_name, config_data)
        entry(archive, "manifest.json", json.dumps([{"Config": config_name, "RepoTags": [IMAGE], "Layers": ["layer.tar"]}]).encode())
        archive.add(layer, arcname="layer.tar", recursive=False)
    provenance["imageArchiveSha256"] = digest(image)
    provenance["imageConfigId"] = "sha256:" + config_name.removesuffix(".json")
    provenance["version"] = "0.2.0"
    provenance["sourceSha256"]["Directory.Build.props"] = digest(repo / "Directory.Build.props")
    (output / "PROVENANCE.json").write_text(json.dumps(provenance, indent=2))
    print("Docker archive built: " + str(image))


if __name__ == "__main__":
    main()
