#!/usr/bin/env python3
"""Offline verification of the maintenance Pod boundary; does not access Kubernetes."""
import copy
import importlib.util
import json
import pathlib
from common import IMAGE
from upgrade import upgraded_template

base = pathlib.Path(__file__).parent
spec = importlib.util.spec_from_file_location("migration", base / "migrate-v2.py")
migration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(migration)
deployment = json.loads((base / "manifests/deployment.json").read_text())
server = deployment["spec"]["template"]["spec"]["containers"][0]
server["command"] = ["/extension/start.sh"]
server["args"] = ["--gateway"]
server.setdefault("volumeMounts", []).append({"name": "gateway", "mountPath": "/extension", "readOnly": True})
deployment["spec"]["template"]["spec"]["volumes"].append({"name": "gateway", "configMap": {"name": "gateway"}})
before = copy.deepcopy(deployment)
helper = migration.helper_pod(deployment, "eqlink-test", "eqlink-maintenance-fixture", IMAGE)
assert deployment == before
assert helper["metadata"]["labels"]["app"] != "eqlink-server"
assert helper["spec"]["volumes"] == deployment["spec"]["template"]["spec"]["volumes"]
assert helper["spec"]["automountServiceAccountToken"] is False
assert helper["spec"]["containers"][0]["command"] == ["/bin/sh", "-ec"]
assert all(key not in helper["spec"]["containers"][0] for key in ("startupProbe", "readinessProbe", "livenessProbe", "ports"))
assert helper["spec"]["containers"][0]["volumeMounts"] == server["volumeMounts"]
target = upgraded_template(deployment, IMAGE, "armhpc.home.arpa/tun")
assert target["spec"]["containers"][0]["command"] == server["command"]
assert target["spec"]["containers"][0]["args"] == server["args"]
print("Migration maintenance isolation and preserved gateway/startup fields: passed (offline fixture)")
