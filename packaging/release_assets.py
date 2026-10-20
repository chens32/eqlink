"""The three manually uploaded release assets; evidence stays in local build output."""
import hashlib
import pathlib
import re
import shutil


def asset_names(version):
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Expected a three-part release version")
    return (
        f"eqlink-client-{version}.exe",
        f"eqlink-server-arm64-{version}.tar.gz",
        f"eqlink-server-amd64-{version}.tar.gz",
    )


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def inspect_uploads(directory, version):
    directory = pathlib.Path(directory)
    expected = set(asset_names(version))
    if {path.name for path in directory.iterdir()} != expected:
        raise ValueError("Upload directory must contain exactly the three release binaries")
    result = {}
    for name in asset_names(version):
        path = directory / name
        if path.is_symlink() or not path.is_file() or path.stat().st_size == 0:
            raise ValueError("Expected a nonempty regular release file: " + name)
        result[name] = {"bytes": path.stat().st_size, "sha256": digest(path)}
    return result


def stage_uploads(output, version):
    directory = pathlib.Path(output) / "upload"
    directory.mkdir(exist_ok=True)
    if {p.name for p in directory.iterdir()} - set(asset_names(version)):
        raise ValueError("Upload directory contains unrelated files; refusing to replace them")
    for name in asset_names(version):
        target = directory / name
        if target.is_symlink():
            raise ValueError("Upload destination must not be a symlink")
        shutil.copy2(pathlib.Path(output) / name, target)
    return inspect_uploads(directory, version)


def asset_plan(remote, local, version):
    """Never replace an existing published binary with different bytes."""
    if set(local) != set(asset_names(version)):
        raise ValueError("Unexpected local release asset set")
    by_name = {asset["name"]: asset for asset in remote}
    if len(by_name) != len(remote):
        raise ValueError("Duplicate remote release asset names")
    plan = []
    legacy_client = f"EqLink-Setup-{version}.exe"
    for name in asset_names(version):
        expected = local[name]
        current = by_name.get(name)
        matches = lambda item: (item.get("state") == "uploaded" and item.get("size") == expected["bytes"]
                                and item.get("digest") == "sha256:" + expected["sha256"])
        if current:
            if not matches(current):
                raise ValueError("Published binary differs; use a new version: " + name)
            plan.append({"action": "keep", "name": name, "id": current["id"]})
        elif name == asset_names(version)[0] and legacy_client in by_name and matches(by_name[legacy_client]):
            plan.append({"action": "rename", "name": name, "id": by_name[legacy_client]["id"], "oldName": legacy_client})
        else:
            plan.append({"action": "upload", "name": name})
    retained_ids = {item["id"] for item in plan if "id" in item}
    plan.extend({"action": "delete", "name": item["name"], "id": item["id"]}
                for item in remote if item["id"] not in retained_ids and item["name"] not in local)
    return plan
