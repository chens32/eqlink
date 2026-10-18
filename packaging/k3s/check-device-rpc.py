#!/usr/bin/env python3
"""Exercise the actual ARM64 kubectl-exec helper inside the isolated smoke mounts."""
import base64
import json
import os
import pathlib
import signal
import subprocess
import sys
import time
import uuid

rootfs = pathlib.Path(sys.argv[1])
command = ["chroot", str(rootfs), "env", "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", "EQLINK_RUNTIME=container", "DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net"]
log_path = rootfs / "tmp/eqlink-smoke/rpc-supervisor.log"
with log_path.open("wb") as log:
    server = subprocess.Popen(command + ["EqLink.Server", "serve", "--settings", "/run/eqlink-bootstrap/server-settings.json"], stdout=log, stderr=log)
    try:
        for _ in range(60):
            if server.poll() is not None:
                raise RuntimeError("RPC smoke supervisor exited; inspect its private test log")
            result = subprocess.run(command + ["EqLink.Server", "probe", "--kind", "ready"], capture_output=True, timeout=15)
            if not result.returncode:
                break
            time.sleep(0.5)
        else:
            raise RuntimeError("RPC smoke supervisor did not become ready")
        operation = uuid.uuid4().hex
        request = json.dumps({"operationId": operation, "name": "rpc-device", "passphrase": "rpc-smoke-test-only-password"}).encode()
        responses = []
        for _ in range(2):
            result = subprocess.run(command + ["python3", "/usr/local/lib/eqlink/device-request.py"], input=request, capture_output=True, timeout=120)
            if result.returncode:
                raise RuntimeError("ARM64 device helper failed: " + result.stderr.decode(errors="replace"))
            response = json.loads(result.stdout)
            if not response.get("ok"):
                raise RuntimeError(response.get("error", "RPC error"))
            responses.append(response)
        assert responses[0]["deviceId"] == responses[1]["deviceId"] == operation
        assert responses[0]["bundle"] == responses[1]["bundle"]
        encrypted = json.loads(base64.b64decode(responses[0]["bundle"]))
        assert encrypted["algorithm"] == "PBKDF2-SHA256/AES-256-GCM"
        state = json.loads((rootfs / "etc/eqlink/state.json").read_text())
        assert sum(device["id"] == operation for device in state["devices"]) == 1
        cached = rootfs / ("etc/eqlink/exports/" + operation + ".eqlink")
        assert cached.stat().st_mode & 0o077 == 0
        print("ARM64 device RPC and operation-ID recovery: passed", flush=True)
    finally:
        if server.poll() is None:
            server.send_signal(signal.SIGTERM)
            try:
                server.wait(timeout=20)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait()
