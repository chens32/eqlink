#!/usr/bin/env python3
"""Plan asset synchronization; --apply keeps exactly three binaries on an existing release."""
import argparse
from datetime import datetime, timezone
import json
import pathlib
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "packaging"))
from release_assets import asset_names, asset_plan, inspect_uploads


def gh(*args, document=False):
    result = subprocess.run(["gh", *args], capture_output=True, check=True, text=True, encoding="utf-8")
    return json.loads(result.stdout) if document else result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", default=ET.parse(ROOT / "Directory.Build.props").findtext("./PropertyGroup/Version"))
    parser.add_argument("--repo", default="chens32/eqlink")
    parser.add_argument("--directory", type=pathlib.Path)
    parser.add_argument("--report", type=pathlib.Path)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    asset_names(args.version)
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repo):
        raise ValueError("Invalid repository")
    directory = args.directory or ROOT / "artifacts/release" / args.version / "upload"
    local = inspect_uploads(directory, args.version)
    endpoint = f"repos/{args.repo}/releases/tags/v{args.version}"
    release = gh("api", endpoint, document=True)
    plan = asset_plan(release["assets"], local, args.version)
    report = {"checkedAt": datetime.now(timezone.utc).isoformat(), "releaseId": release["id"],
              "tag": release["tag_name"], "applied": False, "plan": plan, "assets": local}
    for item in plan:
        print(item["action"] + ": " + item["name"])
    if args.apply:
        for item in plan:
            if item["action"] == "rename":
                gh("api", "--method", "PATCH", f"repos/{args.repo}/releases/assets/{item['id']}",
                   "--raw-field", "name=" + item["name"])
            elif item["action"] == "upload":
                gh("release", "upload", "v" + args.version, str(directory / item["name"]), "--repo", args.repo)
        # Do not remove anything until all three retained binaries are uploaded and verified.
        verified = gh("api", endpoint, document=True)
        if verified["id"] != release["id"]:
            raise ValueError("Release identity changed during synchronization")
        verification = asset_plan(verified["assets"], local, args.version)
        if any(item["action"] != "keep" for item in verification if item["action"] != "delete"):
            raise ValueError("Required binaries are not ready; no extra assets were removed")
        planned_deletions = {item["id"] for item in plan if item["action"] == "delete"}
        actual_deletions = {item["id"] for item in verification if item["action"] == "delete"}
        if planned_deletions != actual_deletions:
            raise ValueError("Remote assets changed during synchronization; review a fresh plan")
        for item in verification:
            if item["action"] == "delete":
                gh("api", "--method", "DELETE", f"repos/{args.repo}/releases/assets/{item['id']}")
        final = gh("api", endpoint, document=True)
        final_plan = asset_plan(final["assets"], local, args.version)
        if len(final_plan) != 3 or any(item["action"] != "keep" for item in final_plan):
            raise ValueError("Release does not have exactly the expected three assets")
        if (final["draft"], final["prerelease"], final["target_commitish"]) != (release["draft"], release["prerelease"], release["target_commitish"]):
            raise ValueError("Release state changed during synchronization")
        report["applied"] = True
        report["verified"] = True
        print("Verified: exactly three release assets; hashes and release state preserved")
    else:
        print("Plan only; pass --apply to synchronize assets. Release publication is a separate operation.")
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.SubprocessError) as exc:
        raise SystemExit("Release synchronization failed: " + str(exc))
