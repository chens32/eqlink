#!/usr/bin/env python3
"""Plan a coordinated, offline identity migration. --apply requires a verified independent management path.
Preserves the original startup wrapper, probes, volumes, Secrets and gateway extension.
"""
import argparse
import copy
import hashlib
import json
import os
import pathlib
import re
import secrets
import sys
import time
from common import IMAGE, Kube, private_write
from upgrade import owned, upgraded_template, check_tun, pod_for, wait_ready


def helper_pod(deployment, namespace, name, image):
    spec = copy.deepcopy(deployment["spec"]["template"]["spec"])
    server = next(c for c in spec["containers"] if c["name"] == "server")
    server.update(image=image, imagePullPolicy="Never", command=["/bin/sh", "-ec"], args=["umask 077; exec sleep 3600"])
    for key in ("startupProbe", "readinessProbe", "livenessProbe", "lifecycle", "ports"):
        server.pop(key, None)
    spec["containers"] = [server]
    spec.pop("initContainers", None)
    spec["restartPolicy"] = "Never"
    spec["automountServiceAccountToken"] = False
    return {"apiVersion": "v1", "kind": "Pod", "metadata": {"name": name, "namespace": namespace, "labels": {"app": "eqlink-maintenance"}}, "spec": spec}


def patch(kube, namespace, current, **fields):
    operations = [{"op": "test", "path": "/metadata/resourceVersion", "value": current["metadata"]["resourceVersion"]}]
    operations += [{"op": "replace", "path": "/spec/" + key, "value": value} for key, value in fields.items()]
    kube.run("patch", "deployment", "eqlink-server", "-n", namespace, "--type=json", "--patch-file", "-", input_data=json.dumps(operations).encode())


def stopped(kube, namespace):
    until = time.monotonic() + 120
    while time.monotonic() < until:
        pods = json.loads(kube.run("get", "pods", "-n", namespace, "-l", "app=eqlink-server", "-o", "json"))["items"]
        if not pods:
            return
        time.sleep(2)
    raise RuntimeError("Old EqLink Pod has not stopped; migration withheld")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--namespace", default="eqlink-test")
    parser.add_argument("--node", required=True)
    parser.add_argument("--tun-resource", default="armhpc.home.arpa/tun")
    parser.add_argument("--expected-image-id")
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--independent-management-verified", action="store_true")
    parser.add_argument("--rollback-from", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, default=pathlib.Path(__file__).parent / "private")
    parser.add_argument("--kubectl")
    parser.add_argument("--kubeconfig")
    args = parser.parse_args()
    if os.name != "posix":
        raise ValueError("Use a Linux management host with private POSIX backups")
    if not all(re.fullmatch(r"[a-z0-9][a-z0-9.-]{0,252}", value) for value in (args.namespace, args.node)):
        raise ValueError("Invalid namespace/node")
    if args.apply and (not args.independent_management_verified or not re.fullmatch(r"sha256:[0-9a-f]{64}", args.expected_image_id or "")):
        raise ValueError("--apply requires a verified independent management path and imported --expected-image-id")
    kube = Kube(args.kubectl, args.kubeconfig)
    deployment = kube.get("deployment", "eqlink-server", args.namespace)
    if args.rollback_from and deployment["spec"].get("replicas") == 0:
        validation = copy.deepcopy(deployment); validation["spec"]["replicas"] = 1; owned(validation)
        pod = json.loads((args.rollback_from / "pod.json").read_text())
    else:
        owned(deployment)
        pod = pod_for(kube, args.namespace)
    if pod["spec"]["nodeName"] != args.node:
        raise ValueError("Selected Pod is not on the requested fixed node")
    terms = deployment["spec"]["template"]["spec"].get("affinity", {}).get("nodeAffinity", {}).get("requiredDuringSchedulingIgnoredDuringExecution", {}).get("nodeSelectorTerms", [])
    if len(terms) != 1 or not any(f.get("key") == "metadata.name" and f.get("operator") == "In" and f.get("values") == [args.node] for f in terms[0].get("matchFields", [])):
        raise ValueError("Deployment must be explicitly pinned to the requested node")
    node = kube.get("node", args.node)
    if node["status"]["nodeInfo"]["architecture"] != "arm64":
        raise ValueError("Expected ARM64 node")
    check_tun(kube, node, args.tun_resource, args.namespace)
    target = upgraded_template(deployment, IMAGE, args.tun_resource)
    folder = args.output / ("migration-" + time.strftime("%Y%m%d-%H%M%S") + "-" + secrets.token_hex(3))
    folder.mkdir(parents=True, mode=0o700)
    os.chmod(folder, 0o700)
    private_write(folder / "deployment.json", json.dumps(deployment).encode())
    private_write(folder / "pod.json", json.dumps(pod).encode())
    private_write(folder / "planned-template.json", json.dumps(target).encode())
    name = "eqlink-maintenance-" + secrets.token_hex(5)
    helper = helper_pod(deployment, args.namespace, name, IMAGE)
    kube.apply(helper, dry_run=True)
    private_write(folder / "maintenance-pod.json", json.dumps(helper).encode())
    summary = {"version": "0.3.0", "applied": False, "stateMigration": "v1-to-v2" if not args.rollback_from else "restore-private-backup",
               "allClientsMustDisconnect": True, "caRealityNetworkPreserved": True,
               "startupWrapperProbesMountsGatewayPreserved": True, "namespace": args.namespace, "node": args.node}
    private_write(folder / "plan.json", json.dumps(summary, indent=2).encode())
    print(json.dumps(summary, indent=2))
    print("Private plan and rollback directory: " + str(folder))
    if not args.apply:
        return
    # Secret/ConfigMap manifests are retained locally and never printed.
    for volume in pod["spec"].get("volumes", []):
        for kind, key, name_key in (("secret", "secret", "secretName"), ("configmap", "configMap", "name")):
            if key in volume:
                object_name = volume[key][name_key]
                path = folder / (kind + "-" + object_name + ".json")
                if not path.exists():
                    private_write(path, json.dumps(kube.get(kind, object_name, args.namespace)).encode())
    for kind, object_name in (("service", "eqlink-outer"), ("pvc", "eqlink-data")):
        private_write(folder / (kind + ".json"), json.dumps(kube.get(kind, object_name, args.namespace)).encode())
    if args.rollback_from:
        saved = json.loads((args.rollback_from / "deployment.json").read_text())
        if saved["metadata"]["namespace"] != args.namespace or saved["spec"]["template"]["spec"].get("affinity") != deployment["spec"]["template"]["spec"].get("affinity"):
            raise ValueError("Rollback backup belongs to another namespace/node policy")
        target = saved["spec"]["template"]
        data = (args.rollback_from / "state.tar.gz").read_bytes()
        if hashlib.sha256(data).hexdigest() != (args.rollback_from / "state.sha256").read_text().strip():
            raise ValueError("Rollback archive hash mismatch")
    patch(kube, args.namespace, deployment, replicas=0)
    stopped(kube, args.namespace)
    kube.apply(helper)
    prefix = ["exec", "-n", args.namespace, name, "-c", "server", "--"]
    try:
        kube.run("wait", "-n", args.namespace, "--for=condition=Ready", "pod/" + name, "--timeout=180s", timeout=190)
        actual = kube.get("pod", name, args.namespace)
        if not actual["status"]["containerStatuses"][0]["imageID"].endswith(args.expected_image_id):
            raise ValueError("Maintenance Pod image digest differs from the verified import")
        # Offline backup after the last data process stopped gives one consistent CA/database/CCD set.
        backup = kube.run(*prefix, "tar", "--exclude=./.state.lock", "--exclude=./.instance.lock", "-C", "/etc/eqlink", "-czf", "-", ".", timeout=120)
        private_write(folder / "state.tar.gz", backup)
        private_write(folder / "state.sha256", hashlib.sha256(backup).hexdigest().encode())
        if args.rollback_from:
            # Confine recursive removal to the explicitly named mounted state root; the independent backup is already local.
            restore = "import pathlib,shutil,sys,tarfile,io; root=pathlib.Path('/etc/eqlink').resolve(); assert str(root)=='/etc/eqlink'; [shutil.rmtree(p) if p.is_dir() and not p.is_symlink() else p.unlink() for p in root.iterdir()]; tarfile.open(fileobj=io.BytesIO(sys.stdin.buffer.read()),mode='r:gz').extractall(root,filter='data')"
            kube.run("exec", "-i", "-n", args.namespace, name, "-c", "server", "--", "python3", "-c", restore, input_data=data, timeout=120)
        else:
            kube.run(*prefix, "EqLink.Server", "migrate-v2", "--backup", "/run/eqlink/pre-v2.tar.gz", timeout=180)
        private_write(folder / "state-step-complete.json", b'{"completed":true}')
    finally:
        kube.run("delete", "pod", name, "-n", args.namespace, "--wait=true", "--timeout=90s", timeout=100)
    current = kube.get("deployment", "eqlink-server", args.namespace)
    if current["spec"].get("replicas") != 0 or current["spec"]["template"] != deployment["spec"]["template"]:
        raise RuntimeError("Deployment changed externally; state backup retained, rollout withheld")
    patch(kube, args.namespace, current, template=target, replicas=1)
    expected = args.expected_image_id
    if args.rollback_from:
        expected = next(s["imageID"] for s in pod["status"]["containerStatuses"] if s["name"] == "server")
        # The saved deployment's image can differ from the currently migrated image. Require operator verification on rollback.
        print("Rollback state/template restored; validate saved image ID, probes and virtual traffic manually.")
    else:
        wait_ready(kube, args.namespace, expected)
    summary["applied"] = True
    private_write(folder / "result.json", json.dumps(summary, indent=2).encode())
    print("State/template change complete. Re-enrollment and two-client public-network acceptance remain required.")


if __name__ == "__main__":
    try:
        main()
    except Exception:
        print("Migration stopped. Check private plan/backup and node state using the independent management path; do not start clients or blindly repeat --apply.", file=sys.stderr)
        sys.exit(1)
