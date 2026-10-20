#!/usr/bin/env python3
"""Prepare and apply EqLink to one explicitly selected amd64/arm64 Kubernetes node."""
import argparse
import base64
import ipaddress
import json
import os
import pathlib
import re
import secrets
import subprocess
import sys

from common import IMAGE, OWNER, Kube, private_write

BASE = pathlib.Path(__file__).resolve().parent


def validate(args):
    if getattr(args, "arch", "arm64") not in ("amd64", "arm64"):
        raise ValueError("Architecture must be amd64 or arm64")
    image = getattr(args, "image", None)
    if image is not None and (not image or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._/:@-]*", image)):
        raise ValueError("Invalid container image reference")
    for value in (args.namespace, args.node):
        if not re.fullmatch(r"[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?", value) or len(value) > 253:
            raise ValueError("Invalid node or namespace name")
    for port in (args.tcp_port, args.udp_port):
        if not 30000 <= port <= 32767:
            raise ValueError("This test package uses NodePorts in 30000..32767")
    host = args.public_host
    try:
        ip = ipaddress.ip_address(host)
        if ip.version != 4:
            raise ValueError("Use an IPv4 public entry for this test")
    except ValueError:
        if not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?", host):
            raise ValueError("Invalid public IPv4 address or DNS name")
    network = ipaddress.ip_network(args.overlay, strict=True)
    if network.version != 4 or network.prefixlen != 24 or not any(network.subnet_of(ipaddress.ip_network(n)) for n in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")):
        raise ValueError("Overlay must be an RFC1918 IPv4 /24")
    if not re.fullmatch(r"[A-Za-z0-9.-]+:[0-9]{1,5}", args.reality_target):
        raise ValueError("REALITY target must be host:port")


def keys(executable):
    result = subprocess.run([str(executable), "x25519"], capture_output=True, check=True)
    fields = dict(line.split(":", 1) for line in result.stdout.decode().splitlines() if ":" in line)
    return fields["PrivateKey"].strip(), fields["Password (PublicKey)"].strip()


def settings(args, private_key, public_key, short_id):
    return {
        "publicHost": args.public_host, "tcpPort": args.tcp_port, "udpPort": args.udp_port,
        "openVpnPort": 1194, "healthPort": 1195, "managementPort": 7505,
        "overlayCidr": args.overlay, "enableUdp": True, "enableTcp": True,
        "hysteriaCertificate": "/run/eqlink-tls/tls.crt", "hysteriaPrivateKey": "/run/eqlink-tls/tls.key",
        "hysteriaServerName": args.tls_server_name or args.public_host,
        "realityTarget": args.reality_target, "realityServerName": args.reality_server_name or args.reality_target.rsplit(":", 1)[0],
        "realityPrivateKey": private_key, "realityPublicKey": public_key, "realityShortId": short_id,
        "xhttpPath": "/eqlink"
    }


def resources(args, configuration, certificate, private_key):
    meta = lambda name: {"name": name, "namespace": args.namespace, "labels": {"app.kubernetes.io/managed-by": OWNER}}
    deployment = json.loads((BASE / "manifests/deployment.json").read_text())
    deployment["metadata"] = meta("eqlink-server")
    pod = deployment["spec"]["template"]["spec"]
    pod["affinity"]["nodeAffinity"]["requiredDuringSchedulingIgnoredDuringExecution"]["nodeSelectorTerms"][0]["matchFields"][0]["values"] = [args.node]
    arch = getattr(args, "arch", "arm64")
    pod["nodeSelector"]["kubernetes.io/arch"] = arch
    pod["containers"][0]["image"] = getattr(args, "image", None) or IMAGE.rsplit("-", 1)[0] + "-" + arch
    resource = getattr(args, "tun_resource", "armhpc.home.arpa/tun")
    for section in ("requests", "limits"):
        pod["containers"][0]["resources"][section].pop("armhpc.home.arpa/tun", None)
        pod["containers"][0]["resources"][section][resource] = "1"
    pod["containers"][0]["ports"][0]["containerPort"] = args.tcp_port
    pod["containers"][0]["ports"][1]["containerPort"] = args.udp_port
    service = json.loads((BASE / "manifests/service.json").read_text())
    service["metadata"] = meta("eqlink-outer")
    for item, port in zip(service["spec"]["ports"], (args.tcp_port, args.udp_port)):
        item.update(port=port, targetPort=port, nodePort=port)
    return [
        {"apiVersion": "v1", "kind": "Namespace", "metadata": {"name": args.namespace, "labels": {"app.kubernetes.io/managed-by": OWNER, "pod-security.kubernetes.io/enforce": "privileged"}}},
        {"apiVersion": "v1", "kind": "PersistentVolumeClaim", "metadata": meta("eqlink-data"), "spec": {"accessModes": ["ReadWriteOnce"], "storageClassName": args.storage_class, "resources": {"requests": {"storage": "1Gi"}}}},
        {"apiVersion": "v1", "kind": "Secret", "metadata": meta("eqlink-bootstrap"), "type": "Opaque", "data": {"server-settings.json": base64.b64encode(json.dumps(configuration).encode()).decode()}},
        {"apiVersion": "v1", "kind": "Secret", "metadata": meta("eqlink-tls"), "type": "kubernetes.io/tls", "data": {"tls.crt": base64.b64encode(certificate).decode(), "tls.key": base64.b64encode(private_key).decode()}},
        deployment, service
    ]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--node", required=True)
    parser.add_argument("--arch", choices=("amd64", "arm64"), default="arm64")
    parser.add_argument("--image", help="Exact image reference already imported into the selected node")
    parser.add_argument("--public-host", required=True)
    parser.add_argument("--tls-cert", required=True, type=pathlib.Path)
    parser.add_argument("--tls-key", required=True, type=pathlib.Path)
    parser.add_argument("--tls-server-name")
    parser.add_argument("--namespace", default="eqlink-test")
    parser.add_argument("--storage-class", default="local-path")
    parser.add_argument("--tun-resource", default="armhpc.home.arpa/tun")
    parser.add_argument("--tcp-port", type=int, default=30443)
    parser.add_argument("--udp-port", type=int, default=30444)
    parser.add_argument("--overlay", default="10.88.0.0/24")
    parser.add_argument("--reality-target", default="www.cloudflare.com:443")
    parser.add_argument("--reality-server-name")
    parser.add_argument("--xray", type=pathlib.Path, default=BASE / "tools/xray")
    parser.add_argument("--kubectl")
    parser.add_argument("--kubeconfig")
    parser.add_argument("--render-only", action="store_true", help="Only write private local manifests; do not access Kubernetes")
    parser.add_argument("--output", type=pathlib.Path, default=BASE / "private")
    args = parser.parse_args()
    if args.image is None:
        args.image = IMAGE.rsplit("-", 1)[0] + "-" + args.arch
    validate(args)
    if not re.fullmatch(r"[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?/[A-Za-z0-9](?:[A-Za-z0-9_.-]*[A-Za-z0-9])?", args.tun_resource):
        raise ValueError("Invalid TUN extended resource name")
    certificate, tls_key = args.tls_cert.read_bytes(), args.tls_key.read_bytes()
    if b"BEGIN CERTIFICATE" not in certificate or b"PRIVATE KEY" not in tls_key:
        raise ValueError("TLS PEM certificate/private key are required")
    kube = Kube(args.kubectl, args.kubeconfig)
    old = None
    if not args.render_only:
        node = kube.get("node", args.node)
        if kube.get("deployment", "eqlink-server", args.namespace, optional=True):
            raise ValueError("Existing EqLink deployment: use upgrade.py; install never replaces an existing deployment")
        if node["status"]["nodeInfo"]["architecture"] != args.arch or not any(c["type"] == "Ready" and c["status"] == "True" for c in node["status"].get("conditions", [])):
            raise ValueError("Selected node must be Ready and " + args.arch)
        from upgrade import check_tun
        check_tun(kube, node, args.tun_resource, args.namespace)
        overlay = ipaddress.ip_network(args.overlay)
        for cluster_node in kube.get("nodes")["items"]:
            spec = cluster_node.get("spec", {})
            cidrs = spec.get("podCIDRs") or ([spec["podCIDR"]] if spec.get("podCIDR") else [])
            for cidr in cidrs:
                network = ipaddress.ip_network(cidr)
                if network.version == 4 and overlay.overlaps(network):
                    raise ValueError("Overlay overlaps allocated Pod CIDR " + cidr)
            for address in cluster_node["status"].get("addresses", []):
                if address["type"] == "InternalIP" and ipaddress.ip_address(address["address"]) in overlay:
                    raise ValueError("Overlay contains a node's internal address")
        kube.get("storageclass", args.storage_class)
        for resource, name in (("namespace", args.namespace), ("pvc", "eqlink-data"), ("deployment", "eqlink-server"), ("service", "eqlink-outer"), ("secret", "eqlink-bootstrap"), ("secret", "eqlink-tls")):
            item = kube.get(resource, name, None if resource == "namespace" else args.namespace, optional=True)
            if item and item["metadata"].get("labels", {}).get("app.kubernetes.io/managed-by") != OWNER:
                raise ValueError("Existing resource is not owned by this package: " + resource + "/" + name)
            if resource == "secret" and name == "eqlink-bootstrap" and item:
                old = json.loads(base64.b64decode(item["data"]["server-settings.json"]))
            if resource == "secret" and name == "eqlink-tls" and item:
                if base64.b64decode(item["data"]["tls.crt"]) != certificate or base64.b64decode(item["data"]["tls.key"]) != tls_key:
                    raise ValueError("TLS material differs from the deployed Secret. Certificate rotation requires a deliberate rollout and new client bundles.")
            if resource == "deployment" and item:
                old_node = item["spec"]["template"]["spec"]["affinity"]["nodeAffinity"]["requiredDuringSchedulingIgnoredDuringExecution"]["nodeSelectorTerms"][0]["matchFields"][0]["values"]
                if old_node != [args.node]:
                    raise ValueError("Existing deployment is pinned to another node; this helper does not migrate persistent state")
        for svc in json.loads(kube.run("get", "services", "-A", "-o", "json"))["items"]:
            for cluster_ip in svc["spec"].get("clusterIPs", []):
                if cluster_ip != "None" and ipaddress.ip_address(cluster_ip) in overlay:
                    raise ValueError("Overlay contains an existing Kubernetes Service address")
            if svc["metadata"].get("namespace") == args.namespace and svc["metadata"]["name"] == "eqlink-outer":
                continue
            if any((p.get("protocol", "TCP"), p.get("nodePort")) in (("TCP", args.tcp_port), ("UDP", args.udp_port)) for p in svc["spec"].get("ports", [])):
                raise ValueError("NodePort already allocated to " + svc["metadata"]["name"])
    if old:
        configuration = settings(args, old["realityPrivateKey"], old["realityPublicKey"], old["realityShortId"])
        if old != configuration:
            raise ValueError("Requested settings differ from persistent bootstrap; changing ports/subnet/keys requires a deliberate migration")
    else:
        private_key, public_key = keys(args.xray)
        configuration = settings(args, private_key, public_key, secrets.token_hex(8))
    items = resources(args, configuration, certificate, tls_key)
    args.output.mkdir(mode=0o700, parents=True, exist_ok=True)
    if os.name != "nt":
        os.chmod(args.output, 0o700)
    output = args.output / ("deployment-" + secrets.token_hex(6) + ".json")
    private_write(output, json.dumps({"apiVersion": "v1", "kind": "List", "items": items}, indent=2).encode())
    print("Private deployment manifest: " + str(output))
    if args.render_only:
        return
    # Validate against the real API before writing namespaced resources.
    kube.apply(items[0], dry_run=True)
    kube.apply(items[0])
    for item in items[1:]:
        kube.apply(item, dry_run=True)
    for item in items[1:]:
        kube.apply(item)
    print("Applied. Inspect: kubectl -n " + args.namespace + " get pods -o wide")
    print("NAT must point TCP/" + str(args.tcp_port) + " and UDP/" + str(args.udp_port) + " to the selected node's LAN IP.")
    print("Image must already be imported on that node: " + args.image)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, OSError, subprocess.SubprocessError, KeyError) as exc:
        print("Deployment failed: " + str(exc), file=sys.stderr)
        sys.exit(1)
