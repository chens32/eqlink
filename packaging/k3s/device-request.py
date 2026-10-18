#!/usr/bin/env python3
"""Runs only inside the container through kubectl exec; no network listener."""
import base64
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import uuid


def main():
    request = json.loads(sys.stdin.buffer.read(16384))
    operation = uuid.UUID(request["operationId"]).hex
    name, passphrase = request["name"], request["passphrase"]
    if not isinstance(passphrase, str) or len(passphrase) < 12:
        raise ValueError("Passphrase must contain at least 12 characters")
    export = pathlib.Path("/etc/eqlink/exports")
    export.mkdir(mode=0o700, exist_ok=True)
    bundle = export / (operation + ".eqlink")
    state = json.loads(pathlib.Path("/etc/eqlink/state.json").read_text())
    existing = next((d for d in state["devices"] if d["id"] == operation), None)
    if existing:
        if existing["name"] != name or not existing["active"] or not bundle.is_file():
            raise ValueError("Operation already exists but is revoked, mismatched, or missing its bundle; inspect list before creating a new operation")
        message = "Retrieved existing device " + operation
    else:
        with tempfile.TemporaryDirectory(prefix="issue-", dir="/run/eqlink") as work:
            password_file = pathlib.Path(work) / "passphrase"
            descriptor = os.open(password_file, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            with os.fdopen(descriptor, "w") as stream:
                stream.write(passphrase)
            result = subprocess.run(["EqLink.Server", "add", "--id", operation, "--name", name, "--bundle", str(bundle), "--passphrase-file", str(password_file)], capture_output=True, timeout=120)
            if result.returncode:
                raise RuntimeError(result.stderr.decode(errors="replace").strip() + "; keep the same operation ID when retrying")
            message = result.stdout.decode().strip()
    print(json.dumps({"ok": True, "deviceId": operation, "message": message, "bundle": base64.b64encode(bundle.read_bytes()).decode()}))


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(json.dumps({"ok": False, "error": str(exc)}))
        # Transport succeeded; devices.py reports the explicit application error.
        sys.exit(0)
