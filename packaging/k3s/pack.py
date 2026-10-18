#!/usr/bin/env python3
"""Package an existing ARM64 image with deployment tools and exact checksums."""
import hashlib
import json
import pathlib
import shutil
import tarfile

BASE = pathlib.Path(__file__).resolve().parent
REPO = BASE.parent.parent
OUT = REPO / "artifacts/k3s"
NAME = "eqlink-k3s-arm64-test-0.2.0"
stage = OUT / NAME
if stage.resolve().parent != OUT.resolve() or stage.name != NAME:
    raise SystemExit("Unsafe package staging directory")
if stage.exists():
    shutil.rmtree(stage)
(stage / "images").mkdir(parents=True)
(stage / "tools").mkdir()
shutil.copy2(OUT / "eqlink-server-arm64.tar", stage / "images/eqlink-server-arm64.tar")
shutil.copy2(REPO / "artifacts/server/eqlink-server-arm64/xray", stage / "tools/xray")
for name in ("common.py", "deploy.py", "upgrade.py", "devices.py", "import-image.sh", "README.zh-CN.md"):
    shutil.copy2(BASE / name, stage / name)
shutil.copytree(BASE / "manifests", stage / "manifests")
for name in ("PROVENANCE.json", "debian-packages-arm64.txt", "VALIDATION.json"):
    shutil.copy2(OUT / name, stage / name)
shutil.copy2(REPO / "packaging/THIRD_PARTY.md", stage / "THIRD_PARTY.md")
lines = []
for path in sorted(stage.rglob("*")):
    if path.is_file():
        with path.open("rb") as stream:
            checksum = hashlib.file_digest(stream, "sha256").hexdigest()
        lines.append(checksum + "  " + path.relative_to(stage).as_posix())
(stage / "SHA256SUMS").write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
archive_path = OUT / (NAME + ".tar.gz")
with tarfile.open(archive_path, "w:gz", compresslevel=6, format=tarfile.PAX_FORMAT) as archive:
    for path in sorted(stage.rglob("*")):
        info = archive.gettarinfo(str(path), arcname=NAME + "/" + path.relative_to(stage).as_posix())
        info.uid = info.gid = 0
        info.uname = info.gname = "root"
        info.mode = 0o755 if path.is_dir() or path.suffix == ".sh" or path.name == "xray" else 0o644
        if path.is_file():
            with path.open("rb") as stream:
                archive.addfile(info, stream)
        else:
            archive.addfile(info)
with archive_path.open("rb") as stream:
    checksum = hashlib.file_digest(stream, "sha256").hexdigest()
archive_path.with_suffix(archive_path.suffix + ".sha256").write_text(checksum + "  " + archive_path.name + "\n")
print(str(archive_path))
print("SHA256 " + checksum)
