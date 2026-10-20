#!/usr/bin/env python3
"""Legacy 0.3.x test-bundle assembler; public releases use package-release.py and upload/."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import pathlib
import runpy
import shutil
import subprocess
import tarfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ca-certificate", type=pathlib.Path, required=True)
    parser.add_argument("--ca-file-sha256", required=True)
    parser.add_argument("--acceptance", type=pathlib.Path, required=True)
    args = parser.parse_args()
    acceptance = json.loads(args.acceptance.read_text(encoding="utf-8-sig"))
    version = ET.parse(ROOT / "src/EqLink.ClientService/EqLink.ClientService.csproj").findtext(".//Version")
    if version not in ("0.3.1", "0.3.2", "0.3.3"):
        raise ValueError("Review release packaging before changing the target version")
    public_ca = args.ca_certificate.resolve()
    if digest(public_ca).lower() != args.ca_file_sha256.lower():
        raise ValueError("Public test CA file hash differs from the verified input")
    output = ROOT / "artifacts/release" / version
    if output.exists():
        raise ValueError("Release directory already exists; retain it and choose a reviewed new release")
    output.mkdir(parents=True)
    for source, name in (("EqLink-Setup.exe", f"EqLink-Setup-{version}.exe"), ("EqLink.msi", f"EqLink-{version}.msi")):
        shutil.copy2(ROOT / "artifacts/package" / source, output / name)
    shutil.copy2(ROOT / ("docs/release-" + version + ".md"), output / "README.zh-CN.md")
    if version == "0.3.3":
        shutil.copy2(ROOT / "docs/hosts-overrides.md", output / "HOSTS.zh-CN.md")
    shutil.copy2(args.acceptance, output / "ACCEPTANCE.json")
    tools_name = "EqLink-BundleTools-" + version
    tools_archive = output / (tools_name + ".zip")
    with zipfile.ZipFile(tools_archive, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        checksums = []
        for name in ("common.py", "devices.py", "bundle_trust.py"):
            source = ROOT / "packaging/k3s" / name
            archive.write(source, tools_name + "/" + name)
            checksums.append(digest(source) + "  " + name)
        for source, name in ((public_ca, "eqlink-hy2-root-ca.cer"), (ROOT / ("docs/release-" + version + ".md"), "README.zh-CN.md")):
            archive.write(source, tools_name + "/" + name)
            checksums.append(digest(source) + "  " + name)
        archive.writestr(tools_name + "/SHA256SUMS", "\n".join(checksums) + "\n")
    source_paths = list(runpy.run_path(str(ROOT / "packaging/package-release.py"))["sources"]())
    source_hashes = {p.relative_to(ROOT).as_posix(): digest(p) for p in source_paths}
    with tarfile.open(output / ("eqlink-source-" + version + ".tar.gz"), "w:gz") as archive:
        for path in source_paths:
            item = archive.gettarinfo(str(path), arcname="eqlink-" + version + "/" + path.relative_to(ROOT).as_posix())
            item.uid = item.gid = 0; item.uname = item.gname = ""
            item.mode = 0o755 if path.suffix == ".sh" else 0o644
            with path.open("rb") as stream:
                archive.addfile(item, stream)
    validation = args.acceptance.resolve().parent
    evidence_files = acceptance.get("publicEvidenceFiles")
    if evidence_files is not None:
        paths = []
        for relative in evidence_files:
            path = (validation / relative).resolve()
            if not path.is_relative_to(validation) or "private-rollback" in path.parts or path.suffix not in (".json", ".log", ".md"):
                raise ValueError("Invalid public validation evidence path")
            paths.append(path)
    else:
        paths = [path for path in sorted(validation.iterdir()) if path.is_file() and path.suffix in (".json", ".log")]
    with zipfile.ZipFile(output / ("eqlink-validation-" + version + ".zip"), "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(paths):
            archive.write(path, path.relative_to(validation).as_posix())
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True))
    manifest = {
        "schemaVersion": 1, "version": version, "channel": "test", "scope": "Windows client and management tools",
        "createdUtc": datetime.now(timezone.utc).isoformat(), "serverCompatibility": ["0.3.0/v2"],
        "source": {"gitHead": head, "workingTreeDirty": dirty, "capture": "workspace snapshot at packaging", "sha256": source_hashes},
        "artifacts": {p.name: {"bytes": p.stat().st_size, "sha256": digest(p)} for p in sorted(output.iterdir())},
        "includedPublicTestCa": {"fileSha256": digest(public_ca), "scope": "Management tool input only; not installed or hardcoded into the generic client"},
        "deploymentExecuted": False, "clientInstallationExecuted": acceptance.get("clientInstallationExecuted", False), "fullOriginalAcceptanceComplete": False,
    }
    (output / "release-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    checksums = [digest(p) + "  " + p.name for p in sorted(output.iterdir())]
    (output / "SHA256SUMS").write_text("\n".join(checksums) + "\n", encoding="ascii")
    print(str(output))


if __name__ == "__main__":
    main()
