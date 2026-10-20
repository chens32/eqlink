#!/usr/bin/env python3
"""Exercise release pruning preconditions using local fixtures; no GitHub writes."""
import pathlib
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "packaging"))
from release_assets import asset_names, asset_plan, inspect_uploads, stage_uploads


def rejected(action):
    try:
        action()
    except ValueError:
        return
    raise AssertionError("Unsafe release input was accepted")


version = "1.0.0"
test_root = ROOT / "artifacts/release-asset-tests"
test_root.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix="eqlink-release-assets-", dir=test_root) as temporary:
    output = pathlib.Path(temporary)
    for index, name in enumerate(asset_names(version)):
        (output / name).write_bytes(b"release fixture" + bytes([index]))
    local = stage_uploads(output, version)
    assert set(local) == set(asset_names(version))
    remote = [{"id": index + 1, "name": name, "state": "uploaded", "size": info["bytes"], "digest": "sha256:" + info["sha256"]}
              for index, (name, info) in enumerate(local.items())]
    assert [item["action"] for item in asset_plan(remote, local, version)] == ["keep"] * 3
    remote[0]["name"] = "EqLink-Setup-1.0.0.exe"
    remote.append({"id": 9, "name": "old-evidence.json", "state": "uploaded"})
    plan = asset_plan(remote, local, version)
    assert [item["action"] for item in plan] == ["rename", "keep", "keep", "delete"]
    assert plan[0]["id"] == 1 and plan[-1]["id"] == 9
    remote[1]["digest"] = "sha256:wrong"
    rejected(lambda: asset_plan(remote, local, version))
    rejected(lambda: asset_plan(remote, {}, version))
    rejected(lambda: asset_names("../1.0.0"))
    (output / "upload" / "unexpected.txt").write_text("must not be uploaded", encoding="utf-8")
    rejected(lambda: inspect_uploads(output / "upload", version))
    rejected(lambda: stage_uploads(output, version))
    (output / "upload" / "unexpected.txt").unlink()
    (output / "upload" / asset_names(version)[0]).write_bytes(b"")
    rejected(lambda: inspect_uploads(output / "upload", version))
print("Release asset set, legacy rename, hash mismatch and pruning preconditions: passed")
