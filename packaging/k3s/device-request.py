#!/usr/bin/env python3
"""Private kubectl-exec enrollment export; no network listener, passwords or device keys."""
import base64
import fcntl
import json
import os
import pathlib
import subprocess
import sys
import uuid

def main():
    request = json.loads(sys.stdin.buffer.read(16384))
    operation = uuid.UUID(request["operationId"]).hex
    name = request["name"]
    if not isinstance(name, str) or not 1 <= len(name) <= 64:
        raise ValueError("Invalid enrollment name")
    export = pathlib.Path("/etc/eqlink/exports")
    export.mkdir(mode=0o700, exist_ok=True)
    os.chmod(export, 0o700)
    with (export / ".lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        bundle = export / (operation + ".eqlink")
        if not bundle.exists():
            result = subprocess.run(["EqLink.Server", "enrollment-create", "--name", name, "--bundle", str(bundle)], capture_output=True, timeout=120)
            if result.returncode:
                raise RuntimeError("Enrollment export failed; inspect enrollment-list before retrying")
        data = json.loads(bundle.read_text())
        state = json.loads(pathlib.Path("/etc/eqlink/state.json").read_text())
        grant = next((g for g in state["grants"] if g["id"] == data["grantId"]), None)
        if data.get("schemaVersion") != 2 or not grant or not grant["active"] or grant["name"] != name:
            raise ValueError("Cached export is revoked or belongs to another enrollment name")
        print(json.dumps({"ok": True, "operationId": operation, "grantId": grant["id"], "message": "Universal enrollment export ready", "bundle": base64.b64encode(bundle.read_bytes()).decode()}))

if __name__ == "__main__":
    try:
        main()
    except Exception:
        print(json.dumps({"ok": False, "error": "Enrollment export unavailable; inspect persistent state and retry the same operation ID"}))
