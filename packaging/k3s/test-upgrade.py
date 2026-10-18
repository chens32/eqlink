#!/usr/bin/env python3
"""Offline regression for preservation and fail-closed preflight; never connects to a cluster."""
import copy
import json
import pathlib
from common import IMAGE
from upgrade import check_tun, upgraded_template

deployment = json.loads((pathlib.Path(__file__).parent / "manifests/deployment.json").read_text())
server = deployment["spec"]["template"]["spec"]["containers"][0]
server["command"] = ["/extension/start.sh"]
server["args"] = ["--gateway"]
server["readinessProbe"] = {"exec": {"command": ["/extension/ready.sh"]}}
server.setdefault("volumeMounts", []).append({"name": "gateway", "mountPath": "/extension", "readOnly": True})
deployment["spec"]["template"]["spec"]["volumes"].append({"name": "gateway", "configMap": {"name": "private-gateway"}})
before = copy.deepcopy(deployment)
result = upgraded_template(deployment, IMAGE, "armhpc.home.arpa/tun")
updated = result["spec"]["containers"][0]
assert deployment == before
for key in ("command", "args", "startupProbe", "readinessProbe", "livenessProbe", "volumeMounts"):
    assert updated.get(key) == server.get(key), key
assert result["spec"]["volumes"] == before["spec"]["template"]["spec"]["volumes"]
assert updated["securityContext"]["privileged"] is False

class KubeFixture:
    def __init__(self, pods): self.pods = pods
    def run(self, *args): return json.dumps({"items": self.pods}).encode()

node = {"metadata": {"name": "arm-test"}, "status": {"allocatable": {"armhpc.home.arpa/tun": "1"}}}
check_tun(KubeFixture([]), node, "armhpc.home.arpa/tun", "eqlink-test")
for candidate in ({"metadata": node["metadata"], "status": {"allocatable": {}}},):
    try: check_tun(KubeFixture([]), candidate, "armhpc.home.arpa/tun", "eqlink-test")
    except ValueError: pass
    else: raise AssertionError("Missing device plugin accepted")
occupied = {"metadata": {"namespace": "other"}, "status": {"phase": "Running"}, "spec": {"containers": [{"resources": {"requests": {"armhpc.home.arpa/tun": "1"}}}]}}
try: check_tun(KubeFixture([occupied]), node, "armhpc.home.arpa/tun", "eqlink-test")
except ValueError: pass
else: raise AssertionError("Occupied resource accepted")
occupied["spec"]["initContainers"] = occupied["spec"].pop("containers")
try: check_tun(KubeFixture([occupied]), node, "armhpc.home.arpa/tun", "eqlink-test")
except ValueError: pass
else: raise AssertionError("Init container resource reservation ignored")
print("Upgrade preservation, missing TUN and occupied TUN: passed")
