#!/usr/bin/env python3
"""Validate rendered resources against official Kubernetes OpenAPI and port-isolation constraints."""
import argparse
import copy
import json
import pathlib
import types

import jsonschema
from deploy import resources, settings, validate

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--schema-dir", required=True, type=pathlib.Path)
args = parser.parse_args()
schemas = {}
for path in args.schema_dir.glob("*.json"):
    schemas.update(json.loads(path.read_text())["components"]["schemas"])
options = types.SimpleNamespace(node="arm-test-1", namespace="eqlink-test", public_host="vpn.example.net", tcp_port=31443, udp_port=31444,
    overlay="10.88.0.0/24", tls_server_name="vpn.example.net", reality_target="www.cloudflare.com:443", reality_server_name=None, storage_class="local-path")
validate(options)
configuration = settings(options, "test-private-key", "test-public-key", "0123456789abcdef")
items = resources(options, configuration, b"test certificate bytes", b"test private bytes")
def check_schema(documents):
    for item in documents:
        group = "apps" if item["kind"] == "Deployment" else "core"
        model = "io.k8s.api." + group + ".v1." + item["kind"]
        root = {"$ref": "#/components/schemas/" + model, "components": {"schemas": schemas}}
        jsonschema.Draft4Validator(root).validate(item)

check_schema(items)
for arch in ("amd64", "arm64"):
    explicit = copy.copy(options)
    explicit.arch = arch
    explicit.image = "docker.io/eqlink/server:1.0.0-" + arch
    validate(explicit)
    explicit_items = resources(explicit, configuration, b"test certificate bytes", b"test private bytes")
    check_schema(explicit_items)
    explicit_pod = next(i for i in explicit_items if i["kind"] == "Deployment")["spec"]["template"]["spec"]
    assert explicit_pod["nodeSelector"]["kubernetes.io/arch"] == arch
    assert explicit_pod["containers"][0]["image"] == explicit.image
    assert explicit_pod["containers"][0]["imagePullPolicy"] == "Never"
service = next(i for i in items if i["kind"] == "Service")
assert {(p["protocol"], p["nodePort"], p["targetPort"]) for p in service["spec"]["ports"]} == {("TCP", 31443, 31443), ("UDP", 31444, 31444)}
assert not {1194, 1195, 7505} & {p["port"] for p in service["spec"]["ports"]}
deployment = next(i for i in items if i["kind"] == "Deployment")
pod = deployment["spec"]["template"]["spec"]
assert deployment["spec"]["replicas"] == 1 and deployment["spec"]["strategy"]["type"] == "Recreate"
assert not pod.get("hostNetwork", False) and not pod.get("hostPID", False) and not pod["automountServiceAccountToken"]
assert pod["containers"][0]["imagePullPolicy"] == "Never"
assert pod["affinity"]["nodeAffinity"]["requiredDuringSchedulingIgnoredDuringExecution"]["nodeSelectorTerms"][0]["matchFields"][0]["values"] == [options.node]
assert configuration["tcpPort"] == 31443 and configuration["udpPort"] == 31444
security = pod["containers"][0]["securityContext"]
assert security["privileged"] is False and security["readOnlyRootFilesystem"] and not security["allowPrivilegeEscalation"]
assert security["capabilities"] == {"drop": ["ALL"], "add": ["NET_ADMIN"]}
assert security["seccompProfile"]["type"] == "RuntimeDefault"
assert pod["containers"][0]["resources"]["limits"]["armhpc.home.arpa/tun"] == "1"
assert not any("hostPath" in volume for volume in pod["volumes"])
assert all(i.get("type") != "LoadBalancer" for i in items)
print("Kubernetes official OpenAPI schema: 6 resources passed")
print("Custom high-port mapping and backend isolation: passed")
print("Explicit amd64/arm64 image and node architecture: passed")
